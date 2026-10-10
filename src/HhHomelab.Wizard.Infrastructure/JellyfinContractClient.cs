using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>M0 contracts for an owned fresh Jellyfin 12.2.0. Dedicated transport with redirects
/// disabled is required. Caller serializes operations and records intent before production writes.</summary>
public sealed class JellyfinContractClient
{
    private readonly MediaContractTransport api;
    private string? accessToken;
    public string? ServerId { get; private set; }
    public string? AdministratorId { get; private set; }

    public JellyfinContractClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http); ArrContractClient.ValidateEndpoint(http.BaseAddress);
        api = new(http, "jellyfin", request =>
        {
            var header = "MediaBrowser Client=\"hh-homelab-wizard M0\", Device=\"Developer probe\", DeviceId=\"hh-homelab-wizard-m0\", Version=\"0.0.0\"";
            if (accessToken is not null) header += ", Token=\"" + accessToken + "\"";
            request.Headers.TryAddWithoutValidation("Authorization", header);
        });
    }

    public async Task<bool> GetStartupCompletedAsync(CancellationToken token)
    {
        var info = Object(await api.ReadAsync("System/Info/Public", token));
        if (Text(info, "Version") != "12.2.0") throw new MediaContractException("jellyfin.version.unsupported");
        var id = Text(info, "Id");
        if (ServerId is not null && ServerId != id) throw new MediaContractException("jellyfin.server.changed");
        ServerId = id;
        return Bool(info, "StartupWizardCompleted");
    }

    public async Task LoginAdministratorAsync(string username, string password, CancellationToken token)
    {
        Credentials(username, password); accessToken = null; AdministratorId = null;
        try { await LoginAdministratorCoreAsync(username, password, token); }
        catch { accessToken = null; AdministratorId = null; throw; }
    }

    private async Task LoginAdministratorCoreAsync(string username, string password, CancellationToken token)
    {
        await GetStartupCompletedAsync(token);
        var result = Object(await api.WriteAsync(HttpMethod.Post, "Users/AuthenticateByName",
            new() { ["Username"] = username, ["Pw"] = password }, HttpStatusCode.OK, true, "jellyfin.login", token));
        var user = Object(result["User"]);
        if (Text(user, "Name") != username || !Bool(Object(user["Policy"]), "IsAdministrator") || Text(result, "ServerId") != ServerId)
            throw new MediaContractException("jellyfin.login.identity-mismatch");
        var secret = Text(result, "AccessToken");
        if (secret.Length != 32 || !secret.All(char.IsAsciiHexDigit)) throw new MediaContractException("jellyfin.login.response-invalid");
        accessToken = secret; AdministratorId = Text(user, "Id");
        var me = Object(await api.ReadAsync("Users/Me", token));
        if (Text(me, "Id") != AdministratorId || !Bool(Object(me["Policy"]), "IsAdministrator"))
        { accessToken = null; AdministratorId = null; throw new MediaContractException("jellyfin.login.not-confirmed"); }
    }

    public async Task<bool> BootstrapAsync(string username, string password, CancellationToken token)
    {
        Credentials(username, password);
        var completed = await GetStartupCompletedAsync(token);
        if (!completed)
        {
            // Never repeat Startup/User blindly: after a lost response it forbids a second password write.
            var authenticated = false;
            try { await LoginAdministratorAsync(username, password, token); authenticated = true; }
            catch (MediaContractException e) when (e.Code == "jellyfin.login.http-error" && e.StatusCode == HttpStatusCode.Unauthorized) { }
            if (!authenticated)
            {
                // Once a password exists, anonymous Startup/User is also protected. Login must
                // precede this read when reconciling a lost password-write response.
                _ = Object(await api.ReadAsync("Startup/User", token));
                await api.WriteAsync(HttpMethod.Post, "Startup/User", new() { ["Name"] = username, ["Password"] = password },
                    HttpStatusCode.NoContent, false, "jellyfin.startup-user", token);
                await LoginAdministratorAsync(username, password, token);
            }
            var desired = new JsonObject { ["ServerName"] = "hh-homelab", ["UICulture"] = "ru-RU", ["MetadataCountryCode"] = "RU", ["PreferredMetadataLanguage"] = "ru" };
            var config = Object(await api.ReadAsync("Startup/Configuration", token));
            if (!Matches(config, desired))
            {
                await api.WriteAsync(HttpMethod.Post, "Startup/Configuration", desired, HttpStatusCode.NoContent, false, "jellyfin.startup-config", token);
                if (!Matches(Object(await api.ReadAsync("Startup/Configuration", token)), desired)) throw new MediaContractException("jellyfin.startup-config.not-applied");
            }
            var network = Object(await api.ReadAsync("System/Configuration/network", token));
            if (Bool(network, "EnableRemoteAccess"))
                await api.WriteAsync(HttpMethod.Post, "Startup/RemoteAccess", new() { ["EnableRemoteAccess"] = false }, HttpStatusCode.NoContent, false, "jellyfin.remote-access", token);
            if (Bool(Object(await api.ReadAsync("System/Configuration/network", token)), "EnableRemoteAccess"))
                throw new MediaContractException("jellyfin.remote-access.not-applied");
            await api.WriteAsync(HttpMethod.Post, "Startup/Complete", null, HttpStatusCode.NoContent, false, "jellyfin.startup-complete", token);
            if (!await GetStartupCompletedAsync(token)) throw new MediaContractException("jellyfin.startup-complete.not-applied");
        }
        else
        {
            await LoginAdministratorAsync(username, password, token);
            var config = Object(await api.ReadAsync("System/Configuration", token));
            if (Text(config, "ServerName") != "hh-homelab" || Text(config, "UICulture") != "ru-RU" ||
                Text(config, "MetadataCountryCode") != "RU" || Text(config, "PreferredMetadataLanguage") != "ru" ||
                Bool(Object(await api.ReadAsync("System/Configuration/network", token)), "EnableRemoteAccess"))
                throw new MediaContractException("jellyfin.bootstrap.conflict");
        }
        return !completed;
    }

    public async Task<IReadOnlyList<JellyfinLibraryIdentity>> GetLibrariesAsync(CancellationToken token)
    {
        RequireSession(); await GetStartupCompletedAsync(token);
        return (await ReadLibrariesAsync(token)).Select(l => new JellyfinLibraryIdentity(Text(l, "ItemId"), Text(l, "Name"), Text(l, "CollectionType"))).ToArray();
    }

    public async Task<bool> EnsureLibraryAsync(MediaLibrary desired, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (!desired.IsValid()) throw new ArgumentException("jellyfin.library.parameters.invalid", nameof(desired));
        RequireSession(); await GetStartupCompletedAsync(token);
        if (await ObserveLibraryAsync(desired, token)) return false;
        var options = new JsonObject { ["PathInfos"] = new JsonArray(new JsonObject { ["Path"] = desired.Path }), ["PreferredMetadataLanguage"] = "ru", ["MetadataCountryCode"] = "RU" };
        var route = "Library/VirtualFolders?refreshLibrary=false&name=" + Uri.EscapeDataString(desired.Name) + "&collectionType=" + desired.CollectionType;
        await api.WriteAsync(HttpMethod.Post, route, new() { ["LibraryOptions"] = options }, HttpStatusCode.NoContent, false, "jellyfin.library", token);
        if (!await ObserveLibraryAsync(desired, token)) throw new MediaContractException("jellyfin.library.not-applied");
        return true;
    }

    private async Task<JsonObject[]> ReadLibrariesAsync(CancellationToken token) => Array(await api.ReadAsync("Library/VirtualFolders", token)).Select(Object).ToArray();
    private async Task<bool> ObserveLibraryAsync(MediaLibrary desired, CancellationToken token)
    {
        var libraries = await ReadLibrariesAsync(token);
        var matches = libraries.Where(l => Text(l, "Name") == desired.Name).ToArray();
        if (matches.Length > 1 || libraries.Any(l => Text(l, "Name") != desired.Name &&
            Array(l["Locations"]).Any(p => p is JsonValue value && value.TryGetValue<string>(out var location) && location?.TrimEnd('/') == desired.Path.TrimEnd('/'))))
            throw new MediaContractException("jellyfin.library.conflict");
        if (matches.Length == 0) return false;
        var library = matches[0];
        var locations = Array(library["Locations"]);
        var options = Object(library["LibraryOptions"]);
        if (Text(library, "CollectionType") != desired.CollectionType || locations.Count != 1 ||
            locations[0] is not JsonValue v || !v.TryGetValue<string>(out var path) || path?.TrimEnd('/') != desired.Path.TrimEnd('/') ||
            Text(options, "PreferredMetadataLanguage") != "ru" || Text(options, "MetadataCountryCode") != "RU")
            throw new MediaContractException("jellyfin.library.conflict");
        return true;
    }
    private void RequireSession()
    {
        if (accessToken is null || AdministratorId is null) throw new MediaContractException("jellyfin.session.required");
    }
    private static bool Matches(JsonObject current, JsonObject desired) => desired.All(p => JsonNode.DeepEquals(current[p.Key], p.Value));
}
