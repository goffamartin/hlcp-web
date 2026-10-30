using Hlcp.Web.Services;

namespace Hlcp.Tests;

public sealed class CircuitConnectionTests
{
    [Fact]
    public async Task PollingPausesWhileCircuitIsDisconnectedAndResumesOnReconnect()
    {
        var connection = new CircuitConnection();
        using var cancellation = new CancellationTokenSource();
        var initial = connection.WaitAsync(cancellation.Token);
        Assert.False(initial.IsCompleted);
        await connection.OnConnectionUpAsync(null!, default);
        await initial;
        await connection.OnConnectionDownAsync(null!, default);
        var paused = connection.WaitAsync(cancellation.Token);
        Assert.False(paused.IsCompleted);
        await connection.OnConnectionUpAsync(null!, default);
        await paused;
        await connection.OnConnectionDownAsync(null!, default);
        var disposed = connection.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposed);
    }
}
