using System.Net;
using System.Text;
using System.Text.Json;
using Hlcp.Application;

namespace Hlcp.Tests;

public sealed class HlcpLogClientTests
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal static LogEvent Event(string kind, ulong sequence, string? code = null, bool gap = false, LogRecord? record = null, string id = "read-1") => new()
    { SchemaVersion = 1, StreamSemantics = "runtime-pipes", RequestId = id, Sequence = sequence, Kind = kind, Code = code, Gap = gap, Record = record };
    internal static LogRecord Record(string text = "example", bool truncated = false) => new() { Timestamp = "2026-10-30T12:00:00.123456789Z", Stream = "stdout", Text = text, Truncated = truncated };
    internal static string Lines(params LogEvent[] events) => string.Join("\n", events.Select(item => JsonSerializer.Serialize(item, Json))) + "\n";
    internal static LogPage Page(string? cursor = null) => new() { SchemaVersion = 1, StreamSemantics = "runtime-pipes", CapturedAt = DateTimeOffset.Parse("2026-10-30T12:00:00Z"), SourceSession = "session-1", Records = [Record()], Code = "OK", Gap = false, Truncated = false, NextCursor = cursor };

    [Fact]
    public async Task HistoryUsesOnlyGetWithBoundedTypedFiltersAndOpaqueCursor()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("/v1/nodes/n%2F1/resources/r%2F1/logs", request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            if (query.Contains("cursor=")) { Assert.Equal("?cursor=opaque_01&limit=50", query); }
            else
            {
                Assert.Contains("contains=%3Ctest%3E%20%26%20text", query);
                Assert.Contains("since=2026-10-30T08%3A00%3A00%2B01%3A00", query);
                Assert.Contains("tail=500", query);
            }
            return Task.FromResult(Reply(JsonSerializer.Serialize(Page("opaque_01"), Json), "application/json"));
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var page = await client.HistoryAsync("n/1", "r/1", new(500, "stderr", "<test> & text", "2026-10-30T08:00:00+01:00"), limit: 50);
        Assert.Equal("opaque_01", page.NextCursor);
        await client.HistoryAsync("n/1", "r/1", cursor: page.NextCursor, limit: 50);
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<LogReadException>(() => client.HistoryAsync("n", "r", new(), "opaque_01"));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(-1, "both", "", "", "")]
    [InlineData(1001, "both", "", "", "")]
    [InlineData(100, "shell", "", "", "")]
    [InlineData(100, "both", "\0", "", "")]
    [InlineData(100, "both", "", "10m", "")]
    [InlineData(100, "both", "", "2026-10-30T08:00:00", "")]
    [InlineData(100, "both", "", "2026-10-30T09:00:00Z", "2026-10-30T08:00:00Z")]
    public async Task InvalidQueryCannotReachHttp(int tail, string stream, string text, string since, string until)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not call HTTP."));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LogReadException>(() => Client(http).HistoryAsync("n", "r", new(tail, stream, text, since, until)));
        Assert.Equal(LogFailure.InvalidQuery, error.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void QueryTextLimitIsUtf8BytesRatherThanCharacters()
    {
        Assert.Throws<LogReadException>(() => new LogQuery(Contains: new string('č', 129)).Validate());
        new LogQuery(Contains: new string('č', 128)).Validate();
    }

    [Fact]
    public async Task FollowPreservesSequenceAroundControllerKeepalivesAndRequiresExplicitEnd()
    {
        var body = Lines(Event("keepalive", 0), Event("start", 1), Event("record", 2, record: Record("<script>not HTML</script>")), Event("keepalive", 0), Event("end", 3, "OK"));
        using var http = Http(body);
        var events = await CollectAsync(Client(http).FollowAsync("n", "r", new(0)));
        Assert.Equal(5, events.Count);
        Assert.Equal("<script>not HTML</script>", events[2].Record!.Text);
        Assert.Equal("OK", events[^1].Code);
        Assert.False(events[^1].Gap);
    }

    [Theory]
    [InlineData("BYTE_LIMIT")]
    [InlineData("TIME_LIMIT")]
    [InlineData("BACKPRESSURE")]
    [InlineData("SESSION_LOST")]
    [InlineData("FORBIDDEN")]
    [InlineData("RUNTIME_FAILED")]
    public async Task ControllerLocalEndsCarryGapAndMustNotBecomeHealthyCompletion(string code)
    {
        using var http = Http(Lines(Event("start", 1), Event("end", 0, code, true)));
        var events = await CollectAsync(Client(http).FollowAsync("n", "r", new()));
        Assert.Equal(code, events[^1].Code);
        Assert.True(events[^1].Gap);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("foreign-id")]
    [InlineData("duplicate-start")]
    [InlineData("unknown-kind")]
    [InlineData("bad-end")]
    [InlineData("record-first")]
    [InlineData("bad-schema")]
    [InlineData("bad-semantics")]
    public async Task InvalidStreamCannotBePresentedAsComplete(string scenario)
    {
        var bad = scenario switch
        {
            "gap" => Event("record", 3, record: Record()),
            "foreign-id" => Event("record", 2, record: Record(), id: "another-read"),
            "duplicate-start" => Event("start", 2),
            "unknown-kind" => Event("exec", 2),
            "bad-end" => Event("end", 2, "FORBIDDEN", false),
            "record-first" => Event("record", 1, record: Record()),
            "bad-schema" => Event("record", 2, record: Record()) with { SchemaVersion = 2 },
            _ => Event("record", 2, record: Record()) with { StreamSemantics = "terminal" }
        };
        using var http = Http(scenario == "record-first" ? Lines(bad) : Lines(Event("start", 1), bad));
        var error = await Assert.ThrowsAsync<LogReadException>(() => CollectAsync(Client(http).FollowAsync("n", "r", new())));
        Assert.Equal(LogFailure.InvalidSequence, error.Failure);
    }

    [Fact]
    public async Task EofAfterRecordsRemainsInterrupted()
    {
        using var http = Http(Lines(Event("start", 1), Event("record", 2, record: Record())));
        var error = await Assert.ThrowsAsync<LogReadException>(() => CollectAsync(Client(http).FollowAsync("n", "r", new())));
        Assert.Equal(LogFailure.Interrupted, error.Failure);
    }

    [Fact]
    public async Task OversizedLineAndMalformedJsonAreBoundedFailures()
    {
        using var huge = Http(new string('x', 64 * 1024 + 1));
        var error = await Assert.ThrowsAsync<LogReadException>(() => CollectAsync(Client(huge).FollowAsync("n", "r", new())));
        Assert.Equal(LogFailure.ByteLimit, error.Failure);
        using var malformed = Http("{\"secret\": unparseable}\n");
        error = await Assert.ThrowsAsync<LogReadException>(() => CollectAsync(Client(malformed).FollowAsync("n", "r", new())));
        Assert.Equal(LogFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task AggregateLiveBytesAreBoundedEvenWhenEveryRecordIsValid()
    {
        var body = new StringBuilder(Lines(Event("start", 1)));
        var text = new string('x', 4096);
        for (ulong sequence = 2; sequence < 2100; sequence++) body.Append(Lines(Event("record", sequence, record: Record(text))));
        using var http = Http(body.ToString());
        async Task Consume() { await foreach (var _ in Client(http).FollowAsync("n", "r", new())) { } }
        var error = await Assert.ThrowsAsync<LogReadException>(Consume);
        Assert.Equal(LogFailure.ByteLimit, error.Failure);
    }

    [Fact]
    public async Task UnknownLengthHistoryIsBoundedAndItsBodyIsClosed()
    {
        var body = new StallingLogStream(new string(' ', 300 * 1024));
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = StreamReply(body);
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<LogReadException>(() => Client(http).HistoryAsync("n", "r"));
        Assert.Equal(LogFailure.ByteLimit, error.Failure);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task InvalidCredentialCannotReachLogEndpoint()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        var client = new HlcpLogClient(http, new("http://127.0.0.1:9444"), new Token("invalid\ncredential"));
        var error = await Assert.ThrowsAsync<LogReadException>(() => client.HistoryAsync("n", "r"));
        Assert.Equal(LogFailure.CredentialUnavailable, error.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task IdleTimeoutDiffersFromCallerCancellationAndClosesBody()
    {
        var stream = new StallingLogStream(Lines(Event("start", 1)));
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(StreamReply(stream))));
        var client = Client(http, TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<LogReadException>(() => CollectAsync(client.FollowAsync("n", "r", new())));
        Assert.Equal(LogFailure.Timeout, error.Failure);
        Assert.True(stream.Disposed);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(client.FollowAsync("n", "r", new(), stop.Token)));
    }

    [Theory]
    [InlineData(403, LogFailure.Forbidden)]
    [InlineData(410, LogFailure.CursorExpired)]
    [InlineData(429, LogFailure.Capacity)]
    [InlineData(422, LogFailure.Unsupported)]
    [InlineData(500, LogFailure.Unavailable)]
    public async Task HttpFailureDoesNotEchoBodyOrToken(int status, LogFailure expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret token /private/path") })));
        var error = await Assert.ThrowsAsync<LogReadException>(() => Client(http).HistoryAsync("n", "r"));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task TruncatedAndPartialHistoryFlagsRemainVisibleAndBadRecordsAreRejected()
    {
        var page = Page() with { Truncated = true, Gap = true, Code = "BYTE_LIMIT", Records = [Record(truncated: true)] };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(JsonSerializer.Serialize(page, Json), "application/json"))));
        var result = await Client(http).HistoryAsync("n", "r");
        Assert.True(result.Gap);
        Assert.True(result.Truncated);
        Assert.True(result.Records[0].Truncated);
        page = page with { Records = [Record(new string('x', 4097))] };
        var error = await Assert.ThrowsAsync<LogReadException>(() => Client(http).HistoryAsync("n", "r"));
        Assert.Equal(LogFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public void BufferDropsOldRowsAndEnforcesBothCountAndMemoryBudgets()
    {
        var buffer = new LogBuffer();
        for (var index = 0; index < 600; index++) buffer.Add(Record(index.ToString()));
        Assert.Equal(500, buffer.Records.Count);
        Assert.Equal(100, buffer.Removed);
        Assert.Equal("100", buffer.Records[0].Text);
        buffer.Clear();
        for (var index = 0; index < 500; index++) buffer.Add(Record(new string('x', 4096), truncated: true));
        Assert.True(buffer.Bytes <= LogBuffer.MaximumBytes);
        Assert.True(buffer.Records.Count < 500);
        Assert.True(buffer.HasTruncatedRecords);
        Assert.DoesNotContain('\u001b', LogBuffer.Display("\u001b[31mred\r\n"));
    }

    private static HlcpLogClient Client(HttpClient http, TimeSpan? idle = null) => new(http, new("http://127.0.0.1:9444"), new Token(), idle);
    private static HttpClient Http(string body) => new(new Handler((request, _) =>
    { Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/logs/stream", request.RequestUri!.AbsolutePath); return Task.FromResult(Reply(body)); }));
    internal static HttpResponseMessage Reply(string body, string mediaType = "application/x-ndjson") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    internal static HttpResponseMessage StreamReply(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/x-ndjson");
        return new(HttpStatusCode.OK) { Content = content };
    }
    internal static async Task<List<LogEvent>> CollectAsync(IAsyncEnumerable<LogEvent> events)
    { var result = new List<LogEvent>(); await foreach (var item in events) result.Add(item); return result; }
    private sealed class Token(string? value = null) : IHlcpTokenSource { public ValueTask<string> ReadAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(value ?? new string('t', 32)); } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return callback(request, cancellationToken); }
    }
}

internal sealed class StallingLogStream(string prefix) : Stream
{
    private readonly byte[] bytes = Encoding.UTF8.GetBytes(prefix);
    private int offset;
    public bool Disposed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset < bytes.Length) { var count = Math.Min(buffer.Length, bytes.Length - offset); bytes.AsMemory(offset, count).CopyTo(buffer); offset += count; return count; }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
