using System.Net;
using System.Text;
using Hlcp.Application;

namespace Hlcp.Tests;

public sealed class HlcpReadClientTests
{
    private const string NodeJson = """
        [{"node_id":"debian-01","agent_version":"v1.1.0","state":"online","last_activity":"2026-10-30T12:00:00Z",
        "inventory":{"revision":18446744073709551615,"resources":[{"id":"app","type":"container","actions":["status"],"tags":["kostka"]}]},
        "resources":{"inventory_revision":18446744073709551615,"resources":[{"resource_id":"app","state":"running","stale":false,"observed_at":"2026-10-30T12:00:00Z"}]}}]
        """;

    [Theory]
    [InlineData("https://127.0.0.1:9444")]
    [InlineData("http://192.168.0.15:9444")]
    [InlineData("http://example.com")]
    [InlineData("http://user:password@127.0.0.1")]
    [InlineData("http://127.0.0.1/v1/nodes")]
    [InlineData("http://127.0.0.1?token=x")]
    [InlineData("http://127.0.0.1/#fragment")]
    public void RejectsNonLoopbackOrNonOriginEndpoints(string endpoint)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new HlcpReadClient(http, new Uri(endpoint), new TokenSource()));
    }

    [Fact]
    public void RealTransportDisablesRedirectsAndProxy()
    {
        using var handler = HlcpReadClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task NodesAreReadWithBearerAndPreserveInventoryPrecision()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:9444/v1/nodes", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(new string('t', 32), request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(NodeJson));
        });
        using var http = new HttpClient(handler);
        var nodes = await Client(http).GetNodesAsync();
        var node = Assert.Single(nodes);
        Assert.Equal(ulong.MaxValue, node.Inventory.Revision);
        Assert.Equal("kostka", Assert.Single(Assert.Single(node.Inventory.Resources!).Tags!));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ResourceFilterIsANDAndRequiresExplicitControllerAcknowledgement()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/v1/nodes/debian-01/resources", request.RequestUri!.AbsolutePath);
            Assert.Equal("?tag=kostka&tag=production", request.RequestUri.Query);
            var response = Json("""[{"id":"app","type":"container","actions":["status"],"tags":["kostka","production"]}]""");
            response.Headers.Add("X-HLCP-Resource-Tags", "1");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        Assert.Single(await Client(http).GetResourcesAsync("debian-01", ["kostka", "production"]));
    }

    [Fact]
    public async Task OldControllerCannotSilentlyReturnUnfilteredResources()
    {
        using var http = Http(_ => Json("""[{"id":"app","type":"container","actions":["status"]}]"""));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).GetResourcesAsync("n", ["kostka"]));
        Assert.Equal(HlcpFailure.UnsupportedTagFiltering, error.Failure);
    }

    [Fact]
    public async Task AcknowledgedButIncorrectFilterIsRejected()
    {
        using var http = Http(_ =>
        {
            var response = Json("""[{"id":"app","type":"container","actions":["status"],"tags":["medaile"]}]""");
            response.Headers.Add("X-HLCP-Resource-Tags", "1");
            return response;
        });
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).GetResourcesAsync("n", ["kostka"]));
        Assert.Equal(HlcpFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(401, HlcpFailure.AccessDenied)]
    [InlineData(403, HlcpFailure.AccessDenied)]
    [InlineData(404, HlcpFailure.NotFound)]
    [InlineData(429, HlcpFailure.RateLimited)]
    [InlineData(500, HlcpFailure.Unavailable)]
    [InlineData(302, HlcpFailure.Unavailable)]
    public async Task HTTPFailuresDoNotLeakResponseBody(int status, HlcpFailure failure)
    {
        using var http = Http(_ => Json("secret-response-body", (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).GetNodesAsync());
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"node_id\":\"n\",\"state\":\"online\"}]")]
    [InlineData("[null]")]
    public async Task InvalidResponsesAreNotPresentedAsEmptyHealthyState(string json)
    {
        using var http = Http(_ => Json(json));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).GetNodesAsync());
        Assert.Equal(HlcpFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task UnknownLengthBodyStillHasABoundedRead()
    {
        using var http = Http(_ =>
        {
            var content = new StreamContent(new NonSeekableStream(new byte[HlcpReadClient.MaximumResponseBytes + 1]));
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        });
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).GetNodesAsync());
        Assert.Equal(HlcpFailure.ResponseTooLarge, error.Failure);
    }

    [Fact]
    public async Task CallerCancellationAndRequestTimeoutAreDifferent()
    {
        using var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("[]");
        });
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetNodesAsync(cancel.Token));
        var client = new HlcpReadClient(http, new("http://127.0.0.1:9444"), new TokenSource(), TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => client.GetNodesAsync());
        Assert.Equal(HlcpFailure.Timeout, error.Failure);
    }

    [Fact]
    public async Task InvalidCredentialIsRejectedBeforeSending()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("[]")));
        using var http = new HttpClient(handler);
        var client = new HlcpReadClient(http, new("http://127.0.0.1:9444"), new TokenSource("invalid\ncredential"));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => client.GetNodesAsync());
        Assert.Equal(HlcpFailure.CredentialUnavailable, error.Failure);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("invalid", error.Message);
    }

    [Fact]
    public async Task TagCatalogAndPathsAreTypedAndReadOnly()
    {
        using var http = Http(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/nodes/node%2Fone/tags", request.RequestUri!.AbsolutePath);
            return Json("[\"kostka\",\"production\"]");
        });
        Assert.Equal(["kostka", "production"], await Client(http).GetTagsAsync("node/one"));
    }

    [Theory]
    [InlineData("{\"command_id\":\"foreign\",\"success\":true,\"code\":\"OK\"}")]
    [InlineData("{\"command_id\":\"op\",\"success\":false,\"code\":\"OK\"}")]
    [InlineData("{\"command_id\":\"op\",\"code\":\"OK\"}")]
    [InlineData("{\"command_id\":\"op\",\"success\":true,\"code\":\"OK\"}")]
    public async Task CommandResultsMustMatchIdAndSuccessContract(string body)
    {
        using var http = Http(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/nodes/n/commands", request.RequestUri!.AbsolutePath);
            return Json(body);
        });
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).SubmitAsync("n", new("op", "r", "restart")));
        Assert.Equal(HlcpFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task PreviewCannotSubstituteAnotherTarget()
    {
        using var http = Http(_ => Json("""{"node":"n","resource":"other","action":"restart","command":{"command_id":"op","resource_id":"r","action":"restart"}}"""));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).PreviewAsync("n", new("op", "r", "restart")));
        Assert.Equal(HlcpFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task NodeDetailUsesTargetedGetAndRejectsForeignNodeResponse()
    {
        using var parsed = System.Text.Json.JsonDocument.Parse(NodeJson);
        var node = parsed.RootElement[0].GetRawText();
        using var http = Http(request => { Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/v1/nodes/debian-01", request.RequestUri!.AbsolutePath); return Json(node); });
        Assert.Equal("debian-01", (await Client(http).GetNodeAsync("debian-01")).NodeId);
        using var foreign = Http(_ => Json(node));
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => Client(foreign).GetNodeAsync("different"));
        Assert.Equal(HlcpFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(2048, false)]
    [InlineData(0, false)]
    [InlineData(2049, true)]
    public async Task CommandOutputRespectsUtf8ProtocolLimit(int count, bool reject)
    {
        var message = new string('č', count);
        using var http = Http(_ => Json(System.Text.Json.JsonSerializer.Serialize(new { command_id = "op", success = true, code = "OK", message })));
        if (reject) Assert.Equal(HlcpFailure.InvalidResponse, (await Assert.ThrowsAsync<HlcpReadException>(() => Client(http).SubmitAsync("n", new("op", "r", "status")))).Failure);
        else Assert.Equal(message, (await Client(http).SubmitAsync("n", new("op", "r", "status"))).Message);
    }

    private static HlcpReadClient Client(HttpClient http) => new(http, new("http://127.0.0.1:9444"), new TokenSource());
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> callback) =>
        new(new Handler((request, _) => Task.FromResult(callback(request))));
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class TokenSource(string? token = null) : IHlcpTokenSource
    {
        public ValueTask<string> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token ?? new string('t', 32));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return callback(request, cancellationToken);
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
