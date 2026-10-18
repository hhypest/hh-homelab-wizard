using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

public sealed class QbittorrentCategoryOperation(QbittorrentContractClient client, ConfigurationRequest request)
    : ConfigurationOperationAdapter(request)
{
    protected override bool MatchesService => Request.Kind == ConfigurationOperationKind.QbittorrentCategory;

    protected override string EndpointFingerprint => client.EndpointFingerprint;

    protected override async Task<(ObservedResourceState State, string Revision)> ReadAsync(CancellationToken token)
    {
        _ = await client.GetVersionAsync(token).ConfigureAwait(false);
        var categories = await client.GetCategoriesAsync(token).ConfigureAwait(false);
        var exists = categories.TryGetValue(Request.Name, out var path);
        var state = !exists ? ObservedResourceState.Missing
            : path!.TrimEnd('/') == Request.Path.TrimEnd('/') ? ObservedResourceState.Satisfied
            : ObservedResourceState.Conflict;
        return (state, ConfigurationRequest.Fingerprint(new { exists, Path = path?.TrimEnd('/') }));
    }

    protected override Task WriteAsync(CancellationToken token) => client.CreateCategoryForOperationAsync(Request.Name, Request.Path, token);
}

public sealed partial class QbittorrentContractClient
{
    internal string EndpointFingerprint => ConfigurationRequest.Fingerprint(client.BaseAddress!.AbsoluteUri);

    internal Task CreateCategoryForOperationAsync(string name, string path, CancellationToken token)
    {
        RequireSession();
        return PostFormAsync("api/v2/torrents/createCategory", new() { ["category"] = name, ["savePath"] = path }, "qb.category", token);
    }
}
