using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>M0 contracts for owned fresh installations. Caller supplies an exclusively owned,
/// redirect-disabled HTTP transport and serializes operations; no automatic retries or journal.</summary>
public sealed class ArrContractClient
{
    private readonly HttpClient http;
    private readonly ArrService service;
    private readonly string apiKey;
    private readonly ContractHttpTransport transport;
    private readonly ContractHttpTimeouts timeouts;
    private string Prefix => service == ArrService.Prowlarr ? "api/v1/" : "api/v3/";
    public string PinnedVersion =>
        service switch
        {
            ArrService.Radarr => "6.4.4.10685",
            ArrService.Sonarr => "4.0.20.3014",
            ArrService.Prowlarr => "2.6.5.5623",
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };

    public ArrContractClient(
        HttpClient http,
        ArrService service,
        string apiKey,
        ContractHttpTimeouts? timeouts = null
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ValidateEndpoint(http.BaseAddress);
        if (!Enum.IsDefined(service))
            throw new ArgumentOutOfRangeException(nameof(service));
        if (apiKey is null || apiKey.Length != 32 || !apiKey.All(char.IsAsciiHexDigit))
            throw new ArgumentException("arr.api-key.invalid", nameof(apiKey));
        this.http = http;
        this.service = service;
        this.apiKey = apiKey;
        this.timeouts = timeouts ?? ContractHttpTimeouts.Default;
        transport = new(http, Failure, this.timeouts);
    }

    public async Task<string> GetVersionAsync(CancellationToken token)
    {
        var status = Object(await ReadAsync("system/status", token));
        var version = Text(status, "version");
        if (version != PinnedVersion)
            throw new ArrContractException("arr.version.unsupported");
        return version;
    }

    /// <summary>UI client must have empty cookies, no authentication headers and redirects disabled.
    /// Matching credentials are verified with a fresh form login even when no write is needed.</summary>
    public async Task<bool> ConfigureAuthenticationAsync(
        string username,
        string password,
        HttpClient freshUi,
        CancellationToken token
    )
    {
        ValidateCredentials(username, password);
        ValidateEndpoint(freshUi.BaseAddress);
        var uiContextInvalid =
            freshUi.BaseAddress != http.BaseAddress
            || freshUi == http
            || freshUi.DefaultRequestHeaders.Any();
        if (uiContextInvalid)
            throw new ArgumentException("arr.ui-context.invalid", nameof(freshUi));
        await GetVersionAsync(token);
        var config = Object(await ReadAsync("config/host", token));
        var method = Text(config, "authenticationMethod");
        var required = Text(config, "authenticationRequired");
        var changed = method == "none";
        var authMethodUnexpected = method != "forms" || required != "enabled";
        var usernameDiffers = !authMethodUnexpected && Text(config, "username") != username;
        if (!changed && (authMethodUnexpected || usernameDiffers))
            throw new ArrContractException("arr.authentication.conflict");
        if (changed)
        {
            var id = Id(config);
            config["authenticationMethod"] = "forms";
            config["authenticationRequired"] = "enabled";
            config["username"] = username;
            config["password"] = password;
            config["passwordConfirmation"] = password;
            await SendAsync(
                HttpMethod.Put,
                "config/host/" + id,
                config,
                "arr.authentication",
                token
            );
            var observed = Object(await ReadAsync("config/host", token));
            if (
                Text(observed, "authenticationMethod") != "forms"
                || Text(observed, "authenticationRequired") != "enabled"
                || Text(observed, "username") != username
            )
                throw new ArrContractException("arr.authentication.not-applied");
        }
        await VerifyUiLoginAsync(freshUi, username, password, timeouts, token);
        return changed;
    }

    public async Task<bool> EnsureRootFolderAsync(string path, CancellationToken token)
    {
        RequireMediaService();
        if (
            !QbittorrentDownloadSettings.IsDataPath(path)
            || !path.StartsWith("/data/media/", StringComparison.Ordinal)
        )
            throw new ArgumentException("arr.root.path.invalid", nameof(path));
        await GetVersionAsync(token);
        if (await ObserveRootAsync(path, token))
            return false;
        await SendAsync(
            HttpMethod.Post,
            "rootfolder",
            new JsonObject { ["path"] = path },
            "arr.root",
            token
        );
        if (!await ObserveRootAsync(path, token))
            throw new ArrContractException("arr.root.not-applied");
        return true;
    }

