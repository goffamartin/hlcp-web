using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hlcp.Application;

public sealed class HlcpLogClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 12 };
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly IHlcpTokenSource tokens;
    private readonly TimeSpan idleTimeout;

    public HlcpLogClient(HttpClient http, Uri endpoint, IHlcpTokenSource tokens, TimeSpan? idleTimeout = null)
    {
        HlcpEndpoint.Validate(endpoint);
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(15);
        if (this.idleTimeout < TimeSpan.FromMilliseconds(50) || this.idleTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        (this.http, this.endpoint, this.tokens) = (http, endpoint, tokens);
    }

    public async Task<LogPage> HistoryAsync(string node, string resource, LogQuery? query = null, string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var path = PathFor(node, resource, false, query, cursor, limit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await OpenAsync(path, "application/json", deadline.Token, cancellationToken);
        if (response.Content.Headers.ContentLength > 256 * 1024) throw new LogReadException(LogFailure.ByteLimit);
        await using var stream = await GuardAsync(() => response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken);
        using var body = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await GuardAsync(async () => await stream.ReadAsync(bytes, deadline.Token), cancellationToken)) > 0)
        {
            if (body.Length + count > 256 * 1024) throw new LogReadException(LogFailure.ByteLimit);
            body.Write(bytes, 0, count);
        }
        var page = Deserialize<LogPage>(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        if (page.SchemaVersion != 1 || page.StreamSemantics != "runtime-pipes" || page.CapturedAt == default || !LogContract.Identifier(page.SourceSession) ||
            page.Records is null || page.Records.Length > limit || page.Records.Any(record => !LogContract.Record(record)) ||
            !LogContract.Code(page.Code) || page.Code != "OK" && !page.Gap ||
            page.NextCursor is { Length: > 0 } && !ValidCursor(page.NextCursor)) throw new LogReadException(LogFailure.InvalidResponse);
        return page;
    }

    public async IAsyncEnumerable<LogEvent> FollowAsync(string node, string resource, LogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = PathFor(node, resource, true, query, null, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(15));
        using var response = await OpenAsync(path, "application/x-ndjson", deadline.Token, cancellationToken);
        await using var stream = await GuardAsync(() => response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken);
        var reader = new BoundedLines(stream, idleTimeout, deadline.Token, cancellationToken);
        var sequence = new LogSequence();
        while (await reader.ReadAsync() is { } line)
        {
            var item = Deserialize<LogEvent>(line);
            sequence.Accept(item);
            yield return item;
            if (item.Kind == "end") yield break;
        }
        throw new LogReadException(LogFailure.Interrupted); // EOF is never a successful end event.
    }

    private async Task<HttpResponseMessage> OpenAsync(string path, string mediaType, CancellationToken operation, CancellationToken caller)
    {
        string token;
        try { token = await tokens.ReadAsync(operation); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new LogReadException(LogFailure.Timeout); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HlcpReadException) { throw new LogReadException(LogFailure.CredentialUnavailable); }
        if (token is null || token.Length is < 32 or > 4096 || token.Any(c => c <= 0x20 || c >= 0x7f)) throw new LogReadException(LogFailure.CredentialUnavailable);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        catch (FormatException) { throw new LogReadException(LogFailure.CredentialUnavailable); }
        using var header = CancellationTokenSource.CreateLinkedTokenSource(operation);
        header.CancelAfter(TimeSpan.FromSeconds(25));
        var response = await GuardAsync(() => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, header.Token), caller);
        try
        {
            if (!response.IsSuccessStatusCode) throw new LogReadException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LogFailure.Forbidden,
                HttpStatusCode.NotFound => LogFailure.NotFound,
                HttpStatusCode.Gone => LogFailure.CursorExpired,
                HttpStatusCode.TooManyRequests => LogFailure.Capacity,
                HttpStatusCode.BadRequest => LogFailure.InvalidQuery,
                HttpStatusCode.UnprocessableEntity => LogFailure.Unsupported,
                HttpStatusCode.GatewayTimeout => LogFailure.Timeout,
                _ => LogFailure.Unavailable
            });
            if (response.Content.Headers.ContentType?.MediaType != mediaType) throw new LogReadException(LogFailure.InvalidResponse);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static string PathFor(string node, string resource, bool follow, LogQuery? query, string? cursor, int? limit)
    {
        if (!LogContract.Identifier(node) || !LogContract.Identifier(resource)) throw new LogReadException(LogFailure.InvalidQuery);
        var values = new List<(string Key, string Value)>();
        if (cursor is not null)
        {
            if (follow || query is not null || !ValidCursor(cursor)) throw new LogReadException(LogFailure.InvalidQuery);
            values.Add(("cursor", cursor));
        }
        else
        {
            query ??= new();
            query.Validate();
            values.Add(("tail", query.Tail.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            values.Add(("stream", query.Stream));
            if (query.Contains.Length > 0) values.Add(("contains", query.Contains));
            if (query.Since.Length > 0) values.Add(("since", query.Since));
            if (query.Until.Length > 0) values.Add(("until", query.Until));
        }
        if (!follow)
        {
            if (limit is not (>= 1 and <= 200)) throw new LogReadException(LogFailure.InvalidQuery);
            values.Add(("limit", limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return "v1/nodes/" + Uri.EscapeDataString(node) + "/resources/" + Uri.EscapeDataString(resource) + "/logs" + (follow ? "/stream" : "") + "?" +
            string.Join("&", values.Select(item => item.Key + "=" + Uri.EscapeDataString(item.Value)));
    }
    private static bool ValidCursor(string value) => value is { Length: > 0 and <= 160 } && value.All(c => c > 0x20 && c < 0x7f);
    private static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new LogReadException(LogFailure.InvalidResponse); }
        catch (JsonException) { throw new LogReadException(LogFailure.InvalidResponse); }
    }
    private static async Task<T> GuardAsync<T>(Func<Task<T>> call, CancellationToken caller)
    {
        try { return await call(); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new LogReadException(LogFailure.Timeout); }
        catch (HttpRequestException) { throw new LogReadException(LogFailure.Unavailable); }
        catch (IOException) { throw new LogReadException(LogFailure.Unavailable); }
    }

    private sealed class BoundedLines(Stream stream, TimeSpan idle, CancellationToken operation, CancellationToken caller)
    {
        private readonly byte[] input = new byte[8192];
        private int position, available, total;
        public async Task<byte[]?> ReadAsync()
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (position == available)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation);
                    timeout.CancelAfter(idle);
                    available = await GuardAsync(async () => await stream.ReadAsync(input, timeout.Token), caller);
                    position = 0;
                    total += available;
                    if (total > 8 * 1024 * 1024) throw new LogReadException(LogFailure.ByteLimit);
                    if (available == 0) return line.WrittenCount == 0 ? null : line.WrittenSpan.ToArray();
                }
                var newline = Array.IndexOf(input, (byte)'\n', position, available - position);
                var end = newline < 0 ? available : newline;
                if (line.WrittenCount + end - position > 64 * 1024) throw new LogReadException(LogFailure.ByteLimit);
                line.Write(input.AsSpan(position, end - position));
                position = newline < 0 ? available : newline + 1;
                if (newline >= 0) return line.WrittenSpan.ToArray();
            }
        }
    }
}

