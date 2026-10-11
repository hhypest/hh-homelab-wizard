using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>M0 contracts for an owned HA Container 2026.10.0. Dedicated HTTP transport,
/// redirects disabled, no default authentication, no concurrent operations. Tokens stay private.
/// Resource ownership and durable intent must be supplied by the future installer.</summary>
public sealed partial class HomeAssistantContractClient : IAsyncDisposable
{
    private const string ClientId = "http://hh-homelab-wizard.local/";
    private static readonly string[] Steps = ["user", "core_config", "analytics", "integration"];
    private readonly HttpClient http;
    private readonly Func<IHomeAssistantSocket> socketFactory;
    private HomeAssistantConnection? connection;
    private string? accessToken;
    private string? refreshToken;
    private string? ownerName;
    private int generation;
    private int? stagedGeneration;
    private JsonObject? httpIntent;
    private bool disposed;
    public string? OwnerId { get; private set; }
    /// <summary>Safe diagnostic if cleanup after a failed authorization could not confirm revocation.
    /// The original failure is preserved; DisposeAsync retries cleanup without masking it.
    /// Explicit RevokeSessionAsync is required when confirmed cleanup is a success criterion.</summary>
    public string? SessionCleanupFailure { get; private set; }
    public bool SessionRevocationConfirmed => refreshToken is null;

    public HomeAssistantContractClient(HttpClient http) : this(http, () => new HomeAssistantSocket()) { }
    internal HomeAssistantContractClient(HttpClient http, Func<IHomeAssistantSocket> socketFactory)
    {
        ArgumentNullException.ThrowIfNull(http); ArrContractClient.ValidateEndpoint(http.BaseAddress);
        if (http.DefaultRequestHeaders.Any()) throw new ArgumentException("ha.authentication-context.invalid");
        this.http = http; this.socketFactory = socketFactory;
    }
    private Uri SocketEndpoint => new UriBuilder(new Uri(http.BaseAddress!, "api/websocket"))
        { Scheme = http.BaseAddress!.Scheme == "https" ? "wss" : "ws" }.Uri;