    public async Task<bool> EnsureDownloadClientAsync(
        ArrDownloadConnection connection,
        string username,
        string password,
        CancellationToken token
    )
    {
        RequireMediaService();
        ArgumentNullException.ThrowIfNull(connection);
        if (
            !connection.IsValid()
            || connection.Category != (service == ArrService.Radarr ? "movies" : "tv")
        )
            throw new ArgumentException("arr.download.parameters.invalid", nameof(connection));
        ValidateCredentials(username, password);
        return await EnsureProviderAsync(
            "downloadclient",
            "hh-homelab qBittorrent",
            "QBittorrent",
            "QBittorrentSettings",
            new()
            {
                ["enable"] = true,
                ["protocol"] = "torrent",
                ["priority"] = 1,
                ["removeCompletedDownloads"] = true,
                ["removeFailedDownloads"] = true,
            },
            new()
            {
                ["host"] = connection.Host,
                ["port"] = connection.Port,
                ["useSsl"] = false,
                ["urlBase"] = "",
                ["username"] = username,
                ["apiKey"] = "",
                [service == ArrService.Radarr ? "movieCategory" : "tvCategory"] =
                    connection.Category,
            },
            new() { ["password"] = password },
            token
        );
    }

    public async Task<bool> EnsureApplicationAsync(
        ArrService target,
        Uri prowlarrUrl,
        Uri targetUrl,
        string targetApiKey,
        CancellationToken token
    )
    {
        if (
            service != ArrService.Prowlarr
            || target is not (ArrService.Radarr or ArrService.Sonarr)
        )
            throw new ArgumentException("arr.application.service.invalid", nameof(target));
        ValidateEndpoint(prowlarrUrl);
        ValidateEndpoint(targetUrl);
        if (
            targetApiKey is null
            || targetApiKey.Length != 32
            || !targetApiKey.All(char.IsAsciiHexDigit)
        )
            throw new ArgumentException("arr.api-key.invalid", nameof(targetApiKey));
        return await EnsureProviderAsync(
            "applications",
            "hh-homelab " + target,
            target.ToString(),
            target + "Settings",
            new() { ["syncLevel"] = "fullSync" },
            new()
            {
                ["prowlarrUrl"] = prowlarrUrl.AbsoluteUri.TrimEnd('/'),
                ["baseUrl"] = targetUrl.AbsoluteUri.TrimEnd('/'),
            },
            new() { ["apiKey"] = targetApiKey },
            token
        );
    }

    private async Task<bool> EnsureProviderAsync(
        string route,
        string name,
        string implementation,
        string contract,
        Dictionary<string, JsonNode?> properties,
        Dictionary<string, JsonNode?> fields,
        Dictionary<string, JsonNode?> secrets,
        CancellationToken token
    )
    {
        await GetVersionAsync(token);
        var existing = await FindProviderAsync(route, name, token);
        if (existing is not null)
        {
            CheckProvider(existing, implementation, contract, properties, fields);
            // Test persisted secrets through their ID; masked values are resolved by the server.
            await SendAsync(HttpMethod.Post, route + "/test", existing, "arr.provider.test", token);
            // Also test the supplied credentials without overwriting the saved provider.
            var proof = (JsonObject)existing.DeepClone();
            SetFields(proof, secrets);
            await SendAsync(HttpMethod.Post, route + "/test", proof, "arr.provider.test", token);
            return false;
        }
        var schemas = Array(await ReadAsync(route + "/schema", token));
        var candidates = schemas
            .Select(Object)
            .Where(s =>
                Text(s, "implementation") == implementation && Text(s, "configContract") == contract
            )
            .ToArray();
        if (candidates.Length != 1)
            throw new ArrContractException("arr.provider.schema-invalid");
        var desired = (JsonObject)candidates[0].DeepClone();
        desired.Remove("id");
        desired["name"] = name;
        foreach (var (key, value) in properties)
            desired[key] = value?.DeepClone();
        SetFields(desired, fields);
        SetFields(desired, secrets);
        await SendAsync(HttpMethod.Post, route + "/test", desired, "arr.provider.test", token);
        await SendAsync(HttpMethod.Post, route, desired, "arr.provider", token);
        existing =
            await FindProviderAsync(route, name, token)
            ?? throw new ArrContractException("arr.provider.not-applied");
        CheckProvider(existing, implementation, contract, properties, fields);
        await SendAsync(HttpMethod.Post, route + "/test", existing, "arr.provider.test", token);
        return true;
    }

