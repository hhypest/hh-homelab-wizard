using System.Net;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Minimal M0 API probe. Credentials belong to the caller and are never journalled.</summary>
public sealed class QbittorrentContractClient
{
    private readonly HttpClient client;

    /// <param name="client">Dedicated client with a CookieContainer and redirects disabled.</param>
    public QbittorrentContractClient(HttpClient client)
    {
        var uri = client.BaseAddress;
        if (uri is null || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("api.base-address.invalid", nameof(client));
        this.client = client;
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login");
        request.Headers.Referrer = client.BaseAddress;
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password
        });
        using var response = await client.SendAsync(request, cancellationToken);
        // Contract of the pinned 5.2.4 source: 204 on success, 401 on wrong credentials.
        // An older documented 200/"Ok." response is not accepted as this version's contract.
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidOperationException("qb.authentication.rejected");
        _ = await GetVersionAsync(cancellationToken); // Prove the authenticated follow-up, including cookie handling.
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("api/v2/app/version", cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("qb.version.unavailable");
        var version = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (version != "v5.2.4") throw new InvalidOperationException("qb.version.unsupported");
        return version;
    }
}
