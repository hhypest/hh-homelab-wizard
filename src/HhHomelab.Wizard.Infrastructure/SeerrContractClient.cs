using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>M0 contracts for an owned Seerr 3.5.0. The HTTP client exclusively owns an empty
/// cookie jar, has no default authentication headers, and disables redirects. No automatic retries.</summary>
public sealed class SeerrContractClient
{
    private readonly MediaContractTransport api;
    private bool authenticated;
    private string? administratorId;
    private JellyfinLibraryIdentity[]? verifiedLibraries;
    private readonly Dictionary<ArrService, JsonObject> verifiedConnections = [];

    public SeerrContractClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http); ArrContractClient.ValidateEndpoint(http.BaseAddress);
        if (http.DefaultRequestHeaders.Any()) throw new ArgumentException("seerr.authentication-context.invalid");
        api = new(http, "seerr");
    }

    public async Task<string> GetVersionAsync(CancellationToken token)
    {
        var version = Text(Object(await api.ReadAsync("api/v1/status", token)), "version");
        if (version != "3.5.0") throw new MediaContractException("seerr.version.unsupported");
        return version;
    }

    public async Task<bool> LoginJellyfinAsync(Uri endpoint, string serverId, string userId, string username, string password, CancellationToken token)
    {
        ArrContractClient.ValidateEndpoint(endpoint); Credentials(username, password);
        if (!Guid.TryParse(serverId, out _) || !Guid.TryParse(userId, out _)) throw new ArgumentException("seerr.jellyfin.identity.invalid");
        authenticated = false; verifiedLibraries = null; verifiedConnections.Clear();
        await GetVersionAsync(token);
        try
        {
            _ = await api.ReadAsync("api/v1/auth/me", token);
            throw new MediaContractException("seerr.authentication.context-preauthenticated");
        }
        catch (MediaContractException e) when (e.Code == "seerr.read.http-error" && e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
        var state = Object(await api.ReadAsync("api/v1/settings/public", token));
        var type = Int(state, "mediaServerType");
        if (type is not (2 or 4)) throw new MediaContractException("seerr.jellyfin.conflict");
        var initial = type == 4;
        var body = new JsonObject { ["username"] = username, ["password"] = password, ["serverType"] = 2 };
        if (initial)
        {
            body["hostname"] = endpoint.Host; body["port"] = endpoint.Port;
            body["useSsl"] = endpoint.Scheme == "https"; body["urlBase"] = endpoint.AbsolutePath.TrimEnd('/');
        }
        // On an existing setup do not supply hostname: Seerr rejects it and must retain its saved endpoint.
        await api.WriteAsync(HttpMethod.Post, "api/v1/auth/jellyfin", body, HttpStatusCode.OK, false, "seerr.login", token);
        var me = Object(await api.ReadAsync("api/v1/auth/me", token));
        if (Int(me, "id") != 1 || (Int(me, "permissions") & 2) == 0 || Text(me, "jellyfinUserId") != userId)
            throw new MediaContractException("seerr.login.identity-mismatch");
        var settings = Object(await api.ReadAsync("api/v1/settings/jellyfin", token));
        if (Text(settings, "serverId") != serverId || Text(settings, "ip") != endpoint.Host || Int(settings, "port") != endpoint.Port ||
            Bool(settings, "useSsl") != (endpoint.Scheme == "https") || Text(settings, "urlBase") != endpoint.AbsolutePath.TrimEnd('/'))
            throw new MediaContractException("seerr.jellyfin.conflict");
        administratorId = userId;
        await VerifyJellyfinConnectionAsync(token);
        authenticated = true;
        return initial;
    }

    public async Task<bool> EnsureLibrariesAsync(IReadOnlyList<JellyfinLibraryIdentity> expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count != 2 || expected.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != expected.Count ||
            expected.Any(l => !Guid.TryParse(l.Id, out _) || string.IsNullOrWhiteSpace(l.Name)) ||
            expected.Count(l => l.CollectionType == "movies") != 1 || expected.Count(l => l.CollectionType == "tvshows") != 1)
            throw new ArgumentException("seerr.libraries.selection.invalid", nameof(expected));
        verifiedLibraries = null;
        RequireSession(); await GetVersionAsync(token); await VerifyJellyfinConnectionAsync(token);
        var libraries = Array(await api.ReadAsync("api/v1/settings/jellyfin/library", token)).Select(Object).ToArray();
        var changed = false;
        if (libraries.Length == 0)
        {
            await api.WriteAsync(HttpMethod.Post, "api/v1/settings/jellyfin/library/sync", null, HttpStatusCode.OK, false, "seerr.library-sync", token);
            libraries = Array(await api.ReadAsync("api/v1/settings/jellyfin/library", token)).Select(Object).ToArray();
            changed = true;
        }
        CheckLibraries(libraries, expected, requireEnabled: false);
        foreach (var library in libraries.Where(l => !Bool(l, "enabled")))
        {
            await api.WriteAsync(HttpMethod.Put, "api/v1/settings/jellyfin/library/" + Uri.EscapeDataString(Text(library, "id")),
                new() { ["enabled"] = true }, HttpStatusCode.OK, false, "seerr.library-enable", token);
            changed = true;
        }
        CheckLibraries(Array(await api.ReadAsync("api/v1/settings/jellyfin/library", token)).Select(Object).ToArray(), expected, requireEnabled: true);
        verifiedLibraries = expected.ToArray();
        return changed;
    }

    public async Task<IReadOnlyList<MediaQualityProfile>> GetQualityProfilesAsync(ArrService service, Uri endpoint, string apiKey, CancellationToken token)
    {
        RequireSession(); ValidateTarget(service, endpoint, apiKey); await GetVersionAsync(token);
        var result = await TestArrAsync(service, TargetBody(endpoint, apiKey), token);
        return Array(result["profiles"]).Select(Object).Select(p => new MediaQualityProfile(Int(p, "id"), Text(p, "name"))).ToArray();
    }

    public async Task<bool> EnsureArrAsync(SeerrArrConnection desired, string apiKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (!desired.IsValid()) throw new ArgumentException("seerr.arr.parameters.invalid", nameof(desired));
        ValidateTarget(desired.Service, desired.Endpoint, apiKey); verifiedConnections.Remove(desired.Service);
        RequireSession(); await GetVersionAsync(token);
        var route = Route(desired.Service);
        var body = TargetBody(desired.Endpoint, apiKey);
        body["name"] = "hh-homelab " + desired.Service; body["activeProfileId"] = desired.ProfileId; body["activeProfileName"] = desired.ProfileName;
        body["activeDirectory"] = desired.RootFolder; body["is4k"] = false; body["isDefault"] = true;
        body["tags"] = new JsonArray(); body["overrideRule"] = new JsonArray(); body["syncEnabled"] = true;
        body["preventSearch"] = desired.PreventSearch; body["tagRequests"] = false;
        if (desired.Service == ArrService.Radarr) body["minimumAvailability"] = "released";
        else { body["seriesType"] = "standard"; body["animeSeriesType"] = "anime"; body["enableSeasonFolders"] = true; body["monitorNewItems"] = "none"; }
        var existing = await ObserveArrAsync(desired.Service, body, token);
        var changed = existing is null;
        // Saving a server does not test it in Seerr. Require Test before and after saving.
        ValidateArrTest(await TestArrAsync(desired.Service, existing ?? body, token), body);
        if (changed)
        {
            await api.WriteAsync(HttpMethod.Post, route, body, HttpStatusCode.Created, false, "seerr.arr", token);
            existing = await ObserveArrAsync(desired.Service, body, token) ?? throw new MediaContractException("seerr.arr.not-applied");
            ValidateArrTest(await TestArrAsync(desired.Service, existing, token), body);
        }
        verifiedConnections[desired.Service] = body;
        return changed;
    }

    public async Task<bool> CompleteSetupAsync(CancellationToken token)
    {
        RequireSession(); await GetVersionAsync(token);
        if (verifiedLibraries is null || verifiedConnections.Count != 2) throw new MediaContractException("seerr.initialize.prerequisites-missing");
        await VerifyJellyfinConnectionAsync(token);
        CheckLibraries(Array(await api.ReadAsync("api/v1/settings/jellyfin/library", token)).Select(Object).ToArray(), verifiedLibraries, requireEnabled: true);
        foreach (var (service, desired) in verifiedConnections)
        {
            var existing = await ObserveArrAsync(service, desired, token) ?? throw new MediaContractException("seerr.arr.not-applied");
            ValidateArrTest(await TestArrAsync(service, existing, token), desired);
        }
        if (Bool(Object(await api.ReadAsync("api/v1/settings/public", token)), "initialized")) return false;
        await api.WriteAsync(HttpMethod.Post, "api/v1/settings/initialize", null, HttpStatusCode.OK, false, "seerr.initialize", token);
        if (!Bool(Object(await api.ReadAsync("api/v1/settings/public", token)), "initialized")) throw new MediaContractException("seerr.initialize.not-applied");
        return true;
    }

    private async Task VerifyJellyfinConnectionAsync(CancellationToken token)
    {
        var users = Array(await api.ReadAsync("api/v1/settings/jellyfin/users", token)).Select(Object).ToArray();
        if (users.Count(u => Text(u, "id") == administratorId) != 1) throw new MediaContractException("seerr.jellyfin.not-confirmed");
    }
    private async Task<JsonObject?> ObserveArrAsync(ArrService service, JsonObject desired, CancellationToken token)
    {
        var list = Array(await api.ReadAsync(Route(service), token)).Select(Object).ToArray();
        if (list.Length == 0) return null;
        if (list.Length != 1 || Int(list[0], "id") < 0 || desired.Any(p => !JsonNode.DeepEquals(list[0][p.Key], p.Value)))
            throw new MediaContractException("seerr.arr.conflict");
        return list[0];
    }
    private async Task<JsonObject> TestArrAsync(ArrService service, JsonObject payload, CancellationToken token) =>
        Object(await api.WriteAsync(HttpMethod.Post, Route(service) + "/test", payload, HttpStatusCode.OK, true, "seerr.arr-test", token));
    private static void ValidateArrTest(JsonObject test, JsonObject desired)
    {
        if (Array(test["profiles"]).Select(Object).Count(p => Int(p, "id") == Int(desired, "activeProfileId") && Text(p, "name") == Text(desired, "activeProfileName")) != 1 ||
            Array(test["rootFolders"]).Select(Object).Count(p => Text(p, "path").TrimEnd('/') == Text(desired, "activeDirectory").TrimEnd('/')) != 1 ||
            Text(test, "urlBase") != Text(desired, "baseUrl")) throw new MediaContractException("seerr.arr-test.selection-missing");
    }
    private static void CheckLibraries(JsonObject[] libraries, IReadOnlyList<JellyfinLibraryIdentity> expected, bool requireEnabled)
    {
        if (libraries.Length != expected.Count || libraries.Select(l => Text(l, "id")).Distinct(StringComparer.Ordinal).Count() != libraries.Length)
            throw new MediaContractException("seerr.libraries.conflict");
        foreach (var wanted in expected)
        {
            var actual = libraries.SingleOrDefault(l => Text(l, "id") == wanted.Id);
            if (actual is null || Text(actual, "name") != wanted.Name || Text(actual, "type") != (wanted.CollectionType == "movies" ? "movie" : "show"))
                throw new MediaContractException("seerr.libraries.conflict");
            if (requireEnabled && !Bool(actual, "enabled")) throw new MediaContractException("seerr.libraries.not-enabled");
        }
    }
    private static JsonObject TargetBody(Uri endpoint, string apiKey) => new()
    { ["hostname"] = endpoint.Host, ["port"] = endpoint.Port, ["useSsl"] = endpoint.Scheme == "https", ["baseUrl"] = endpoint.AbsolutePath.TrimEnd('/'), ["apiKey"] = apiKey };
    private static string Route(ArrService service) => "api/v1/settings/" + service.ToString().ToLowerInvariant();
    private static void ValidateTarget(ArrService service, Uri endpoint, string apiKey)
    {
        if (service is not (ArrService.Radarr or ArrService.Sonarr)) throw new ArgumentException("seerr.arr.service.invalid");
        ArrContractClient.ValidateEndpoint(endpoint);
        if (apiKey is null || apiKey.Length != 32 || !apiKey.All(char.IsAsciiHexDigit)) throw new ArgumentException("seerr.arr.api-key.invalid");
    }
    private void RequireSession()
    {
        if (!authenticated) throw new MediaContractException("seerr.session.required");
    }
}
