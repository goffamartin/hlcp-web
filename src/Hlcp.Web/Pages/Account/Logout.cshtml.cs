using Hlcp.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hlcp.Web.Pages.Account;

[Authorize(Policy = "Admin")]
public sealed class LogoutModel(AdminSessions sessions) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");
    public async Task<IActionResult> OnPostAsync()
    {
        sessions.Revoke(User);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/account/login");
    }
}
