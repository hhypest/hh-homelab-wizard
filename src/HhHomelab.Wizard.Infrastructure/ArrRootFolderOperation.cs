using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

public sealed class ArrRootFolderOperation(ArrContractClient client, ConfigurationRequest request)
    : ConfigurationOperationAdapter(request)
{
    protected override bool MatchesService => Request.Kind == ConfigurationOperationKind.ArrRootFolder
        && Request.Identity.Resource.Service == client.ServiceIdentity;

    protected override string EndpointFingerprint => client.EndpointFingerprint;

    protected override async Task<(ObservedResourceState State, string Revision)> ReadAsync(CancellationToken token)
    {
        _ = await client.GetVersionAsync(token).ConfigureAwait(false);
        var identity = await client.ReadRootIdentityForOperationAsync(Request.Path, token).ConfigureAwait(false);
        var state = identity is null ? ObservedResourceState.Missing : ObservedResourceState.Satisfied;
        return (state, ConfigurationRequest.Fingerprint(identity));
    }

    protected override Task WriteAsync(CancellationToken token) => client.CreateRootForOperationAsync(Request.Path, token);
}

public sealed partial class ArrContractClient
{
    internal string ServiceIdentity => service.ToString().ToLowerInvariant();

    internal string EndpointFingerprint => ConfigurationRequest.Fingerprint(http.BaseAddress!.AbsoluteUri);

    internal Task<int?> ReadRootIdentityForOperationAsync(string path, CancellationToken token) => ReadRootIdentityAsync(path, token);

    internal Task CreateRootForOperationAsync(string path, CancellationToken token) => SendAsync(
        HttpMethod.Post,
        "rootfolder",
        new JsonObject { ["path"] = path },
        "arr.root",
        token
    );
}
