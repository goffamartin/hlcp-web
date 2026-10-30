using System.Threading.RateLimiting;
using Hlcp.Application;
using Hlcp.Web.Security;
using Hlcp.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--set-admin-password").ToArray());
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("This host is Development-only until HTTPS and deployment hardening are completed.");
builder.WebHost.UseUrls("http://127.0.0.1:5088");
var dataDirectory = Path.GetFullPath(builder.Configuration["Storage:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, ".local"));
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection().SetApplicationName("Hlcp.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton(new AdminCredentialFile(builder.Configuration["Admin:PasswordFile"] ?? Path.Combine(dataDirectory, "admin.hash")));
builder.Services.AddSingleton<AdminSessions>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Hlcp.Web.Admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // HTTP is allowed only by the Development-only, forced-loopback host above.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/account/login";
    options.ExpireTimeSpan = AdminSessions.Lifetime;
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        if (!await context.HttpContext.RequestServices.GetRequiredService<AdminSessions>().IsValidAsync(context.Principal!, context.HttpContext.RequestAborted))
            context.RejectPrincipal();
    };
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireAuthenticatedUser().RequireClaim("hlcp_access", "admin")));
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAdminState>();
builder.Services.AddScoped<DashboardReader>();
builder.Services.AddSingleton(new OperationJournal(Path.Combine(dataDirectory, "operations")));
builder.Services.AddScoped<ResourceActions>();
builder.Services.AddScoped<CircuitConnection>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(provider => provider.GetRequiredService<CircuitConnection>());
builder.Services.AddSingleton<IHlcpTokenSource>(provider => new FileTokenSource(provider.GetRequiredService<IConfiguration>()["Hlcp:TokenFile"]));
builder.Services.AddHttpClient("Hlcp", http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(HlcpReadClient.CreateHandler);
builder.Services.AddScoped(provider => new HlcpReadClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("Hlcp"),
    new Uri(provider.GetRequiredService<IConfiguration>()["Hlcp:Endpoint"] ?? "http://127.0.0.1:9444"), provider.GetRequiredService<IHlcpTokenSource>()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsync("Příliš mnoho přihlašovacích pokusů. Zkus to za minutu.", cancellationToken);
    };
    // One admin account: global limit also covers attempts distributed across source IPs.
    options.AddPolicy("Login", _ => RateLimitPartition.GetFixedWindowLimiter("admin", _ => new FixedWindowRateLimiterOptions
    { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (args.Contains("--set-admin-password", StringComparer.Ordinal))
{
    Environment.ExitCode = await PasswordSetup.RunAsync(app.Services.GetRequiredService<AdminCredentialFile>());
    return;
}
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/_blazor"))
    {
        var origins = context.Request.Headers.Origin;
        if ((origins.Count != 0 && (origins.Count != 1 || origins[0] != $"{context.Request.Scheme}://{context.Request.Host}")) ||
            (context.WebSockets.IsWebSocketRequest && origins.Count != 1))
        { context.Response.StatusCode = 403; return; }
    }
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapRazorPages();
app.MapGet("/api/dashboard", async (HttpContext context, DashboardReader reader) =>
{
    try { return Results.Ok(await reader.ReadAsync(context.User, context.RequestAborted)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).RequireAuthorization("Admin");
app.MapStaticAssets();
app.MapRazorComponents<Hlcp.Web.Components.App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
