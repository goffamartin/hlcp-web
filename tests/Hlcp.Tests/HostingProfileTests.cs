using System.Net;
using Hlcp.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Hlcp.Tests;

public sealed class HostingProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://hlcp.home.goffa.cz")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://hlcp.home.goffa.cz/path")]
    [InlineData("https://user:secret@hlcp.home.goffa.cz")]
    [InlineData("https://hlcp.home.goffa.cz:8443")]
    public void InvalidPublicOriginCannotEnableProduction(string? origin) => Assert.Throws<InvalidOperationException>(() => HostingProfile.ParseOrigin(origin));

    [Fact]
    public void ProductionRequiresExternalStateAndRejectsUnrestrictedForwardingOrCustomBindings()
    {
        var builder = Builder();
        builder.Configuration["Storage:Directory"] = builder.Environment.ContentRootPath;
        Assert.Throws<InvalidOperationException>(() => HostingProfile.Configure(builder));
        builder = Builder(); builder.Configuration["ForwardedHeaders_Enabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => HostingProfile.Configure(builder));
        builder = Builder(); builder.Configuration["Kestrel:Endpoints:Unsafe:Url"] = "http://0.0.0.0:5088";
        Assert.Throws<InvalidOperationException>(() => HostingProfile.Configure(builder));
    }

    [Theory]
    [InlineData("127.0.0.1", "https", "hlcp.home.goffa.cz", 200)]
    [InlineData("127.0.0.1", "http", "hlcp.home.goffa.cz", 400)]
    [InlineData("127.0.0.1", null, "hlcp.home.goffa.cz", 400)]
    [InlineData("192.168.0.20", "https", "hlcp.home.goffa.cz", 400)]
    [InlineData("127.0.0.1", "https", "wrong.example", 400)]
    public async Task OnlyLocalProxyWithCanonicalHttpsOriginIsAccepted(string remote, string? forwarded, string host, int expected)
    {
        var builder = Builder();
        builder.WebHost.UseTestServer();
        var profile = HostingProfile.Configure(builder);
        Assert.True(profile.Production);
        await using var app = builder.Build();
        profile.Use(app);
        app.MapGet("/probe", () => Results.Ok());
        await app.StartAsync();
        var context = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            context.Request.Scheme = "http";
            context.Request.Method = "GET";
            context.Request.Host = new HostString(host);
            context.Request.Path = "/probe";
            if (forwarded is not null) context.Request.Headers["X-Forwarded-Proto"] = forwarded;
        });
        Assert.Equal(expected, context.Response.StatusCode);
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:PublicOrigin"] = "https://hlcp.home.goffa.cz",
            ["Storage:Directory"] = Path.Combine(Path.GetTempPath(), "hlcp-production-state"),
            ["Logging:LogLevel:Default"] = "Error"
        });
        return builder;
    }
}