    private async Task<JsonObject?> FindProviderAsync(
        string route,
        string name,
        CancellationToken token
    )
    {
        var providers = Array(await ReadAsync(route, token)).Select(Object).ToArray();
        // Fresh-install contracts reject unrelated definitions instead of taking over a foreign service.
        if (providers.Any(p => Text(p, "name") != name) && route == "downloadclient")
            throw new ArrContractException("arr.provider.conflict");
        if (
            route == "applications"
            && providers.Any(p =>
                Text(p, "name") is not ("hh-homelab Radarr" or "hh-homelab Sonarr")
            )
        )
            throw new ArrContractException("arr.provider.conflict");
        var matches = providers.Where(p => Text(p, "name") == name).ToArray();
        if (matches.Length > 1)
            throw new ArrContractException("arr.provider.conflict");
        if (matches.Length == 1)
            _ = Id(matches[0]);
        return matches.SingleOrDefault();
    }

    private static void CheckProvider(
        JsonObject provider,
        string implementation,
        string contract,
        Dictionary<string, JsonNode?> properties,
        Dictionary<string, JsonNode?> fields
    )
    {
        var implementationMismatch =
            Text(provider, "implementation") != implementation
            || Text(provider, "configContract") != contract;
        if (implementationMismatch)
            throw new ArrContractException("arr.provider.conflict");
        var propertiesMismatch = properties.Any(p =>
            !JsonNode.DeepEquals(provider[p.Key], p.Value)
        );
        if (propertiesMismatch)
            throw new ArrContractException("arr.provider.conflict");
        var actual = GetFields(provider);
        foreach (var (key, value) in fields)
        {
            if (!actual.TryGetValue(key, out var field))
                throw new ArrContractException("arr.provider.conflict");
            var valueMatches = JsonNode.DeepEquals(field["value"], value);
            // Arr normalizes an empty UrlBase to null.
            var emptyUrlBaseNormalized =
                key == "urlBase" && field["value"] is null && value?.ToJsonString() == "\"\"";
            if (!valueMatches && !emptyUrlBaseNormalized)
                throw new ArrContractException("arr.provider.conflict");
        }
    }

    private async Task<bool> ObserveRootAsync(string path, CancellationToken token)
    {
        var roots = Array(await ReadAsync("rootfolder", token)).Select(Object).ToArray();
        if (roots.Any(r => Text(r, "path").TrimEnd('/') != path.TrimEnd('/')) || roots.Length > 1)
            throw new ArrContractException("arr.root.conflict");
        if (roots.Length == 0)
            return false;
        _ = Id(roots[0]);
        if (
            roots[0]["accessible"] is not JsonValue accessible
            || !accessible.TryGetValue<bool>(out var value)
        )
            throw new ArrContractException("arr.response-invalid");
        if (!value)
            throw new ArrContractException("arr.root.inaccessible");
        return true;
    }