    private async Task VerifyVersionAsync(CancellationToken token)
    {
        using var socket = socketFactory();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await socket.ConnectAsync(SocketEndpoint, deadline.Token);
            var hello = await socket.ReceiveAsync(deadline.Token);
            if (Text(hello, "type") != "auth_required") throw new MediaContractException("ha.ws.authentication-context.invalid");
            if (Text(hello, "ha_version") != "2026.10.0") throw new MediaContractException("ha.version.unsupported");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new MediaContractException("ha.ws.version-unavailable"); }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or HttpRequestException or JsonException)
        { throw new MediaContractException("ha.ws.version-unavailable"); }
    }

    private async Task<Dictionary<string, bool>> OnboardingAsync(CancellationToken token)
    {
        try
        {
            var list = Array(await HttpAsync(HttpMethod.Get, "api/onboarding", null, false, "ha.onboarding", token)).Select(Object).ToArray();
            if (list.Length != Steps.Length || Steps.Any(s => list.Count(item => Text(item, "step") == s) != 1))
                throw new MediaContractException("ha.onboarding.response-invalid");
            return list.ToDictionary(item => Text(item, "step"), item => Bool(item, "done"), StringComparer.Ordinal);
        }
        catch (MediaContractException e) when (e.Code == "ha.onboarding.http-error" && e.StatusCode == HttpStatusCode.NotFound)
        { return Steps.ToDictionary(s => s, _ => true); } // Completed onboarding views are absent after restart.
    }

    public async Task<bool> BootstrapOwnerAsync(string name, string username, string password, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Credentials(username, password);
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new ArgumentException("ha.owner.name.invalid");
        await RevokeSessionAsync(token);
        try
        {
            await VerifyVersionAsync(token);
            var state = await OnboardingAsync(token);
            var created = !state["user"];
            if (created)
            {
                var result = Object(await HttpAsync(HttpMethod.Post, "api/onboarding/users", new()
                { ["name"] = name, ["username"] = username, ["password"] = password, ["client_id"] = ClientId, ["language"] = "ru" }, false, "ha.owner", token));
                await ExchangeCodeAsync(Text(result, "auth_code"), token);
                ownerName = name;
                await ConnectOwnerAsync(token);
            }
            else await LoginOwnerCoreAsync(name, username, password, token);
            state = await OnboardingAsync(token);
            foreach (var step in Steps.Where(s => s != "user"))
            {
                if (state[step]) continue;
                await HttpAsync(HttpMethod.Post, "api/onboarding/" + step, step == "integration" ?
                    new() { ["client_id"] = ClientId, ["redirect_uri"] = ClientId } : new(), true, "ha.onboarding-step", token);
                state = await OnboardingAsync(token);
                if (!state[step]) throw new MediaContractException("ha.onboarding.not-applied");
            }
            if (state.Values.Any(done => !done)) throw new MediaContractException("ha.onboarding.not-applied");
            return created;
        }
        catch { await CleanupAfterFailureAsync(); throw; }
    }

    public async Task LoginOwnerAsync(string name, string username, string password, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Credentials(username, password); await RevokeSessionAsync(token);
        try
        {
            await LoginOwnerCoreAsync(name, username, password, token);
        }
        catch { await CleanupAfterFailureAsync(); throw; }
    }

    private async Task LoginOwnerCoreAsync(string name, string username, string password, CancellationToken token)
    {
        await VerifyVersionAsync(token);
        var flow = Object(await HttpAsync(HttpMethod.Post, "auth/login_flow", new()
        { ["client_id"] = ClientId, ["redirect_uri"] = ClientId, ["handler"] = new JsonArray("homeassistant", (JsonNode?)null) }, false, "ha.login", token));
        if (Text(flow, "type") != "form" || Text(flow, "step_id") != "init") throw new MediaContractException("ha.login.interaction-required");
        var result = Object(await HttpAsync(HttpMethod.Post, "auth/login_flow/" + Uri.EscapeDataString(Text(flow, "flow_id")), new()
        { ["client_id"] = ClientId, ["username"] = username, ["password"] = password }, false, "ha.login", token));
        if (Text(result, "type") != "create_entry") throw new MediaContractException("ha.login.rejected-or-interaction-required");
        await ExchangeCodeAsync(Text(result, "result"), token); ownerName = name;
        await ConnectOwnerAsync(token);
    }

    private async Task ExchangeCodeAsync(string code, CancellationToken token) =>
        await GrantAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code }, initial: true, token);

    private async Task GrantAsync(Dictionary<string, string> fields, bool initial, CancellationToken token)
    {
        using var form = new FormUrlEncodedContent(fields);
        var result = Object(await HttpContentAsync(HttpMethod.Post, "auth/token", form, false, "ha.token", token));
        // Capture an issued refresh token before validating other fields, so rejection can revoke it.
        if (initial)
        {
            var refresh = Text(result, "refresh_token");
            if (string.IsNullOrWhiteSpace(refresh) || refresh.Any(char.IsControl)) throw new MediaContractException("ha.token.response-invalid");
            refreshToken = refresh;
        }
        if (Text(result, "token_type") != "Bearer") throw new MediaContractException("ha.token.response-invalid");
        var secret = Text(result, "access_token");
        if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl)) throw new MediaContractException("ha.token.response-invalid");
        accessToken = secret;
    }

    /// <summary>Explicit reconnect, using an in-memory refresh token. Never retries a configuration write.</summary>
    public async Task ReconnectAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (refreshToken is null || ownerName is null) throw new MediaContractException("ha.session.required");
        RetireConnection();
        try
        {
            await VerifyVersionAsync(token);
            await GrantAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = refreshToken }, initial: false, token);
            await ConnectOwnerAsync(token);
        }
        catch (MediaContractException e) when (e.Code is "ha.owner.identity-mismatch" or "ha.config.unsupported" or "ha.version.unsupported")
        { await CleanupAfterFailureAsync(); throw; }
        catch { RetireConnection(); throw; } // Retain refresh token for an explicit retry after transient reconnect failure.
    }
    private async Task ConnectOwnerAsync(CancellationToken token)
    {
        var next = new HomeAssistantConnection(socketFactory());
        try
        {
            await next.AuthenticateAsync(SocketEndpoint, accessToken!, token);
            var user = Object(await next.CommandAsync("auth/current_user", null, false, token));
            var id = Text(user, "id");
            if (!Bool(user, "is_owner") || !Bool(user, "is_admin") || Text(user, "name") != ownerName || (OwnerId is not null && id != OwnerId))
                throw new MediaContractException("ha.owner.identity-mismatch");
            var config = Object(await next.CommandAsync("get_config", null, false, token));
            if (Text(config, "version") != "2026.10.0" || Bool(config, "safe_mode")) throw new MediaContractException("ha.config.unsupported");
            OwnerId = id; connection = next; generation++;
        }
        catch { next.Dispose(); throw; }
    }

    private Task<JsonNode?> CommandAsync(string type, JsonObject? fields, bool write, CancellationToken token) =>
        (connection ?? throw new MediaContractException("ha.session.required")).CommandAsync(type, fields, write, token);

    public async Task<IReadOnlyList<HomeAssistantEntity>> GetEntitiesAsync(CancellationToken token) =>
        Array(await CommandAsync("config/entity_registry/list", null, false, token)).Select(Object).Select(e => new HomeAssistantEntity(
            Text(e, "id"), Text(e, "entity_id"), Text(e, "unique_id"), Text(e, "platform"), NullableText(e, "config_entry_id"), NullableText(e, "device_id"), NullableText(e, "disabled_by"))).ToArray();

    // Friend developer tool only: a real local helper on an owned empty fixture, never a DSM substitute.
    internal async Task<string> CreateProbeHelperAsync(CancellationToken token)
    {
        if (Array(await CommandAsync("input_boolean/list", null, false, token)).Count != 0) throw new MediaContractException("ha.fixture.not-empty");
        var created = Object(await CommandAsync("input_boolean/create", new() { ["name"] = "HH homelab M0 fixture", ["initial"] = false }, true, token));
        return Text(created, "id");
    }

    // Friend developer tool only: count metadata of this client ID, never expose raw token values.
    internal async Task<int> GetProbeSessionCountAsync(CancellationToken token) =>
        Array(await CommandAsync("auth/refresh_tokens", null, false, token)).Select(Object)
            .Count(item => NullableText(item, "client_id") == ClientId);

    public async Task<bool> EnsureEntityIdAsync(HomeAssistantEntity identity, string target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!HomeAssistantDashboard.IsEntityId(identity.EntityId) || !HomeAssistantDashboard.IsEntityId(target) ||
            identity.EntityId.Split('.')[0] != target.Split('.')[0] || string.IsNullOrWhiteSpace(identity.RegistryId) || string.IsNullOrWhiteSpace(identity.UniqueId))
            throw new ArgumentException("ha.entity.selection.invalid");
        var entities = await GetEntitiesAsync(token);
        var found = entities.Where(e => e.RegistryId == identity.RegistryId).ToArray();
        if (found.Length != 1 || found[0].UniqueId != identity.UniqueId || found[0].Platform != identity.Platform ||
            found[0].ConfigEntryId != identity.ConfigEntryId || found[0].DeviceId != identity.DeviceId || found[0].DisabledBy != identity.DisabledBy ||
            (found[0].EntityId != identity.EntityId && found[0].EntityId != target) || entities.Any(e => e.EntityId == target && e.RegistryId != identity.RegistryId))
            throw new MediaContractException("ha.entity.conflict");
        if (found[0].EntityId == target) return false;
        await CommandAsync("config/entity_registry/update", new() { ["entity_id"] = identity.EntityId, ["new_entity_id"] = target }, true, token);
        var after = await GetEntitiesAsync(token);
        if (after.Count(e => e == identity with { EntityId = target }) != 1 || after.Count(e => e.EntityId == target) != 1 ||
            after.Any(e => e.EntityId == identity.EntityId)) throw new MediaContractException("ha.entity.not-applied");
        return true;
    }

    public async Task<bool> EnsureDashboardAsync(HomeAssistantDashboard desired, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (!desired.IsValid()) throw new ArgumentException("ha.dashboard.parameters.invalid");
        var entities = await GetEntitiesAsync(token);
        if (desired.Entities.Any(id => entities.Count(e => e.EntityId == id && e.DisabledBy is null) != 1)) throw new MediaContractException("ha.dashboard.entity-missing-or-disabled");
        var list = Array(await CommandAsync("lovelace/dashboards/list", null, false, token)).Select(Object).ToArray();
        var matches = list.Where(d => Text(d, "url_path") == desired.UrlPath).ToArray();
        var changed = matches.Length == 0;
        if (matches.Length > 1 || matches.Any(d => Text(d, "mode") != "storage" || Text(d, "title") != desired.Title ||
            Bool(d, "require_admin") || !Bool(d, "show_in_sidebar"))) throw new MediaContractException("ha.dashboard.conflict");
        if (changed)
        {
            await CommandAsync("lovelace/dashboards/create", new() { ["url_path"] = desired.UrlPath, ["title"] = desired.Title,
                ["mode"] = "storage", ["require_admin"] = false, ["show_in_sidebar"] = true }, true, token);
            var observed = Array(await CommandAsync("lovelace/dashboards/list", null, false, token)).Select(Object).Where(d => Text(d, "url_path") == desired.UrlPath).ToArray();
            if (observed.Length != 1 || Text(observed[0], "title") != desired.Title || Text(observed[0], "mode") != "storage" ||
                Bool(observed[0], "require_admin") || !Bool(observed[0], "show_in_sidebar")) throw new MediaContractException("ha.dashboard.not-applied");
        }
        var config = new JsonObject { ["views"] = new JsonArray(new JsonObject { ["title"] = desired.Title, ["path"] = "infrastructure",
            ["cards"] = new JsonArray(new JsonObject { ["type"] = "entities", ["show_header_toggle"] = false,
                ["entities"] = new JsonArray(desired.Entities.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }) }) };
        JsonNode? saved;
        try { saved = await CommandAsync("lovelace/config", new() { ["url_path"] = desired.UrlPath }, false, token); }
        catch (MediaContractException e) when (e.Code == "ha.ws.config-not-found") { saved = null; }
        if (saved is not null)
        {
            if (!JsonNode.DeepEquals(saved, config)) throw new MediaContractException("ha.dashboard.config-conflict");
            return changed;
        }
        await CommandAsync("lovelace/config/save", new() { ["url_path"] = desired.UrlPath, ["config"] = config }, true, token);
        if (!JsonNode.DeepEquals(await CommandAsync("lovelace/config", new() { ["url_path"] = desired.UrlPath }, false, token), config))
            throw new MediaContractException("ha.dashboard.not-applied");
        return true;
    }

    public async Task<HomeAssistantHttpStage> StageHttpProtectionAsync(CancellationToken token)
    {
        if (Text(Object(await CommandAsync("get_config", null, false, token)), "state") != "RUNNING")
            throw new MediaContractException("ha.ws.not-running");
        var state = Object(await CommandAsync("http/config", null, false, token));
        var desired = DesiredHttp(state);
        if (state["pending"] is JsonObject pending)
        {
            if (httpIntent is null || stagedGeneration is null || !SameHttp(pending, httpIntent) || pending["error"] is not null)
                throw new MediaContractException("ha.http.pending-conflict");
            return HomeAssistantHttpStage.AwaitingVerification;
        }
        var baseline = state["stable"] as JsonObject ?? Object(state["default"]);
        if (Protected(baseline) && baseline["error"] is null && SameHttp(baseline, httpIntent ?? desired)) return HomeAssistantHttpStage.Unchanged;
        if (httpIntent is not null && !SameHttp(desired, httpIntent)) throw new MediaContractException("ha.http.baseline-conflict");
        httpIntent = desired;
        stagedGeneration = generation;
        try
        {
            var result = Object(await CommandAsync("http/config/configure", new() { ["config"] = desired }, true, token));
            if (!Bool(result, "restart")) throw new MediaContractException("ha.http.restart-not-confirmed");
        }
        catch (MediaContractException e) when (e.Code == "ha.ws.result-unknown") { throw; }
        catch (OperationCanceledException) { throw; } // A caller cancellation does not prove the write was rejected.
        catch { httpIntent = null; stagedGeneration = null; throw; }
        return HomeAssistantHttpStage.RestartRequired;
    }

    /// <summary>Bounded observation only. An open HTTP/WebSocket endpoint is not proof of completed startup.</summary>
    public Task WaitUntilRunningAsync(CancellationToken token) =>
        WaitUntilRunningAsync(token, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));

    internal async Task WaitUntilRunningAsync(CancellationToken token, TimeSpan timeout, TimeSpan interval)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        try
        {
            while (Text(Object(await CommandAsync("get_config", null, false, deadline.Token)), "state") != "RUNNING")
                await Task.Delay(interval, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new MediaContractException("ha.startup.timeout"); }
    }

    /// <summary>Explicit confirmation after reconnect and successful pending trial. Never confirms a failed trial.</summary>
    public async Task<bool> ConfirmHttpProtectionAsync(CancellationToken token)
    {
        if (stagedGeneration == generation) throw new MediaContractException("ha.http.reconnect-required");
        var config = Object(await CommandAsync("get_config", null, false, token));
        if (Text(config, "state") != "RUNNING") throw new MediaContractException("ha.http.trial-not-active");
        var state = Object(await CommandAsync("http/config", null, false, token));
        var desired = httpIntent ?? DesiredHttp(state);
        if (state["pending"] is null && state["stable"] is JsonObject stable && SameHttp(stable, desired) && stable["error"] is null && Text(state, "active_config_type") == "stable") return false;
        if (httpIntent is null || stagedGeneration is null) throw new MediaContractException("ha.http.intent-required");
        if (state["pending"] is not JsonObject pending || !SameHttp(pending, desired) || pending["error"] is not null ||
            Text(state, "active_config_type") != "pending" || state["revert_at"] is null) throw new MediaContractException("ha.http.trial-not-active");
        await CommandAsync("http/config/promote", null, true, token);
        var after = Object(await CommandAsync("http/config", null, false, token));
        if (after["pending"] is not null || after["revert_at"] is not null || after["stable"] is not JsonObject promoted ||
            !SameHttp(promoted, desired) || promoted["error"] is not null || Text(after, "active_config_type") != "stable") throw new MediaContractException("ha.http.not-promoted");
        return true;
    }
    private static JsonObject DesiredHttp(JsonObject state)
    {
        var baseline = state["stable"] as JsonObject ?? Object(state["default"]);
        if (Int(baseline, "server_port") != 8123 || baseline["ssl_certificate"] is not null || baseline["ssl_key"] is not null ||
            baseline["error"] is not null || (baseline["use_x_forwarded_for"] is not null && Bool(baseline, "use_x_forwarded_for")) ||
            (baseline["trusted_proxies"] is not null && Array(baseline["trusted_proxies"]).Count != 0)) throw new MediaContractException("ha.http.baseline-conflict");
        var desired = WithoutHttpMeta(baseline);
        desired["ip_ban_enabled"] = true; desired["login_attempts_threshold"] = 5;
        desired["use_x_forwarded_for"] = false; desired["trusted_proxies"] = new JsonArray();
        return desired;
    }
    private static JsonObject WithoutHttpMeta(JsonObject config)
    {
        var copy = (JsonObject)config.DeepClone();
        foreach (var meta in new[] { "created_at", "error", "error_message" }) copy.Remove(meta);
        return copy;
    }
    private static bool SameHttp(JsonObject actual, JsonObject desired) => Protected(actual) && JsonNode.DeepEquals(WithoutHttpMeta(actual), WithoutHttpMeta(desired));
    private static bool Protected(JsonObject config) => config["ssl_certificate"] is null && config["ssl_key"] is null &&
        Int(config, "server_port") == 8123 && Bool(config, "ip_ban_enabled") &&
        Int(config, "login_attempts_threshold") == 5 && config["use_x_forwarded_for"] is not null && !Bool(config, "use_x_forwarded_for") &&
        config["trusted_proxies"] is JsonArray { Count: 0 };

    public async Task CheckConfigurationAsync(CancellationToken token)
    {
        if (connection is null) throw new MediaContractException("ha.session.required");
        var result = Object(await HttpAsync(HttpMethod.Post, "api/config/core/check_config", new(), true, "ha.check-config", token));
        if (Text(result, "result") != "valid" || result["errors"] is not null || result["warnings"] is not null) throw new MediaContractException("ha.check-config.invalid-or-warning");
    }
    private async Task<JsonNode?> HttpAsync(HttpMethod method, string path, JsonObject? body, bool authenticated, string code, CancellationToken token)
    {
        using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await HttpContentAsync(method, path, content, authenticated, code, token);
    }
    private async Task<JsonNode?> HttpContentAsync(HttpMethod method, string path, HttpContent? content, bool authenticated, string code, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (authenticated)
        {
            if (connection is null || accessToken is null) throw new MediaContractException("ha.session.required");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw new MediaContractException(code + ".http-error", response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonNode.ParseAsync(stream, cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new MediaContractException(code + (method == HttpMethod.Get ? ".transport-failed" : ".result-unknown")); }
        catch (HttpRequestException) { throw new MediaContractException(code + (method == HttpMethod.Get ? ".transport-failed" : ".result-unknown")); }
        catch (JsonException) { throw new MediaContractException(code + (method == HttpMethod.Get ? ".response-invalid" : ".result-unknown")); }
    }
    private static string? NullableText(JsonObject obj, string key) => obj[key] is null ? null : Text(obj, key);
    private void RetireConnection()
    {
        connection?.Dispose(); connection = null; accessToken = null;
    }

    /// <summary>Revoke only this client's issued refresh token via the HA 2026.10.0 revocation endpoint.
    /// Empty 200 is valid. On failure retain the private token for explicit cleanup retry; never delete other sessions.</summary>
    public async Task RevokeSessionAsync(CancellationToken token)
    {
        RetireConnection();
        if (refreshToken is null) { ownerName = null; SessionCleanupFailure = null; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth/revoke")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }) };
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw new MediaContractException("ha.session.revoke.http-error", response.StatusCode);
            refreshToken = null; ownerName = null; SessionCleanupFailure = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { SessionCleanupFailure = "ha.session.revoke.result-unknown"; throw; }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        { SessionCleanupFailure = "ha.session.revoke.result-unknown"; throw new MediaContractException(SessionCleanupFailure); }
        catch (MediaContractException e) { SessionCleanupFailure = e.Code; throw; }
    }

    private async Task CleanupAfterFailureAsync()
    {
        try { await RevokeSessionAsync(CancellationToken.None); }
        catch (MediaContractException) { } // Preserve original failure; safe cleanup diagnostic and token remain for retry.
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try { await RevokeSessionAsync(CancellationToken.None); }
        catch (MediaContractException) { } // Never mask an operation failure during await-using unwinding.
        finally { disposed = true; }
        // Cleanup status remains visible; explicit RevokeSessionAsync can still retry on a live HTTP transport.
    }
}
