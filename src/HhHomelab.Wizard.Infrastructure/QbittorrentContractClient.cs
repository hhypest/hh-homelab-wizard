using System.Net;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Minimal M0 API probe. Credentials belong to the caller and are never journalled.</summary>
public sealed partial class QbittorrentContractClient
{
    private readonly HttpClient client;
    private readonly ContractHttpTransport transport;
    private bool authenticated;

    /// <param name="client">Exclusively owned client with a fresh CookieContainer and redirects disabled.
    /// Do not authenticate this client concurrently or change the server's bypass policy during a probe.</param>
    public QbittorrentContractClient(HttpClient client, ContractHttpTimeouts? timeouts = null)
    {
        var uri = client.BaseAddress;
        if (uri is null)
            throw new ArgumentException("api.base-address.invalid", nameof(client));
        var shapeInvalid =
            uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || !uri.AbsolutePath.EndsWith('/');
        if (shapeInvalid)
            throw new ArgumentException("api.base-address.invalid", nameof(client));
        this.client = client;
        transport = new(client,
            static (code, status, kind) => new QbittorrentContractException(code, status, kind), timeouts);
    }

    public async Task LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        // qBittorrent ignores submitted credentials if a session or IP-based bypass already exists.
        // Require denial of a protected endpoint before sending any credentials. Do not log out
        // somebody else's session or change the server's authentication policy to make this pass.
        using (var preflight = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version"))
        {
            preflight.Headers.Referrer = client.BaseAddress;
            var status = await ReadStatusAsync(
                preflight,
                "qb.authentication.preflight",
                cancellationToken
            );
            if (status == HttpStatusCode.OK)
                throw new QbittorrentContractException(
                    "qb.authentication.context-preauthenticated",
                    status
                );
            if (status != HttpStatusCode.Forbidden)
                throw new QbittorrentContractException(
                    "qb.authentication.preflight-failed",
                    status
                );
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login");
        request.Headers.Referrer = client.BaseAddress;
        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = username, ["password"] = password }
        );
        var loginStatus = await ReadStatusAsync(request, "qb.authentication", cancellationToken);
        // Contract of the pinned 5.2.4 source: 204 on success, 401 on wrong credentials.
        // An older documented 200/"Ok." response is not accepted as this version's contract.
        if (loginStatus != HttpStatusCode.NoContent)
        {
            var code = loginStatus switch
            {
                HttpStatusCode.Unauthorized => "qb.authentication.rejected",
                HttpStatusCode.Forbidden => "qb.authentication.forbidden",
                _ => "qb.authentication.http-error",
            };
            throw new QbittorrentContractException(code, loginStatus);
        }
        _ = await GetVersionAsync(cancellationToken); // Prove the authenticated follow-up, including cookie handling.
        authenticated = true;
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version");
        request.Headers.Referrer = client.BaseAddress;
        var version = await transport.ExchangeAsync(request, "qb.version", async (response, readToken) =>
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new QbittorrentContractException("qb.version.unavailable", response.StatusCode);
            return (await response.Content.ReadAsStringAsync(readToken)).Trim();
        }, cancellationToken);
        if (version != PinnedRuntimeProfile.Version("qbittorrent"))
            throw new QbittorrentContractException("qb.version.unsupported");
        return version;
    }

    private Task<HttpStatusCode> ReadStatusAsync(
        HttpRequestMessage request,
        string code,
        CancellationToken token
    ) => transport.ExchangeAsync(request, code,
        static (response, _) => Task.FromResult(response.StatusCode), token);
}
