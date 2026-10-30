using System.Security.Claims;
using System.Text.Json;
using Hlcp.Application;
using Hlcp.Web.Components;
using Hlcp.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hlcp.Tests;

public sealed class DetailsAndOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessAndFailureOutputReachCallerAndLookupButNotJournal(bool success)
    {
        await using var factory = new TestWeb(); factory.CreateClient();
        var user = await factory.PrincipalAsync();
        factory.Controller.ResultMessage = "application-output-secret\n<script>untrusted</script>";
        factory.Controller.ResultSuccess = success;
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var operation = await actions.PrepareAsync(user, "synthetic-node", "synthetic-container", "restart", default);
        var result = await actions.ConfirmAsync(user, operation.Id, default);
        Assert.Equal(success, result!.Success);
        Assert.Equal(factory.Controller.ResultMessage, result.Message);
        Assert.Equal(result.Message, (await actions.LookupAsync(user, operation.Id, default))!.Message);
        var saved = await File.ReadAllTextAsync(Path.Combine(factory.Root, "operations", operation.Id + ".result.json"));
        Assert.DoesNotContain("application-output-secret", saved);
        Assert.DoesNotContain("Message", saved);
        Assert.Null(Assert.Single(await actions.ListAsync(user, default)).Result!.Message);
    }

    [Fact]
    public async Task StatusPreviewDeterminesReadOnlyAndIsRecheckedBeforeSubmission()
    {
        await using var factory = new TestWeb(); factory.CreateClient();
        var user = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var plan = await actions.PrepareWithPreviewAsync(user, "synthetic-node", "synthetic-container", "status", default);
        Assert.True(plan.ReadOnly);
        factory.Controller.ForceMutatingPreview = true;
        var refused = await Assert.ThrowsAsync<HlcpReadException>(() => actions.ConfirmAsync(user, plan.Operation.Id, default, requireReadOnly: true));
        Assert.Equal(HlcpFailure.CommandRefused, refused.Failure);
        Assert.Equal(0, factory.Controller.Submissions);
        factory.Controller.ForceMutatingPreview = false;
        Assert.Equal("running", (await actions.ConfirmAsync(user, plan.Operation.Id, default, requireReadOnly: true))!.Message);
        factory.Controller.StatusIsMutating = true;
        var script = await actions.PrepareWithPreviewAsync(user, "synthetic-node", "synthetic-container", "status", default);
        Assert.False(script.ReadOnly);
    }

    [Fact]
    public async Task RetainedOutputCanBeReadWithObserverAfterOperatorIsDisabled()
    {
        await using var factory = new TestWeb(); factory.CreateClient();
        var user = await factory.PrincipalAsync();
        using var scope = factory.Services.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<ResourceActions>();
        var operation = await actions.PrepareAsync(user, "synthetic-node", "synthetic-container", "status", default);
        await actions.ConfirmAsync(user, operation.Id, default);
        factory.Services.GetRequiredService<IConfiguration>()["Hlcp:OperatorTokenFile"] = null;
        Assert.False(actions.Enabled);
        Assert.Equal("running", (await actions.LookupAsync(user, operation.Id, default))!.Message);
        Assert.Equal(1, factory.Controller.Submissions);
    }

    [Fact]
    public async Task NodeReadsRequireSessionAndDistinguishMissingAndDeniedTargets()
    {
        await using var factory = new TestWeb(); factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<DashboardReader>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadNodeAsync(new ClaimsPrincipal(), "synthetic-node"));
        Assert.Equal(0, factory.Controller.Calls);
        var user = await factory.PrincipalAsync();
        var read = await reader.ReadNodeAsync(user, "synthetic-node");
        Assert.Equal("synthetic-node", read.Node!.NodeId);
        Assert.Contains("podman", read.Node.Capabilities!);
        Assert.Equal(HlcpFailure.NotFound, (await reader.ReadNodeAsync(user, "missing")).Failure);
        factory.Controller.ForbidReads = true;
        var denied = await reader.ReadNodeAsync(user, "synthetic-node");
        Assert.Equal(HlcpFailure.AccessDenied, denied.Failure);
        Assert.Null(denied.Node);
    }

    [Fact]
    public async Task ResourceHistoryFiltersBeforeTakingLatestFiftyAndReadsLegacyMetadata()
    {
        await using var factory = new TestWeb(); factory.CreateClient();
        var journal = factory.Services.GetRequiredService<OperationJournal>();
        var desired = await journal.PrepareAsync("n", "r", "status", default);
        await journal.MarkSubmittedAsync(desired.Id);
        // This is the pre-upgrade on-disk shape, with no Message field.
        await File.WriteAllTextAsync(Path.Combine(factory.Root, "operations", desired.Id + ".result.json"), JsonSerializer.Serialize(new { CommandId = desired.Id, Success = true, Code = "OK" }));
        for (var i = 0; i < 51; i++) await journal.PrepareAsync("other", "r", "status", default);
        Assert.DoesNotContain(await journal.ListAsync(default), item => item.Operation.Id == desired.Id);
        var filtered = Assert.Single(await journal.ListAsync(default, "n", "r"));
        Assert.Equal(desired.Id, filtered.Operation.Id);
        Assert.True(filtered.Result!.Success);
        Assert.Null(filtered.Result.Message);
    }

    [Fact]
    public async Task ResultHtmlEscapesUntrustedOutputAndPreservesMultilineText()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ActionResultView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Result"] = new CommandResult { CommandId = "op", Success = true, Code = "OK", Message = "running\n<script>alert(1)</script>\u001b[31m" } }));
            return rendered.ToHtmlString();
        });
        Assert.Contains("running", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain('\u001b', html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void OutputFormatsObjectsWithoutTreatingPlainTextAsStructuredState()
    {
        Assert.Equal("running", CommandOutput.Format("running"));
        Assert.Equal("first\nsecond\tvalue", CommandOutput.Format("first\nsecond\tvalue"));
        Assert.Contains('\n', CommandOutput.Format("{\"state\":\"running\",\"items\":[1,2]}"));
        Assert.Equal("/nodes/n%2Fone/resources/r%2Fone", UiLinks.Resource("n/one", "r/one"));
    }
}
