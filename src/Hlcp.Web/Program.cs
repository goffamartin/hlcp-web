var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("This bootstrap host is Development-only. Authentication and LAN deployment are not implemented yet.");
builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<Hlcp.Web.Components.App>().AddInteractiveServerRenderMode();
app.Run();
