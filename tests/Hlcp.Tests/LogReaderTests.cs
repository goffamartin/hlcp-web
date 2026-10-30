using System.Security.Claims;
using Hlcp.Application;
using Hlcp.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hlcp.Tests;

public sealed class LogReaderTests
{
    [Fact]
    public async Task AnonymousReadsCannotReachController()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.HistoryAsync(new ClaimsPrincipal(), "n", "r"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HlcpLogClientTests.CollectAsync(reader.FollowAsync(new ClaimsPrincipal(), "n", "r", new())));
        Assert.Equal(0, factory.Controller.Calls);
    }

    [Fact]
    public async Task LogsUseObserverAndNeverCreateOperationsOrCommands()
    {
        await using var factory = new TestWeb(withOperator: false);
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        Assert.Single((await reader.HistoryAsync(user, "synthetic-node", "synthetic-container")).Records);
        var events = await HlcpLogClientTests.CollectAsync(reader.FollowAsync(user, "synthetic-node", "synthetic-container", new()));
        Assert.Equal("end", events[^1].Kind);
        Assert.Equal(0, factory.Controller.Submissions);
        Assert.Equal(2, factory.Controller.LogReads);
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "operations")));
    }

    [Fact]
    public async Task AllowlistAndGlobalBudgetAreEnforcedBeforeStartingLogRead()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        factory.Controller.DenyAction = true;
        var error = await Assert.ThrowsAsync<LogReadException>(() => reader.HistoryAsync(user, "synthetic-node", "synthetic-container"));
        Assert.Equal(LogFailure.Forbidden, error.Failure);
        Assert.Equal(0, factory.Controller.LogReads);
        factory.Controller.DenyAction = false;
        var budget = factory.Services.GetRequiredService<LogReadBudget>();
        Assert.True(budget.TryEnter()); Assert.True(budget.TryEnter());
        try
        {
            error = await Assert.ThrowsAsync<LogReadException>(() => reader.HistoryAsync(user, "synthetic-node", "synthetic-container"));
            Assert.Equal(LogFailure.Capacity, error.Failure);
            Assert.Equal(0, factory.Controller.LogReads);
        }
        finally { budget.Exit(); budget.Exit(); }
        await reader.HistoryAsync(user, "synthetic-node", "synthetic-container");
        Assert.Equal(1, factory.Controller.LogReads);
    }

    [Fact]
    public async Task ScriptActionNamedLogsRemainsOrdinaryActionRatherThanNativeContainerRead()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        factory.Controller.TaskLogs = true;
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        await Assert.ThrowsAsync<LogReadException>(() => reader.HistoryAsync(user, "synthetic-node", "synthetic-container"));
        Assert.Equal(0, factory.Controller.LogReads);
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var operation = await actions.PrepareAsync(user, "synthetic-node", "synthetic-container", "logs", default);
        Assert.True((await actions.ConfirmAsync(user, operation.Id, default))!.Success);
        Assert.Equal(1, factory.Controller.Submissions);
    }

    [Fact]
    public async Task CircuitDisconnectCancelsIdleStreamAndReleasesReadBudget()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        var body = new StallingLogStream(HlcpLogClientTests.Lines(HlcpLogClientTests.Event("start", 1)));
        factory.Controller.LogResponse = () => HlcpLogClientTests.StreamReply(body);
        using var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<CircuitConnection>();
        await connection.OnConnectionUpAsync(null!, default);
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        await using (var events = reader.FollowAsync(user, "synthetic-node", "synthetic-container", new()).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync());
            var pending = events.MoveNextAsync().AsTask();
            await connection.OnConnectionDownAsync(null!, default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.True(body.Disposed);
        var budget = factory.Services.GetRequiredService<LogReadBudget>();
        Assert.True(budget.TryEnter()); Assert.True(budget.TryEnter());
        budget.Exit(); budget.Exit();
        Assert.Equal(1, factory.Controller.LogReads);
    }

    [Fact]
    public async Task PasswordRotationCancelsIdleStreamWithoutWaitingForAnotherRecord()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        var body = new StallingLogStream(HlcpLogClientTests.Lines(HlcpLogClientTests.Event("start", 1)));
        factory.Controller.LogResponse = () => HlcpLogClientTests.StreamReply(body);
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<LogReader>();
        await using var events = reader.FollowAsync(user, "synthetic-node", "synthetic-container", new()).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var waiting = events.MoveNextAsync().AsTask();
        await factory.Services.GetRequiredService<Hlcp.Web.Security.AdminCredentialFile>().WriteAsync("Rotated synthetic password 123");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(body.Disposed);
        Assert.Equal(0, factory.Controller.Submissions);
    }
}
