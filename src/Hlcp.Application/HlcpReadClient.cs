using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hlcp.Application;

public interface IHlcpTokenSource
{
    ValueTask<string> ReadAsync(CancellationToken cancellationToken);
}

public enum HlcpFailure
{
    Unavailable, Timeout, AccessDenied, NotFound, RateLimited,
    InvalidResponse, ResponseTooLarge, UnsupportedTagFiltering, CredentialUnavailable
}

// Fixed codes/messages deliberately omit response bodies, tokens and secret paths.
public sealed class HlcpReadException(HlcpFailure failure) : Exception($"HLCP read failed: {failure}.")
{
    public HlcpFailure Failure { get; } = failure;
}

public sealed class HlcpReadClient
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 16
    };
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly IHlcpTokenSource tokens;
    private readonly TimeSpan timeout;

    public HlcpReadClient(HttpClient http, Uri endpoint, IHlcpTokenSource tokens, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tokens);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || !endpoint.IsLoopback ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("The controller endpoint must be a plain loopback HTTP origin.", nameof(endpoint));
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout < TimeSpan.FromMilliseconds(50) || this.timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        (this.http, this.endpoint, this.tokens) = (http, endpoint, tokens);
    }

    // Use this handler for real calls; tests supply an isolated synthetic handler.
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    };

    public async Task<IReadOnlyList<NodeSnapshot>> GetNodesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await GetAsync<NodeSnapshot[]>("v1/nodes", false, cancellationToken);
        if (nodes.Any(node => node is null || !ValidIdentifier(node.NodeId) || string.IsNullOrEmpty(node.State) || node.Inventory is null || node.Resources is null) ||
            nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw new HlcpReadException(HlcpFailure.InvalidResponse);
        foreach (var node in nodes)
        {
            ValidateResources(node.Inventory.Resources ?? []);
            if ((node.Resources.Resources ?? []).Any(state => state is null || !ValidIdentifier(state.ResourceId) || string.IsNullOrEmpty(state.State)))
                throw new HlcpReadException(HlcpFailure.InvalidResponse);
        }
        return nodes;
    }

    public async Task<IReadOnlyList<ResourceDescriptor>> GetResourcesAsync(string node, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        var selected = ResourceTags.Validate(tags);
        var resources = await GetAsync<ResourceDescriptor[]>(ResourcePath(node, "resources", selected), selected.Length != 0, cancellationToken);
        ValidateResources(resources);
        if (resources.Any(resource => !ResourceTags.Matches(resource.Tags, selected)))
            throw new HlcpReadException(HlcpFailure.InvalidResponse);
        return resources;
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string node, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<string[]>(ResourcePath(node, "tags", ResourceTags.Validate(tags)), false, cancellationToken);
        if (result.Length > 4096 || result.Any(tag => !ResourceTags.IsValid(tag)) || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new HlcpReadException(HlcpFailure.InvalidResponse);
        return result;
    }

    private async Task<T> GetAsync<T>(string path, bool requireTags, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            string token;
            try { token = await tokens.ReadAsync(deadline.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new HlcpReadException(HlcpFailure.CredentialUnavailable); }
            if (token is null || token.Length is < 32 or > 4096 || token.Any(c => c <= 0x20 || c >= 0x7f))
                throw new HlcpReadException(HlcpFailure.CredentialUnavailable);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
            try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
            catch (FormatException) { throw new HlcpReadException(HlcpFailure.CredentialUnavailable); }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new HlcpReadException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HlcpFailure.AccessDenied,
                    HttpStatusCode.NotFound => HlcpFailure.NotFound,
                    HttpStatusCode.TooManyRequests => HlcpFailure.RateLimited,
                    _ => HlcpFailure.Unavailable
                });
            if (requireTags && (!response.Headers.TryGetValues("X-HLCP-Resource-Tags", out var values) || !values.SequenceEqual(["1"])))
                throw new HlcpReadException(HlcpFailure.UnsupportedTagFiltering);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new HlcpReadException(HlcpFailure.ResponseTooLarge);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new HlcpReadException(HlcpFailure.InvalidResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (body.Length + count > MaximumResponseBytes)
                    throw new HlcpReadException(HlcpFailure.ResponseTooLarge);
                body.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), Json)
                ?? throw new HlcpReadException(HlcpFailure.InvalidResponse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HlcpReadException(HlcpFailure.Timeout); }
        catch (HttpRequestException) { throw new HlcpReadException(HlcpFailure.Unavailable); }
        catch (IOException) { throw new HlcpReadException(HlcpFailure.Unavailable); }
        catch (JsonException) { throw new HlcpReadException(HlcpFailure.InvalidResponse); }
    }

    private static string ResourcePath(string node, string suffix, string[] tags)
    {
        if (!ValidIdentifier(node)) throw new ArgumentException("Invalid node identifier.", nameof(node));
        return "v1/nodes/" + Uri.EscapeDataString(node) + "/" + suffix +
            (tags.Length == 0 ? "" : "?" + string.Join("&", tags.Select(tag => "tag=" + Uri.EscapeDataString(tag))));
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value is not "." and not ".." && value.All(c => c > 0x20 && c < 0x7f);

    private static void ValidateResources(ResourceDescriptor[] resources)
    {
        if (resources.Length > 256 || resources.Any(resource => resource is null || !ValidIdentifier(resource.Id) || !ValidIdentifier(resource.Type) ||
            resource.Actions is not { Length: > 0 and <= 32 } || resource.Actions.Any(action => !ValidIdentifier(action)) ||
            resource.Tags is { Length: > 16 } || (resource.Tags ?? []).Any(tag => !ResourceTags.IsValid(tag)) ||
            (resource.Tags ?? []).Distinct(StringComparer.Ordinal).Count() != (resource.Tags?.Length ?? 0)) ||
            resources.Select(resource => resource.Id).Distinct(StringComparer.Ordinal).Count() != resources.Length)
            throw new HlcpReadException(HlcpFailure.InvalidResponse);
    }
}
