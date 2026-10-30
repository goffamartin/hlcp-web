using System.Security.Claims;
using Hlcp.Application;
using Hlcp.Web.Security;

namespace Hlcp.Web.Services;

public sealed record DashboardReadResult(NodeSnapshot[]? Nodes, DateTimeOffset? CapturedAt, HlcpFailure? Failure);

public sealed class DashboardReader(AdminSessions sessions, HlcpReadClient client, TimeProvider clock)
{
    public async Task<DashboardReadResult> ReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        // Enforce at the service boundary too: a previously authenticated Blazor circuit may have expired.
        if (!await sessions.IsValidAsync(principal, cancellationToken)) throw new UnauthorizedAccessException();
        try { return new((await client.GetNodesAsync(cancellationToken)).ToArray(), clock.GetUtcNow(), null); }
        catch (HlcpReadException error) { return new(null, null, error.Failure); }
    }
}
