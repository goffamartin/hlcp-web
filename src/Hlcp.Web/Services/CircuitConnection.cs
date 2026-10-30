using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Hlcp.Web.Services;

// Retained Blazor circuits must not keep polling the controller after the browser disconnects.
public sealed class CircuitConnection : CircuitHandler
{
    private readonly object gate = new();
    private TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    { lock (gate) connected.TrySetResult(); return Task.CompletedTask; }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lock (gate) if (connected.Task.IsCompleted) connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.CompletedTask;
    }
    public Task WaitAsync(CancellationToken cancellationToken)
    { lock (gate) return connected.Task.WaitAsync(cancellationToken); }
}
