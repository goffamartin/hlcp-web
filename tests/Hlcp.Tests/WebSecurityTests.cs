using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlcp.Application;
using Hlcp.Web.Security;
using Hlcp.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace Hlcp.Tests;

public sealed class WebSecurityTests
{
    internal const string TestPassword = "Synthetic test password only 123";

    [Fact]
    public async Task AnonymousRequestsCannotReachDataOrReadControllerCredential()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/account/login", page.Headers.Location!.ToString());
        Assert.Equal(0, factory.Controller.Calls);
        var reader = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        using (reader)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ServiceProvider.GetRequiredService<DashboardReader>().ReadAsync(new ClaimsPrincipal()));
    }

    [Fact]
    public async Task LoginRequiresAntiforgeryAndWrongPasswordDoesNotEchoInput()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SetPasswordAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestPassword }))).StatusCode);
        var csrf = await TokenAsync(client, "/account/login");
        const string wrong = "Wrong synthetic password <secret>";
        var rejected = await client.PostAsync("/account/login", Form(csrf, wrong));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.DoesNotContain(wrong, await rejected.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Controller.Calls);
    }

    [Fact]
    public async Task EmptyLoginIsRefusedAndCrossOriginCircuitConnectionIsBlocked()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await TokenAsync(client, "/account/login");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/account/login", Form(csrf, ""))).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(0, factory.Controller.Calls);
    }

    [Fact]
    public async Task LoginReadsScopedSnapshotAndLogoutRevokesCapturedCircuitPrincipal()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        await factory.SetPasswordAsync();
        var login = await client.PostAsync("/account/login", Form(await TokenAsync(client, "/account/login"), TestPassword));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        var response = await client.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("synthetic-node", payload);
        Assert.DoesNotContain(TestWeb.Token, payload);
        var csrf = await TokenAsync(client, "/");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task PasswordRotationRevokesExistingSessionAndNoCredentialLeaksOnFailure()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SetPasswordAsync();
        await client.PostAsync("/account/login", Form(await TokenAsync(client, "/account/login"), TestPassword));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard")).StatusCode);
        await factory.Services.GetRequiredService<AdminCredentialFile>().WriteAsync("New synthetic password only 456");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task GlobalLoginRateLimitRejectsRepeatedAttempts()
    {
        await using var factory = new TestWeb();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        HttpResponseMessage? response = null;
        for (var index = 0; index < 11; index++) response = await client.GetAsync("/account/login");
        Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
    }

    [Fact]
    public async Task SessionsExpireRevokeAndDoNotSurviveServerRestart()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        await factory.SetPasswordAsync();
        var credential = factory.Services.GetRequiredService<AdminCredentialFile>();
        var clock = new TestClock();
        var sessions = new AdminSessions(credential, clock);
        var principal = sessions.Create((await credential.VersionAsync(default))!)!;
        Assert.True(await sessions.IsValidAsync(principal));
        Assert.False(await new AdminSessions(credential, clock).IsValidAsync(principal));
        sessions.Revoke(principal);
        Assert.False(await sessions.IsValidAsync(principal));
        principal = sessions.Create((await credential.VersionAsync(default))!)!;
        clock.Now += TimeSpan.FromHours(8);
        Assert.False(await sessions.IsValidAsync(principal));
    }

    internal static FormUrlEncodedContent Form(string csrf, string password) => new(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["Password"] = password });
    internal static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token must be rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}

internal sealed class TestWeb(bool withOperator = true) : WebApplicationFactory<Program>
{
    public const string Token = "synthetic-observer-token-0000000000000000";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hlcp-web-test-" + Guid.NewGuid().ToString("N"));
    public SyntheticController Controller { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "observer.token"), Token);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Directory"] = Root, ["Admin:PasswordFile"] = Path.Combine(Root, "admin.hash"),
            ["Hlcp:TokenFile"] = Path.Combine(Root, "observer.token"), ["Hlcp:OperatorTokenFile"] = withOperator ? Path.Combine(Root, "observer.token") : null,
            ["Hlcp:Endpoint"] = "http://127.0.0.1:19444", ["Logging:LogLevel:Default"] = "Error"
        }));
        builder.ConfigureServices(services =>
        {
            // Minimal-host bootstrap settings are evaluated before this factory callback.
            // Replace every stateful service explicitly, so factories never share account/journal data.
            services.AddSingleton(new AdminCredentialFile(Path.Combine(Root, "admin.hash")));
            services.AddSingleton(new OperationJournal(Path.Combine(Root, "operations")));
            services.AddDataProtection().SetApplicationName(Root).PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Root, "keys")));
            services.AddHttpClient("Hlcp").ConfigurePrimaryHttpMessageHandler(() => Controller);
        });
    }
    public Task SetPasswordAsync() => Services.GetRequiredService<AdminCredentialFile>().WriteAsync(WebSecurityTests.TestPassword);
    public async Task<ClaimsPrincipal> PrincipalAsync()
    {
        await SetPasswordAsync();
        var version = await Services.GetRequiredService<AdminCredentialFile>().VersionAsync(default);
        return Services.GetRequiredService<AdminSessions>().Create(version!)!;
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class SyntheticController : HttpMessageHandler
{
    public int Calls;
    public int Submissions;
    public bool FailSubmission;
    public bool ForbidReads;
    public bool DenyAction;
    private ResourceCommand? last;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Assert.Equal(TestWeb.Token, request.Headers.Authorization?.Parameter);
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/v1/nodes")
        {
            if (ForbidReads) return new(HttpStatusCode.Forbidden) { Content = new StringContent("private upstream diagnostic " + TestWeb.Token) };
            return Reply(new[] { new NodeSnapshot { NodeId = "synthetic-node", State = "online", Inventory = new() { Revision = 1, Resources = [Resource()] }, Resources = new() { InventoryRevision = 1, Resources = [new() { ResourceId = "synthetic-container", State = "running", ObservedAt = DateTimeOffset.UtcNow }] } } });
        }
        if (path.EndsWith("/resources")) return Reply(DenyAction ? Array.Empty<ResourceDescriptor>() : new[] { Resource() });
        if (path.EndsWith("/resolve"))
        {
            var command = (await request.Content!.ReadFromJsonAsync<ResourceCommand>(Json, cancellationToken))!;
            return Reply(new CommandPreview { Node = "synthetic-node", Resource = command.ResourceId, Action = command.Action, Command = command });
        }
        if (path.EndsWith("/commands") && request.Method == HttpMethod.Post)
        {
            last = await request.Content!.ReadFromJsonAsync<ResourceCommand>(Json, cancellationToken);
            Interlocked.Increment(ref Submissions);
            if (FailSubmission) throw new HttpRequestException("Synthetic connection lost after admission");
            return Reply(new CommandResult { CommandId = last!.CommandId, Success = true, Code = "OK" });
        }
        if (path.Contains("/commands/"))
        {
            if (last is null) return new(HttpStatusCode.NotFound);
            return Reply(new CommandProgress { Command = last, State = "completed", Result = new() { CommandId = last.CommandId, Success = true, Code = "OK" } });
        }
        throw new InvalidOperationException("Unexpected synthetic API path.");
    }
    private static ResourceDescriptor Resource() => new() { Id = "synthetic-container", Type = "container", Actions = ["status", "restart", "logs"], Tags = ["synthetic", "production"] };
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
}
