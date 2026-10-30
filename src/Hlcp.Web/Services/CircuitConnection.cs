using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Hlcp.Web.Services;

// Retained Blazor circuits must not keep polling the controller after the browser disconnects.
public sealed class CircuitConnection : CircuitHandler, IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource disconnected = new();
    private bool disposed;
    public CancellationToken DisconnectToken { get { lock (gate) return disconnected.Token; } }
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disconnected.IsCancellationRequested) { disconnected.Dispose(); disconnected = new(); }
            connected.TrySetResult();
        }
        return Task.CompletedTask;
    }
    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CancellationTokenSource previous;
        lock (gate)
        {
            if (connected.Task.IsCompleted) connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous = disconnected;
        }
        await previous.CancelAsync();
    }
    public Task WaitAsync(CancellationToken cancellationToken)
    { lock (gate) return connected.Task.WaitAsync(cancellationToken); }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; disconnected.Cancel(); disconnected.Dispose(); }
    }
}
