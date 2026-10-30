using System.Runtime.CompilerServices;
using System.Security.Claims;
using Hlcp.Application;
using Hlcp.Web.Security;

namespace Hlcp.Web.Services;

public sealed class LogReadBudget
{
    private readonly SemaphoreSlim reads = new(2, 2);
    public bool TryEnter() => reads.Wait(0);
    public void Exit() => reads.Release();
}

public sealed class LogReader(AdminSessions sessions, HlcpReadClient inventory, HlcpLogClient logs, LogReadBudget budget, CircuitConnection connection)
{
    public async Task<LogPage> HistoryAsync(ClaimsPrincipal user, string node, string resource, LogQuery? query = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(user, cancellationToken);
        if (!budget.TryEnter()) throw new LogReadException(LogFailure.Capacity);
        try
        {
            await using var lease = new ReadLease(sessions, user, connection.DisconnectToken, cancellationToken);
            try
            {
                await VerifyResourceAsync(node, resource, lease.Token);
                var result = await logs.HistoryAsync(node, resource, query, cursor, cancellationToken: lease.Token);
                await AuthorizeAsync(user, cancellationToken); // Do not return a completed response to a revoked session.
                return result;
            }
            catch (OperationCanceledException) when (lease.Revoked) { throw new UnauthorizedAccessException(); }
        }
        finally { budget.Exit(); }
    }

    public async IAsyncEnumerable<LogEvent> FollowAsync(ClaimsPrincipal user, string node, string resource, LogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(user, cancellationToken);
        if (!budget.TryEnter()) throw new LogReadException(LogFailure.Capacity);
        try
        {
            await using var lease = new ReadLease(sessions, user, connection.DisconnectToken, cancellationToken);
            try { await VerifyResourceAsync(node, resource, lease.Token); }
            catch (OperationCanceledException) when (lease.Revoked) { throw new UnauthorizedAccessException(); }
            await using var iterator = logs.FollowAsync(node, resource, query, lease.Token).GetAsyncEnumerator();
            while (true)
            {
                bool more;
                try { more = await iterator.MoveNextAsync(); }
                catch (OperationCanceledException) when (lease.Revoked) { throw new UnauthorizedAccessException(); }
                if (!more) break;
                if (lease.Revoked) throw new UnauthorizedAccessException();
                yield return iterator.Current;
            }
        }
        finally { budget.Exit(); }
    }

    private async Task AuthorizeAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    { if (!await sessions.IsValidAsync(user, cancellationToken)) throw new UnauthorizedAccessException(); }
    private async Task VerifyResourceAsync(string node, string resource, CancellationToken cancellationToken)
    {
        var resources = await inventory.GetResourcesAsync(node, cancellationToken: cancellationToken);
        if (!resources.Any(row => row.Id == resource && row.Type == "container" && row.Actions.Contains("logs", StringComparer.Ordinal)))
            throw new LogReadException(LogFailure.Forbidden);
    }

    private sealed class ReadLease : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop;
        private readonly Task monitor;
        private volatile bool revoked;
        public bool Revoked => revoked;
        public CancellationToken Token => stop.Token;
        public ReadLease(AdminSessions sessions, ClaimsPrincipal user, CancellationToken disconnect, CancellationToken caller)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(disconnect, caller);
            monitor = MonitorAsync(sessions, user);
        }
        private async Task MonitorAsync(AdminSessions sessions, ClaimsPrincipal user)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    if (await sessions.IsValidAsync(user, stop.Token)) continue;
                    revoked = true;
                    await stop.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { await stop.CancelAsync(); await monitor; stop.Dispose(); }
    }
}
