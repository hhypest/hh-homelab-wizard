using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<bool> Bootstrap(HomeAssistantContractClient ha) => ha.BootstrapOwnerAsync("M0 owner", "wizard", "synthetic-password", Token);
    private static HomeAssistantEntity Identity => new("registry1", "input_boolean.fixture", "fixture1", "input_boolean", null, null, null);
    private static HomeAssistantDashboard Dashboard => new("hh-homelab-m0", "M0", [Identity.EntityId]);

    [Fact]
    public async Task LostOwnerResponseUsesNormalLoginAndNeverCreatesSecondOwner()
    {
        using var f = new Fixture { LoseOwner = true }; using var h = f.Http(); using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal("ha.owner.result-unknown", e.Code);
        Assert.False(await Bootstrap(ha)); Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Fact]
    public async Task FailedLoginRetiresSessionAndCannotRename()
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.LoginOwnerAsync("M0 owner", "wizard", "wrong-password", Token)); Assert.Equal("ha.login.rejected-or-interaction-required", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.session.required", e.Code); Assert.Equal(0, f.RegistryWrites);
    }
    [Theory]
    [InlineData("core_config")]
    [InlineData("analytics")]
    [InlineData("integration")]
    public async Task LostOnboardingStepIsObservedAndNotPostedAgain(string step)
    {
        using var f = new Fixture { LoseStep = step }; using var h = f.Http(); using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal("ha.onboarding-step.result-unknown", e.Code);
        Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Fact]
    public async Task CompletedOnboardingWithoutViewsUsesNormalLogin()
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha); f.ViewsAbsent = true;
        Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Theory]
    [InlineData("lovelace")]
    [InlineData("hh-homelab/foreign")]
    [InlineData("../hh-homelab")]
    public async Task InvalidDashboardPathNeverCallsServer(string path)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h);
        await Assert.ThrowsAsync<ArgumentException>(() => ha.EnsureDashboardAsync(Dashboard with { UrlPath = path }, Token)); Assert.Equal(0, f.HttpWrites); Assert.Equal(0, f.DashboardCreates);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("preauthenticated")]
    public async Task UntrustedGreetingBlocksOwnerAndCredentialPosts(string failure)
    {
        using var f = new Fixture { Version = failure == "version" ? "2026.11.0" : "2026.10.0", Preauthenticated = failure == "preauthenticated" };
        using var h = f.Http(); using var ha = f.Client(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal(0, f.HttpWrites);
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("name")]
    [InlineData("safe_mode")]
    public async Task WrongOwnerOrSafeModeCannotEstablishSession(string failure)
    {
        using var f = new Fixture { IdentityFailure = failure }; using var h = f.Http(); using var ha = f.Client(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureDashboardAsync(Dashboard, Token)); Assert.Equal("ha.session.required", e.Code);
    }
    [Fact]
    public async Task ChangedOwnerOnReconnectIsRejectedBeforeMutation()
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha); f.OwnerId = "foreign-owner";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.ReconnectAsync(Token)); Assert.Equal("ha.owner.identity-mismatch", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.session.required", e.Code);
    }
    [Theory]
    [InlineData("config/entity_registry/update")]
    [InlineData("cancel")]
    public async Task UnknownRenameIsObservedByIdentityAndNeverRepeated(string mode)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        f.LoseCommand = "config/entity_registry/update"; f.CancelLoss = mode == "cancel";
        if (f.CancelLoss) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token));
        else { var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.ws.result-unknown", e.Code); }
        await ha.ReconnectAsync(Token); Assert.False(await ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal(1, f.RegistryWrites);
    }
    [Theory]
    [InlineData("unique_id")]
    [InlineData("platform")]
    [InlineData("device_id")]
    [InlineData("config_entry_id")]
    [InlineData("disabled_by")]
    [InlineData("entity_id")]
    [InlineData("collision")]
    public async Task RegistryIdentityConflictNeverRenames(string field)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        if (field == "collision") f.Entities.Add(Entity("other", "input_boolean.target")); else f.Entities[0]![field] = "foreign";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.entity.conflict", e.Code); Assert.Equal(0, f.RegistryWrites);
    }
    [Theory]
    [InlineData("switch.target")]
    [InlineData("input_boolean.Bad name")]
    public async Task InvalidOrCrossDomainRenameNeverReadsServer(string target)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h);
        await Assert.ThrowsAsync<ArgumentException>(() => ha.EnsureEntityIdAsync(Identity, target, Token)); Assert.Equal(0, f.HttpWrites);
    }
    [Theory]
    [InlineData("lovelace/dashboards/create")]
    [InlineData("lovelace/config/save")]
    public async Task LostDashboardWriteIsReconciledWithoutDuplicateOrOverwrite(string type)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha); f.LoseCommand = type;
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureDashboardAsync(Dashboard, Token)); Assert.Equal("ha.ws.result-unknown", e.Code);
        await ha.ReconnectAsync(Token); await ha.EnsureDashboardAsync(Dashboard, Token); Assert.False(await ha.EnsureDashboardAsync(Dashboard, Token)); Assert.Equal(1, f.DashboardCreates); Assert.Equal(1, f.DashboardSaves);
    }
    [Theory]
    [InlineData("mode")]
    [InlineData("title")]
    [InlineData("config")]
    [InlineData("entity")]
    public async Task DashboardConflictsArePreserved(string field)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha); await ha.EnsureDashboardAsync(Dashboard, Token);
        if (field == "config") f.DashboardConfig = new JsonObject { ["secret"] = "synthetic-sensitive" };
        else if (field == "entity") f.Entities[0]!["disabled_by"] = "user";
        else f.Dashboards[0]![field] = "foreign";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureDashboardAsync(Dashboard, Token)); Assert.DoesNotContain("synthetic-sensitive", e.ToString()); Assert.Equal(1, f.DashboardSaves);
    }
    [Fact]
    public async Task HttpTrialNeedsReconnectAndRunningPendingBeforePromotion()
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal(HomeAssistantHttpStage.RestartRequired, await ha.StageHttpProtectionAsync(Token));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.ConfirmHttpProtectionAsync(Token)); Assert.Equal("ha.http.reconnect-required", e.Code);
        await ha.ReconnectAsync(Token); f.Running = false;
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.ConfirmHttpProtectionAsync(Token)); Assert.Equal("ha.http.trial-not-active", e.Code); Assert.Equal(0, f.Promotes);
        f.Running = true; Assert.True(await ha.ConfirmHttpProtectionAsync(Token)); Assert.False(await ha.ConfirmHttpProtectionAsync(Token)); Assert.Equal(HomeAssistantHttpStage.Unchanged, await ha.StageHttpProtectionAsync(Token)); Assert.Equal(1, f.Configures); Assert.Equal(1, f.Promotes);
    }
    [Theory]
    [InlineData("http/config/configure")]
    [InlineData("http/config/promote")]
    public async Task HttpUnknownResultIsObservedWithoutAnotherWrite(string type)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        if (type.EndsWith("promote", StringComparison.Ordinal)) { await ha.StageHttpProtectionAsync(Token); await ha.ReconnectAsync(Token); }
        f.LoseCommand = type;
        await Assert.ThrowsAsync<MediaContractException>(async () => { if (type.EndsWith("configure", StringComparison.Ordinal)) await ha.StageHttpProtectionAsync(Token); else await ha.ConfirmHttpProtectionAsync(Token); });
        await ha.ReconnectAsync(Token);
        if (type.EndsWith("configure", StringComparison.Ordinal)) Assert.Equal(HomeAssistantHttpStage.AwaitingVerification, await ha.StageHttpProtectionAsync(Token));
        await ha.ConfirmHttpProtectionAsync(Token); Assert.Equal(1, f.Configures); Assert.Equal(1, f.Promotes);
    }
    [Theory]
    [InlineData("failed")]
    [InlineData("foreign")]
    [InlineData("reverted")]
    public async Task FailedOrForeignHttpTrialIsNeverPromoted(string reason)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha); await ha.StageHttpProtectionAsync(Token); await ha.ReconnectAsync(Token);
        if (reason == "failed") f.Pending!["error"] = "apply_failed";
        if (reason == "foreign") f.Pending!["cors_allowed_origins"] = new JsonArray("http://foreign.local");
        if (reason == "reverted") f.Active = "default";
        await Assert.ThrowsAsync<MediaContractException>(() => ha.ConfirmHttpProtectionAsync(Token)); Assert.Equal(0, f.Promotes);
    }
    [Fact]
    public async Task StartingHaCannotStageHttpRestart()
    {
        using var f = new Fixture { Running = false }; using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.StageHttpProtectionAsync(Token)); Assert.Equal("ha.ws.not-running", e.Code); Assert.Equal(0, f.Configures);
    }
    [Theory]
    [InlineData("port")]
    [InlineData("proxy")]
    [InlineData("ssl")]
    public async Task ForeignHttpBaselineCannotBeOverwritten(string reason)
    {
        using var f = new Fixture(); using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        if (reason == "port") f.Default["server_port"] = 8888;
        if (reason == "proxy") f.Default["use_x_forwarded_for"] = true;
        if (reason == "ssl") f.Default["ssl_key"] = "/config/foreign-key";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.StageHttpProtectionAsync(Token)); Assert.Equal("ha.http.baseline-conflict", e.Code); Assert.Equal(0, f.Configures);
    }
    [Fact]
    public async Task CheckConfigDoesNotExposeErrorOrWarningText()
    {
        using var f = new Fixture { BadConfig = true }; using var h = f.Http(); using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.CheckConfigurationAsync(Token)); Assert.Equal("ha.check-config.invalid-or-warning", e.Code); Assert.DoesNotContain("synthetic-sensitive", e.ToString());
    }

    private static JsonObject Entity(string id = "registry1", string entity = "input_boolean.fixture") => new()
    { ["id"] = id, ["entity_id"] = entity, ["unique_id"] = "fixture1", ["platform"] = "input_boolean", ["config_entry_id"] = null, ["device_id"] = null, ["disabled_by"] = null };

    private sealed class Fixture : HttpMessageHandler
    {
        public string Version { get; init; } = "2026.10.0";
        public bool Preauthenticated { get; init; }
        public string? IdentityFailure { get; init; }
        public string OwnerId { get; set; } = "owner1";
        public bool LoseOwner { get; init; }
        public string? LoseStep { get; init; }
        public bool ViewsAbsent { get; set; }
        public bool BadConfig { get; init; }
        public string? LoseCommand { get; set; }
        public bool CancelLoss { get; set; }
        public bool Running { get; set; } = true;
        public int OwnerWrites, StepWrites, HttpWrites, RegistryWrites, DashboardCreates, DashboardSaves, Configures, Promotes;
        public JsonArray Entities { get; } = new(Entity());
        public JsonArray Dashboards { get; } = [];
        public JsonNode? DashboardConfig { get; set; }
        public JsonObject Default { get; } = new() { ["server_port"] = 8123, ["ip_ban_enabled"] = true, ["login_attempts_threshold"] = -1, ["error"] = null, ["created_at"] = "fixture" };
        public JsonObject? Pending { get; set; }
        public JsonObject? Stable { get; set; }
        public string Active { get; set; } = "default";
        private readonly HashSet<string> done = [];
        public HttpClient Http() => new(this, disposeHandler: false) { BaseAddress = new("http://ha:8123/"), Timeout = TimeSpan.FromSeconds(5) };
        public HomeAssistantContractClient Client(HttpClient http) => new(http, () => new Socket(this));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is not null && path != "/auth/token" ? JsonNode.Parse(await request.Content.ReadAsStringAsync(token)) as JsonObject : null;
            if (request.Method != HttpMethod.Get) HttpWrites++;
            JsonNode? result = new JsonObject();
            if (path == "/api/onboarding" && ViewsAbsent) return new(HttpStatusCode.NotFound) { Content = new StringContent("synthetic-sensitive") };
            if (path == "/api/onboarding")
                result = new JsonArray(new[] { "user", "core_config", "analytics", "integration" }.Select(s => (JsonNode?)new JsonObject { ["step"] = s, ["done"] = done.Contains(s) }).ToArray());
            else if (path == "/api/onboarding/users") { OwnerWrites++; done.Add("user"); if (LoseOwner && OwnerWrites == 1) throw new HttpRequestException("synthetic-sensitive"); result = new JsonObject { ["auth_code"] = "synthetic-code" }; }
            else if (path.StartsWith("/api/onboarding/", StringComparison.Ordinal)) { StepWrites++; var step = path.Split('/')[^1]; done.Add(step); if (step == LoseStep) throw new HttpRequestException("synthetic-sensitive"); }
            else if (path == "/auth/login_flow") result = new JsonObject { ["type"] = "form", ["step_id"] = "init", ["flow_id"] = "fixture-flow" };
            else if (path == "/auth/login_flow/fixture-flow") result = body?["password"]?.GetValue<string>() == "synthetic-password" ? new JsonObject { ["type"] = "create_entry", ["result"] = "synthetic-code" } : new JsonObject { ["type"] = "form", ["errors"] = new JsonObject { ["base"] = "invalid_auth" } };
            else if (path == "/auth/token") result = new JsonObject { ["access_token"] = "synthetic-access", ["refresh_token"] = "synthetic-refresh", ["token_type"] = "Bearer" };
            else if (path == "/api/config/core/check_config") result = new JsonObject { ["result"] = BadConfig ? "invalid" : "valid", ["errors"] = BadConfig ? "synthetic-sensitive" : null, ["warnings"] = null };
            else throw new InvalidOperationException("Unexpected fixture HTTP route");
            return new(HttpStatusCode.OK) { Content = new StringContent(result!.ToJsonString()) };
        }
        private sealed class Socket(Fixture f) : IHomeAssistantSocket
        {
            private readonly Queue<JsonObject> answers = new();
            private string? current;
            public Task ConnectAsync(Uri endpoint, CancellationToken token)
            { answers.Enqueue(new() { ["type"] = f.Preauthenticated ? "auth_ok" : "auth_required", ["ha_version"] = f.Version }); return Task.CompletedTask; }
            public Task SendAsync(JsonObject msg, CancellationToken token)
            {
                current = msg["type"]!.GetValue<string>(); JsonNode? result = null; string? error = null;
                if (current == "auth") { answers.Enqueue(new() { ["type"] = "auth_ok", ["ha_version"] = f.Version }); return Task.CompletedTask; }
                switch (current)
                {
                    case "auth/current_user": result = new JsonObject { ["id"] = f.OwnerId, ["name"] = f.IdentityFailure == "name" ? "foreign" : "M0 owner", ["is_owner"] = f.IdentityFailure != "owner", ["is_admin"] = f.IdentityFailure != "admin" }; break;
                    case "get_config": result = new JsonObject { ["version"] = f.Version, ["safe_mode"] = f.IdentityFailure == "safe_mode", ["state"] = f.Running ? "RUNNING" : "STARTING" }; break;
                    case "config/entity_registry/list": result = f.Entities.DeepClone(); break;
                    case "config/entity_registry/update": f.RegistryWrites++; f.Entities[0]!["entity_id"] = msg["new_entity_id"]!.DeepClone(); result = new JsonObject(); break;
                    case "lovelace/dashboards/list": result = f.Dashboards.DeepClone(); break;
                    case "lovelace/dashboards/create": f.DashboardCreates++; var d = (JsonObject)msg.DeepClone(); d.Remove("type"); d["id"] = "dashboard1"; f.Dashboards.Add(d); result = d.DeepClone(); break;
                    case "lovelace/config": result = f.DashboardConfig?.DeepClone(); if (result is null) error = "config_not_found"; break;
                    case "lovelace/config/save": f.DashboardSaves++; f.DashboardConfig = msg["config"]!.DeepClone(); break;
                    case "http/config": result = new JsonObject { ["default"] = f.Default.DeepClone(), ["stable"] = f.Stable?.DeepClone(), ["pending"] = f.Pending?.DeepClone(), ["active_config_type"] = f.Active, ["revert_at"] = f.Pending is null ? null : "fixture-deadline" }; break;
                    case "http/config/configure": f.Configures++; f.Pending = (JsonObject)msg["config"]!.DeepClone(); f.Active = "pending"; result = new JsonObject { ["restart"] = true }; break;
                    case "http/config/promote": f.Promotes++; f.Stable = f.Pending; f.Pending = null; f.Active = "stable"; break;
                    default: throw new InvalidOperationException("Unexpected fixture WS route");
                }
                answers.Enqueue(new() { ["id"] = msg["id"]!.DeepClone(), ["type"] = "result", ["success"] = error is null, ["result"] = result,
                    ["error"] = error is null ? null : new JsonObject { ["code"] = error, ["message"] = "synthetic-sensitive" } });
                return Task.CompletedTask;
            }
            public Task<JsonObject> ReceiveAsync(CancellationToken token)
            {
                var next = answers.Dequeue();
                if (current is not null && current == f.LoseCommand) { f.LoseCommand = null; if (f.CancelLoss) throw new OperationCanceledException(); throw new HttpRequestException("synthetic-sensitive"); }
                return Task.FromResult(next);
            }
            public void Dispose() { }
        }
    }
}
