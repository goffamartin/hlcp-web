using Hlcp.Web.Services;

namespace Hlcp.Tests;

public sealed class CircuitConnectionTests
{
    [Fact]
    public async Task PollingPausesWhileCircuitIsDisconnectedAndResumesOnReconnect()
    {
        using var connection = new CircuitConnection();
        using var cancellation = new CancellationTokenSource();
        var initial = connection.WaitAsync(cancellation.Token);
        Assert.False(initial.IsCompleted);
        await connection.OnConnectionUpAsync(null!, default);
        await initial;
        var beforeDisconnect = connection.DisconnectToken;
        await connection.OnConnectionDownAsync(null!, default);
        Assert.True(beforeDisconnect.IsCancellationRequested);
        var paused = connection.WaitAsync(cancellation.Token);
        Assert.False(paused.IsCompleted);
        await connection.OnConnectionUpAsync(null!, default);
        Assert.False(connection.DisconnectToken.IsCancellationRequested);
        await paused;
        await connection.OnConnectionDownAsync(null!, default);
        var disposed = connection.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposed);
    }
}
