using Hlcp.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Hlcp.Web.Pages.Account;

[AllowAnonymous, EnableRateLimiting("Login"), RequestSizeLimit(8192)]
public sealed class LoginModel(AdminCredentialFile credential, AdminSessions sessions) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    public bool Failed { get; private set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect("/") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var version = await credential.VerifyAsync(Password, cancellationToken);
        Password = "";
        ModelState.Clear();
        var principal = version is null ? null : sessions.Create(version);
        if (principal is null) { Failed = true; Response.StatusCode = 401; return Page(); }
        sessions.Revoke(User);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
        return LocalRedirect("/");
    }
}
