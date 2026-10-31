using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Hlcp.Web.Security;

public sealed record HostingProfile(Uri? PublicOrigin)
{
    public bool Production => PublicOrigin is not null;

    public static HostingProfile Configure(WebApplicationBuilder builder)
    {
        // The backend always stays on this host. Never inherit a wildcard URL from the environment.
        builder.WebHost.UseUrls("http://127.0.0.1:5088");
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("Custom Kestrel endpoints are not supported. The backend must stay on loopback.");
        if (builder.Environment.IsDevelopment()) return new((Uri?)null);
        if (!builder.Environment.IsProduction()) throw new InvalidOperationException("Use Development or the explicit Production reverse-proxy profile.");
        if (new[] { "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "DOTNET_FORWARDEDHEADERS_ENABLED" }.Any(name =>
            string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase)) ||
            builder.Configuration.GetValue<bool>("ForwardedHeaders_Enabled"))
            throw new InvalidOperationException("Automatic forwarded headers are forbidden; only the configured local proxy is trusted.");
        var origin = ParseOrigin(builder.Configuration["Hosting:PublicOrigin"]);
        var storage = builder.Configuration["Storage:Directory"];
        if (string.IsNullOrWhiteSpace(storage) || !Path.IsPathFullyQualified(storage))
            throw new InvalidOperationException("Production requires an absolute Storage:Directory outside the application directory.");
        var relative = Path.GetRelativePath(builder.Environment.ContentRootPath, Path.GetFullPath(storage));
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative))
            throw new InvalidOperationException("Production storage must be outside the application directory.");
        builder.Configuration["AllowedHosts"] = origin.IdnHost;
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
        });
        builder.Services.AddHsts(options => { options.MaxAge = TimeSpan.FromDays(7); options.IncludeSubDomains = false; });
        return new(origin);
    }

    public static Uri ParseOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns ||
            uri.Port != 443 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Production requires an explicit HTTPS DNS origin on port 443, without a path or credentials.");
        return uri;
    }

    public void Use(WebApplication app)
    {
        if (!Production) return;
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Web požadavek nedokončil. Zkontroluj provozní log služby.");
        }));
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, PublicOrigin!.Authority, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; return; }
            await next();
        });
        app.UseHsts();
    }
}