internal sealed class LogSequence
{
    private string? id;
    private ulong sequence;
    public void Accept(LogEvent item)
    {
        if (item.SchemaVersion != 1 || item.StreamSemantics != "runtime-pipes" || !LogContract.Identifier(item.RequestId, 64) || id is not null && id != item.RequestId)
            throw new LogReadException(LogFailure.InvalidSequence);
        id ??= item.RequestId;
        if (item.Kind == "keepalive" && item.Sequence == 0 && item.Record is null && string.IsNullOrEmpty(item.Code) && !item.Gap) return;
        if (item.Kind == "end" && item.Sequence == 0 && item.Record is null && item.Gap && LogContract.Code(item.Code)) return;
        if (sequence == ulong.MaxValue || item.Sequence != sequence + 1) throw new LogReadException(LogFailure.InvalidSequence);
        var valid = item.Kind switch
        {
            "start" => sequence == 0 && item.Record is null && string.IsNullOrEmpty(item.Code) && !item.Gap,
            "record" => sequence > 0 && LogContract.Record(item.Record) && string.IsNullOrEmpty(item.Code) && !item.Gap,
            "end" => item.Record is null && LogContract.Code(item.Code) && (item.Code == "OK" ? sequence > 0 : item.Gap),
            _ => false
        };
        if (!valid) throw new LogReadException(LogFailure.InvalidSequence);
        sequence = item.Sequence;
    }
}
