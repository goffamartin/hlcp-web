using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Hlcp.Web.Security;

internal sealed class RevalidatingAdminState(ILoggerFactory loggerFactory, AdminSessions sessions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
        => sessions.IsValidAsync(state.User, cancellationToken);
}
