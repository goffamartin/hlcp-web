using System.Security.Claims;
using Hlcp.Application;
using Hlcp.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hlcp.Tests;

public sealed class ResourceActionsTests
{
    [Fact]
    public async Task ReadOnlyConfigurationCannotSubmitCommandsEvenForAuthenticatedAdmin()
    {
        await using var factory = new TestWeb(withOperator: false);
        factory.CreateClient();
        var user = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        Assert.False(actions.Enabled);
        var error = await Assert.ThrowsAsync<HlcpReadException>(() => actions.PrepareAsync(user, "synthetic-node", "synthetic-container", "restart", default));
        Assert.Equal(HlcpFailure.CredentialUnavailable, error.Failure);
        Assert.Equal(0, factory.Controller.Calls);
    }

    [Fact]
    public async Task ConfirmUsesPersistedIdAndDoubleClickSubmitsOnlyOnce()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var principal = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var prepared = await actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "restart", default);
        Assert.Equal(0, factory.Controller.Submissions);
        var stored = await factory.Services.GetRequiredService<OperationJournal>().ReadAsync(prepared.Id);
        Assert.Equal(prepared, stored);
        async Task<CommandResult?> Confirm()
        {
            try { return await actions.ConfirmAsync(principal, prepared.Id, default); }
            catch (HlcpReadException error) when (error.Failure == HlcpFailure.NotFound) { return null; } // Duplicate lookup may precede admission.
        }
        var results = await Task.WhenAll(Confirm(), Confirm());
        Assert.Equal(1, factory.Controller.Submissions);
        Assert.Contains(results, result => result?.CommandId == prepared.Id);
        Assert.All(results.Where(result => result is not null), result => Assert.Equal(prepared.Id, result!.CommandId));
        var item = Assert.Single(await actions.ListAsync(principal, default));
        Assert.True(item.Submitted);
        Assert.True(item.Result!.Success);
    }

    [Fact]
    public async Task LostResponseIsLookedUpWithoutAnotherSubmissionEvenWithNewJournalInstance()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var principal = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var operation = await actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "restart", default);
        factory.Controller.FailSubmission = true;
        await Assert.ThrowsAsync<HlcpReadException>(() => actions.ConfirmAsync(principal, operation.Id, default));
        var disk = new OperationJournal(Path.Combine(factory.Root, "operations"));
        Assert.True(Assert.Single(await disk.ListAsync(default)).Submitted);
        Assert.Null(Assert.Single(await disk.ListAsync(default)).Result);
        Assert.False(await disk.MarkSubmittedAsync(operation.Id));
        var recovered = await actions.ConfirmAsync(principal, operation.Id, default);
        Assert.True(recovered!.Success);
        Assert.Equal(1, factory.Controller.Submissions);
    }

    [Fact]
    public async Task AuthenticationAndFreshAllowlistAreCheckedAtServiceBoundary()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var principal = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => actions.PrepareAsync(new ClaimsPrincipal(), "synthetic-node", "synthetic-container", "restart", default));
        await Assert.ThrowsAsync<HlcpReadException>(() => actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "arbitrary-script", default));
        await Assert.ThrowsAsync<HlcpReadException>(() => actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "logs", default));
        var operation = await actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "restart", default);
        factory.Controller.DenyAction = true;
        await Assert.ThrowsAsync<HlcpReadException>(() => actions.ConfirmAsync(principal, operation.Id, default));
        Assert.Equal(0, factory.Controller.Submissions);
        Assert.False(Assert.Single(await actions.ListAsync(principal, default)).Submitted);
    }

    [Fact]
    public async Task JournalFailureCannotSendACommandAndPathTraversalIsRejected()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var principal = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var journal = factory.Services.GetRequiredService<OperationJournal>();
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ReadAsync("../outside"));
        await Assert.ThrowsAsync<ArgumentException>(() => actions.ConfirmAsync(principal, "../outside", default));
        // A regular file blocks creation of the operations directory.
        await File.WriteAllTextAsync(Path.Combine(factory.Root, "operations"), "synthetic filesystem fault");
        await Assert.ThrowsAnyAsync<IOException>(() => actions.PrepareAsync(principal, "synthetic-node", "synthetic-container", "restart", default));
        Assert.Equal(0, factory.Controller.Submissions);
    }

    [Fact]
    public async Task UpstreamForbiddenBodyAndTokenDoNotEnterDashboardResult()
    {
        await using var factory = new TestWeb();
        factory.CreateClient();
        var principal = await factory.PrincipalAsync();
        factory.Controller.ForbidReads = true;
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DashboardReader>().ReadAsync(principal);
        Assert.Equal(HlcpFailure.AccessDenied, result.Failure);
        Assert.Null(result.Nodes);
        Assert.DoesNotContain(TestWeb.Token, System.Text.Json.JsonSerializer.Serialize(result));
    }
}