    private static Dictionary<string, JsonObject> GetFields(JsonObject provider)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var field in Array(provider["fields"]).Select(Object))
            if (!result.TryAdd(Text(field, "name"), field))
                throw new ArrContractException("arr.provider.schema-invalid");
        return result;
    }

    private static void SetFields(JsonObject provider, Dictionary<string, JsonNode?> values)
    {
        var fields = GetFields(provider);
        foreach (var (key, value) in values)
        {
            if (!fields.TryGetValue(key, out var field))
                throw new ArrContractException("arr.provider.schema-invalid");
            field["value"] = value?.DeepClone();
        }
    }

    private async Task<JsonNode?> ReadAsync(string route, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Get, route);
        return await transport.ExchangeAsync(request, "arr.read", async (response, readToken) =>
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ArrContractException("arr.read.http-error", response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(readToken);
            return await JsonNode.ParseAsync(stream, cancellationToken: readToken);
        }, token, invalidResponseCode: "arr.response-invalid");
    }

    private async Task SendAsync(
        HttpMethod method,
        string route,
        JsonObject payload,
        string code,
        CancellationToken token
    )
    {
        using var request = CreateRequest(method, route);
        request.Content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        await transport.ExchangeAsync(request, code, (response, _) =>
        {
            if (
                response.StatusCode
                is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
            )
                throw new ArrContractException(code + ".http-error", response.StatusCode);
            return Task.FromResult(true);
        }, token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, Prefix + route);
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private void RequireMediaService()
    {
        if (service == ArrService.Prowlarr)
            throw new ArgumentException("arr.media-service.required");
    }

    internal static void ValidateEndpoint(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            throw new ArgumentException("arr.endpoint.invalid");
        var shapeInvalid =
            uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !uri.AbsolutePath.EndsWith('/');
        if (shapeInvalid)
            throw new ArgumentException("arr.endpoint.invalid");
    }

    private static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl))
            throw new ArgumentException("arr.credentials.invalid");
        if (password is null || password.Length < 8 || password.Any(char.IsControl))
            throw new ArgumentException("arr.credentials.invalid");
    }

    private static JsonObject Object(JsonNode? node) =>
        node as JsonObject ?? throw new ArrContractException("arr.response-invalid");

    private static JsonArray Array(JsonNode? node) =>
        node as JsonArray ?? throw new ArrContractException("arr.response-invalid");

    private static string Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text is not null
            ? text
            : throw new ArrContractException("arr.response-invalid");

    private static int Id(JsonObject obj) =>
        obj["id"] is JsonValue value && value.TryGetValue<int>(out var id) && id > 0
            ? id
            : throw new ArrContractException("arr.response-invalid");

    private static async Task VerifyUiLoginAsync(
        HttpClient ui,
        string username,
        string password,
        ContractHttpTimeouts timeouts,
        CancellationToken token
    )
    {
        using var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "");
        var before = await SendUiAsync(ui, beforeRequest, timeouts, token);
        var location = before.Location;
        if (
            before.StatusCode != HttpStatusCode.Found
            || location is null
            || new Uri(ui.BaseAddress!, location).GetLeftPart(UriPartial.Path)
                != new Uri(ui.BaseAddress!, "login").GetLeftPart(UriPartial.Path)
        )
            throw new ArrContractException("arr.ui.context-preauthenticated-or-policy-invalid");
        using var request = new HttpRequestMessage(HttpMethod.Post, "login")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["rememberMe"] = "off",
                }
            ),
        };
        var login = await SendUiAsync(ui, request, timeouts, token);
        if (
            login.StatusCode != HttpStatusCode.Found
            || login.Location is null
            || new Uri(ui.BaseAddress!, login.Location) != ui.BaseAddress
        )
            throw new ArrContractException("arr.ui.authentication.rejected", login.StatusCode);
        using var afterRequest = new HttpRequestMessage(HttpMethod.Get, "");
        var after = await SendUiAsync(ui, afterRequest, timeouts, token);
        if (after.StatusCode != HttpStatusCode.OK)
            throw new ArrContractException("arr.ui.authentication.not-confirmed", after.StatusCode);
    }

    private static async Task<(HttpStatusCode StatusCode, Uri? Location)> SendUiAsync(
        HttpClient ui,
        HttpRequestMessage request,
        ContractHttpTimeouts timeouts,
        CancellationToken token
    )
    {
        var exchange = new ContractHttpTransport(ui, Failure, timeouts);
        return await exchange.ExchangeAsync(request, "arr.ui", static (response, _) =>
            Task.FromResult((response.StatusCode, response.Headers.Location)), token);
    }

    private static ArrContractException Failure(string code, HttpStatusCode? status, ContractFailureKind kind) =>
        new(code, status, kind);
}

public sealed class ArrContractException : ContractException
{
    internal ArrContractException(
        string code,
        HttpStatusCode? statusCode = null,
        ContractFailureKind kind = ContractFailureKind.Contract
    )
        : base(code, statusCode, kind) { }
}
