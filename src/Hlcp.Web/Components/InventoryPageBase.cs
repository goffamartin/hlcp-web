using Hlcp.Application;
using Hlcp.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Hlcp.Web.Components;

public abstract class InventoryPageBase : ComponentBase, IAsyncDisposable
{
    [Parameter] public string Node { get; set; } = "";
    [Inject] public DashboardReader Reader { get; set; } = default!;
    [Inject] public AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] public CircuitConnection Connection { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    protected virtual bool LoadAllNodes => false;
    protected virtual string RouteKey => Node;
    protected DashboardState State { get; private set; } = new();
    protected bool Loading { get; private set; }
    protected bool Stale => State.IsStale(DateTimeOffset.UtcNow);
    protected NodeSnapshot? CurrentNode => State.Nodes?.SingleOrDefault(item => item.NodeId == Node);
    protected DashboardSnapshot Snapshot => State.Project(DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim requests = new(1, 1);
    private CancellationTokenSource? route;
    private Task? polling;
    private string? key;
    private bool pending, disposed;

    protected override async Task OnParametersSetAsync()
    {
        if (key == RouteKey) return;
        key = RouteKey;
        State = new(); // Clear the old target before awaiting cancellation or displaying a new heading.
        await StopAsync();
        route = new();
        pending = true;
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (pending && !disposed)
        {
            pending = false;
            polling = PollAsync(Node, State, route!.Token);
        }
        return Task.CompletedTask;
    }
    private async Task PollAsync(string node, DashboardState state, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Connection.WaitAsync(cancellationToken);
                await FetchAsync(node, state, cancellationToken);
                await Task.Delay(state.RefreshDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) when (!disposed) { await InvokeAsync(() => Navigation.NavigateTo("/account/login", forceLoad: true)); }
    }
    protected async Task RefreshAsync()
    {
        if (route is null || disposed) return;
        var cancellationToken = route.Token;
        try { await FetchAsync(Node, State, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) when (!disposed) { Navigation.NavigateTo("/account/login", forceLoad: true); }
    }
    protected Task RefreshAfterActionAsync(CommandResult _) => RefreshAsync();
    private async Task FetchAsync(string node, DashboardState state, CancellationToken cancellationToken)
    {
        if (!await requests.WaitAsync(0, cancellationToken)) return;
        try
        {
            Loading = true;
            if (!disposed) await InvokeAsync(StateHasChanged);
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            if (LoadAllNodes)
            {
                var result = await Reader.ReadAsync(user, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Nodes is not null && result.CapturedAt is { } at) state.Accept(result.Nodes, at);
                else state.Refuse(result.Failure ?? HlcpFailure.Unavailable);
            }
            else
            {
                var result = await Reader.ReadNodeAsync(user, node, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Node is not null && result.CapturedAt is { } at) state.Accept([result.Node], at);
                else state.Refuse(result.Failure ?? HlcpFailure.Unavailable);
            }
        }
        finally
        {
            Loading = false;
            requests.Release();
            if (!disposed && !cancellationToken.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }
    private async Task StopAsync()
    {
        if (route is not null) await route.CancelAsync();
        if (polling is not null) await polling;
        route?.Dispose();
        polling = null;
    }
    public async ValueTask DisposeAsync() { disposed = true; await StopAsync(); }
}
