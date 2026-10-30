using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Hlcp.Web.Security;

public sealed class AdminSessions(AdminCredentialFile credential, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly Dictionary<string, DateTimeOffset> active = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public ClaimsPrincipal? Create(string version)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            foreach (var expired in active.Where(item => item.Value <= clock.GetUtcNow()).Select(item => item.Key).ToArray()) active.Remove(expired);
            if (active.Count >= 32) return null;
            active.Add(id, clock.GetUtcNow() + Lifetime);
        }
        return new(new ClaimsIdentity([new(ClaimTypes.Name, "Správce"), new("hlcp_access", "admin"),
            new("hlcp_session", id), new("hlcp_password_version", version)], CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.Identity?.IsAuthenticated != true || !principal.HasClaim("hlcp_access", "admin")) return false;
        var id = principal.FindFirstValue("hlcp_session");
        lock (gate)
            if (id is null || !active.TryGetValue(id, out var expires) || expires <= clock.GetUtcNow()) return false;
        var version = await credential.VersionAsync(cancellationToken);
        return version is not null && version == principal.FindFirstValue("hlcp_password_version");
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        if (principal.FindFirstValue("hlcp_session") is { } id) lock (gate) active.Remove(id);
    }
}
