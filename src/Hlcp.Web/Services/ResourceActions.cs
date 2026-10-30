using System.Security.Claims;
using Hlcp.Application;
using Hlcp.Web.Security;

namespace Hlcp.Web.Services;

public sealed class ResourceActions(AdminSessions sessions, HlcpReadClient inventory, OperationJournal journal, IHttpClientFactory http, IConfiguration configuration)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["Hlcp:OperatorTokenFile"]);
    private HlcpReadClient Operator() => new(http.CreateClient("Hlcp"), new Uri(configuration["Hlcp:Endpoint"] ?? "http://127.0.0.1:9444"), new FileTokenSource(configuration["Hlcp:OperatorTokenFile"]), TimeSpan.FromSeconds(30));
    private async Task AuthorizeAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!await sessions.IsValidAsync(user, cancellationToken)) throw new UnauthorizedAccessException();
        if (!Enabled) throw new HlcpReadException(HlcpFailure.CredentialUnavailable);
    }

    public async Task<WebOperation> PrepareAsync(ClaimsPrincipal user, string node, string resource, string action, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(user, cancellationToken);
        await VerifyAllowlistAsync(node, resource, action, cancellationToken);
        var operation = await journal.PrepareAsync(node, resource, action, cancellationToken);
        await Operator().PreviewAsync(node, operation.Command, cancellationToken);
        return operation;
    }

    private async Task VerifyAllowlistAsync(string node, string resource, string action, CancellationToken cancellationToken)
    {
        var rows = await inventory.GetResourcesAsync(node, cancellationToken: cancellationToken);
        if (!rows.Any(row => row.Id == resource && row.Actions.Contains(action, StringComparer.Ordinal) && !(row.Type == "container" && action == "logs")))
            throw new HlcpReadException(HlcpFailure.CommandRefused);
    }

    public async Task<CommandResult?> ConfirmAsync(ClaimsPrincipal user, string id, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(user, cancellationToken);
        var operation = await journal.ReadAsync(id, cancellationToken);
        if (DateTimeOffset.UtcNow - operation.CreatedAt > TimeSpan.FromMinutes(5))
            throw new HlcpReadException(HlcpFailure.CommandRefused);
        await VerifyAllowlistAsync(operation.Node, operation.Resource, operation.Action, cancellationToken);
        var client = Operator();
        await client.PreviewAsync(operation.Node, operation.Command, cancellationToken);
        if (!await journal.MarkSubmittedAsync(id)) return await LookupAsync(user, id, cancellationToken);
        // Marker was persisted before POST. Any error after this point is an unknown outcome;
        // reconnect/double-click can only look up this same ID, never automatically POST again.
        var result = await client.SubmitAsync(operation.Node, operation.Command, cancellationToken);
        await journal.SaveResultAsync(id, result);
        return result;
    }

    public async Task<CommandResult?> LookupAsync(ClaimsPrincipal user, string id, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(user, cancellationToken);
        var operation = await journal.ReadAsync(id, cancellationToken);
        var result = (await Operator().LookupAsync(operation.Node, operation.Command, cancellationToken)).Result;
        if (result is not null) await journal.SaveResultAsync(id, result);
        return result;
    }

    public async Task<OperationStatus[]> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!await sessions.IsValidAsync(user, cancellationToken)) throw new UnauthorizedAccessException();
        return await journal.ListAsync(cancellationToken);
    }
}
