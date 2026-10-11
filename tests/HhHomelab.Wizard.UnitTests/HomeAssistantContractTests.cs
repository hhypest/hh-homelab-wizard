using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed partial class HomeAssistantContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<bool> Bootstrap(HomeAssistantContractClient ha) => ha.BootstrapOwnerAsync("M0 owner", "wizard", "synthetic-password", Token);
    private static HomeAssistantEntity Identity => new("registry1", "input_boolean.fixture", "fixture1", "input_boolean", null, null, null);
    private static HomeAssistantDashboard Dashboard => new("hh-homelab-m0", "M0", [Identity.EntityId]);

    [Fact]
    public async Task LostOwnerResponseUsesNormalLoginAndNeverCreatesSecondOwner()
    {
        using var f = new Fixture { LoseOwner = true }; using var h = f.Http(); await using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal("ha.owner.result-unknown", e.Code);
        Assert.False(await Bootstrap(ha)); Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Fact]
    public async Task FailedLoginRetiresSessionAndCannotRename()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.LoginOwnerAsync("M0 owner", "wizard", "wrong-password", Token)); Assert.Equal("ha.login.rejected-or-interaction-required", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.session.required", e.Code); Assert.Equal(0, f.RegistryWrites);
    }
    [Theory]
    [InlineData("core_config")]
    [InlineData("analytics")]
    [InlineData("integration")]
    public async Task LostOnboardingStepIsObservedAndNotPostedAgain(string step)
    {
        using var f = new Fixture { LoseStep = step }; using var h = f.Http(); await using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal("ha.onboarding-step.result-unknown", e.Code);
        Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Fact]
    public async Task CompletedOnboardingWithoutViewsUsesNormalLogin()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); f.ViewsAbsent = true;
        Assert.False(await Bootstrap(ha)); Assert.Equal(1, f.OwnerWrites); Assert.Equal(3, f.StepWrites);
    }
    [Theory]
    [InlineData("lovelace")]
    [InlineData("hh-homelab/foreign")]
    [InlineData("../hh-homelab")]
    public async Task InvalidDashboardPathNeverCallsServer(string path)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h);
        await Assert.ThrowsAsync<ArgumentException>(() => ha.EnsureDashboardAsync(Dashboard with { UrlPath = path }, Token)); Assert.Equal(0, f.HttpWrites); Assert.Equal(0, f.DashboardCreates);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("preauthenticated")]
    public async Task UntrustedGreetingBlocksOwnerAndCredentialPosts(string failure)
    {
        using var f = new Fixture { Version = failure == "version" ? "2026.11.0" : "2026.10.0", Preauthenticated = failure == "preauthenticated" };
        using var h = f.Http(); await using var ha = f.Client(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha)); Assert.Equal(0, f.HttpWrites);
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("name")]
    [InlineData("safe_mode")]
    public async Task WrongOwnerOrSafeModeCannotEstablishSession(string failure)
    {
        using var f = new Fixture { IdentityFailure = failure }; using var h = f.Http(); await using var ha = f.Client(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureDashboardAsync(Dashboard, Token)); Assert.Equal("ha.session.required", e.Code);
    }
    [Fact]
    public async Task ChangedOwnerOnReconnectIsRejectedBeforeMutation()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); f.OwnerId = "foreign-owner";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.ReconnectAsync(Token)); Assert.Equal("ha.owner.identity-mismatch", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.session.required", e.Code);
    }
    [Theory]
    [InlineData("config/entity_registry/update")]
    [InlineData("cancel")]
    public async Task UnknownRenameIsObservedByIdentityAndNeverRepeated(string mode)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        f.LoseCommand = "config/entity_registry/update"; f.CancelLoss = mode == "cancel"; f.CallerToCancel = cancel;
        if (f.CancelLoss) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", cancel.Token));
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
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        if (field == "collision") f.Entities.Add(Entity("other", "input_boolean.target")); else f.Entities[0]![field] = "foreign";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityIdAsync(Identity, "input_boolean.target", Token)); Assert.Equal("ha.entity.conflict", e.Code); Assert.Equal(0, f.RegistryWrites);
    }
    [Theory]
    [InlineData("switch.target")]
    [InlineData("input_boolean.Bad name")]
    public async Task InvalidOrCrossDomainRenameNeverReadsServer(string target)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h);
        await Assert.ThrowsAsync<ArgumentException>(() => ha.EnsureEntityIdAsync(Identity, target, Token)); Assert.Equal(0, f.HttpWrites);
    }
    [Theory]
    [InlineData("lovelace/dashboards/create")]
    [InlineData("lovelace/config/save")]
    public async Task LostDashboardWriteIsReconciledWithoutDuplicateOrOverwrite(string type)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); f.LoseCommand = type;
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
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await ha.EnsureDashboardAsync(Dashboard, Token);
        if (field == "config") f.DashboardConfig = new JsonObject { ["secret"] = "synthetic-sensitive" };
        else if (field == "entity") f.Entities[0]!["disabled_by"] = "user";
        else f.Dashboards[0]![field] = "foreign";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureDashboardAsync(Dashboard, Token)); Assert.DoesNotContain("synthetic-sensitive", e.ToString()); Assert.Equal(1, f.DashboardSaves);
    }
    [Fact]
    public async Task HttpTrialNeedsReconnectAndRunningPendingBeforePromotion()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
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
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
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
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await ha.StageHttpProtectionAsync(Token); await ha.ReconnectAsync(Token);
        if (reason == "failed") f.Pending!["error"] = "apply_failed";
        if (reason == "foreign") f.Pending!["cors_allowed_origins"] = new JsonArray("http://foreign.local");
        if (reason == "reverted") f.Active = "default";
        await Assert.ThrowsAsync<MediaContractException>(() => ha.ConfirmHttpProtectionAsync(Token)); Assert.Equal(0, f.Promotes);
    }
    [Fact]
    public async Task StartingHaCannotStageHttpRestart()
    {
        using var f = new Fixture { Running = false }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.StageHttpProtectionAsync(Token)); Assert.Equal("ha.ws.not-running", e.Code); Assert.Equal(0, f.Configures);
    }
    [Theory]
    [InlineData("port")]
    [InlineData("proxy")]
    [InlineData("ssl")]
    public async Task ForeignHttpBaselineCannotBeOverwritten(string reason)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        if (reason == "port") f.Default["server_port"] = 8888;
        if (reason == "proxy") f.Default["use_x_forwarded_for"] = true;
        if (reason == "ssl") f.Default["ssl_key"] = "/config/foreign-key";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.StageHttpProtectionAsync(Token)); Assert.Equal("ha.http.baseline-conflict", e.Code); Assert.Equal(0, f.Configures);
    }
    [Fact]
    public async Task CheckConfigDoesNotExposeErrorOrWarningText()
    {
        using var f = new Fixture { BadConfig = true }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.CheckConfigurationAsync(Token)); Assert.Equal("ha.check-config.invalid-or-warning", e.Code); Assert.DoesNotContain("synthetic-sensitive", e.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalPendingTrialFromAnotherClientIsNeverAdopted(bool stage)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var first = f.Client(h); await Bootstrap(first);
        await first.StageHttpProtectionAsync(Token);
        using var h2 = f.Http(); await using var second = f.Client(h2); await Bootstrap(second);
        var e = await Assert.ThrowsAsync<MediaContractException>(async () =>
        { if (stage) await second.StageHttpProtectionAsync(Token); else await second.ConfirmHttpProtectionAsync(Token); });
        Assert.Equal(stage ? "ha.http.pending-conflict" : "ha.http.intent-required", e.Code);
        Assert.Equal(1, f.Configures); Assert.Equal(0, f.Promotes);
        await first.ReconnectAsync(Token); Assert.True(await first.ConfirmHttpProtectionAsync(Token));
    }

    [Fact]
    public async Task AsyncDisposeRevokesOnlyIssuedTokenAndIsIdempotent()
    {
        using var f = new Fixture(); f.ActiveRefresh.Add("foreign-refresh"); using var h = f.Http(); await using var ha = f.Client(h);
        await Bootstrap(ha); Assert.Equal(2, f.ActiveRefresh.Count);
        await ha.DisposeAsync(); await ha.DisposeAsync();
        Assert.Equal(1, f.Revokes); Assert.Equal(["foreign-refresh"], f.ActiveRefresh);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bootstrap(ha));
    }

    [Fact]
    public async Task RepeatedLoginRevokesPreviousSessionButReconnectReusesToken()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h);
        await Bootstrap(ha); await ha.ReconnectAsync(Token); Assert.Equal(0, f.Revokes); Assert.Single(f.ActiveRefresh);
        await Bootstrap(ha); Assert.Equal(1, f.Revokes); Assert.Single(f.ActiveRefresh);
        await ha.DisposeAsync(); Assert.Equal(2, f.Revokes); Assert.Empty(f.ActiveRefresh);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("name")]
    [InlineData("safe_mode")]
    public async Task ValidationFailureRevokesNewlyIssuedToken(string failure)
    {
        using var f = new Fixture { IdentityFailure = failure }; using var h = f.Http(); await using var ha = f.Client(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        Assert.Equal(1, f.Revokes); Assert.Empty(f.ActiveRefresh); Assert.Null(ha.SessionCleanupFailure);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("lost-response")]
    [InlineData("status")]
    public async Task FailedRevocationRetainsTokenForExplicitRetryAndBlocksNewLogin(string failure)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.RevokeFailure = failure;
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        Assert.Equal(failure == "status" ? "ha.session.revoke.http-error" : "ha.session.revoke.result-unknown", e.Code);
        Assert.Equal(e.Code, ha.SessionCleanupFailure); Assert.Equal(1, f.Grants);
        Assert.DoesNotContain("synthetic-sensitive", e.ToString());
        f.RevokeFailure = null; await ha.RevokeSessionAsync(Token); Assert.Null(ha.SessionCleanupFailure); Assert.Empty(f.ActiveRefresh);
        await Bootstrap(ha); Assert.Equal(2, f.Grants); Assert.Single(f.ActiveRefresh);
    }

    [Fact]
    public async Task CleanupFailurePreservesOriginalValidationErrorAndCanBeRetried()
    {
        using var f = new Fixture { IdentityFailure = "owner", RevokeFailure = "transport" }; using var h = f.Http(); await using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        Assert.Equal("ha.owner.identity-mismatch", e.Code); Assert.Equal("ha.session.revoke.result-unknown", ha.SessionCleanupFailure);
        Assert.Single(f.ActiveRefresh); f.RevokeFailure = null; await ha.DisposeAsync(); Assert.Empty(f.ActiveRefresh);
    }

    [Fact]
    public async Task ReconnectIdentityFailureRevokesExistingToken()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.OwnerId = "foreign-owner"; await Assert.ThrowsAsync<MediaContractException>(() => ha.ReconnectAsync(Token));
        Assert.Equal(1, f.Revokes); Assert.Empty(f.ActiveRefresh);
    }

    [Fact]
    public async Task FailedDisposeReportsCleanupAndCanBeExplicitlyRetried()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.RevokeFailure = "transport";
        await ha.DisposeAsync();
        Assert.Equal("ha.session.revoke.result-unknown", ha.SessionCleanupFailure); Assert.Single(f.ActiveRefresh);
        Assert.False(ha.SessionRevocationConfirmed);
        f.RevokeFailure = null; await ha.RevokeSessionAsync(Token); Assert.Empty(f.ActiveRefresh); Assert.Null(ha.SessionCleanupFailure);
        Assert.True(ha.SessionRevocationConfirmed);
    }

    [Fact]
    public async Task TransientReconnectFailureRetainsTokenForExplicitReconnect()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.LoseCommand = "auth/current_user";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.ReconnectAsync(Token));
        Assert.Equal("ha.ws.transport-failed", e.Code); Assert.Equal(0, f.Revokes); Assert.Single(f.ActiveRefresh);
        await ha.ReconnectAsync(Token); Assert.Equal(1, f.Grants); Assert.Single(f.ActiveRefresh);
    }

    [Fact]
    public async Task TokenValidationFailureRevokesCapturedRefreshToken()
    {
        using var f = new Fixture { InvalidTokenType = true }; using var h = f.Http(); await using var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Bootstrap(ha));
        Assert.Equal("ha.token.response-invalid", e.Code); Assert.Equal(1, f.Revokes); Assert.Empty(f.ActiveRefresh);
    }

    [Theory]
    [InlineData("/api/onboarding", "ha.onboarding.transport-failed")]
    [InlineData("/api/config/core/check_config", "ha.check-config.result-unknown")]
    public async Task HttpTransportTimeoutDoesNotMasqueradeAsCallerCancellation(string path, string code)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.TimeoutPath = path;
        var e = await Assert.ThrowsAsync<MediaContractException>(() => path == "/api/onboarding" ? Bootstrap(ha) : ha.CheckConfigurationAsync(Token));
        Assert.Equal(code, e.Code); Assert.False(Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AwaitUsingUnwindPreservesValidationFailureWhenBothCleanupAttemptsFail()
    {
        using var f = new Fixture { IdentityFailure = "owner", RevokeFailure = "transport" }; using var h = f.Http(); var ha = f.Client(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(async () => { await using (ha) { await Bootstrap(ha); } });
        Assert.Equal("ha.owner.identity-mismatch", e.Code); Assert.Equal(2, f.Revokes);
        Assert.Equal("ha.session.revoke.result-unknown", ha.SessionCleanupFailure);
        f.RevokeFailure = null; await ha.RevokeSessionAsync(Token); Assert.True(ha.SessionRevocationConfirmed);
    }

    [Fact]
    public async Task StartupDeadlineIsDistinctFromCallerCancellation()
    {
        using var f = new Fixture { Running = false }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.WaitUntilRunningAsync(Token, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1)));
        Assert.Equal("ha.startup.timeout", e.Code);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.WaitUntilRunningAsync(caller.Token, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task RejectedConfigureCannotAdoptLaterIdenticalTrial()
    {
        using var f = new Fixture { RejectConfigure = true }; using var h = f.Http(); await using var first = f.Client(h); await Bootstrap(first);
        await Assert.ThrowsAsync<MediaContractException>(() => first.StageHttpProtectionAsync(Token));
        f.RejectConfigure = false;
        using var h2 = f.Http(); await using var second = f.Client(h2); await Bootstrap(second); await second.StageHttpProtectionAsync(Token);
        await first.ReconnectAsync(Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => first.StageHttpProtectionAsync(Token)); Assert.Equal("ha.http.pending-conflict", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => first.ConfirmHttpProtectionAsync(Token)); Assert.Equal("ha.http.intent-required", e.Code); Assert.Equal(0, f.Promotes);
    }

    private static JsonObject Entity(string id = "registry1", string entity = "input_boolean.fixture") => new()
    { ["id"] = id, ["entity_id"] = entity, ["unique_id"] = "fixture1", ["platform"] = "input_boolean", ["config_entry_id"] = null, ["device_id"] = null, ["disabled_by"] = null };

    private sealed partial class Fixture : HttpMessageHandler
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
        public CancellationTokenSource? CallerToCancel { get; set; }
        public string? RevokeFailure { get; set; }
        public string? TimeoutPath { get; set; }
        public bool InvalidTokenType { get; init; }
        public bool RejectConfigure { get; set; }
        public string? LoseFlow { get; set; }
        public bool CancelFlow { get; set; }
        public int FlowStarts, FlowSubmits;
        public bool FlowComplete { get; set; }
        public string FlowHandler { get; set; } = "jellyfin";
        public string? FlowError { get; set; }
        public string ServerId { get; set; } = new('a', 32);
        public string EntryState { get; set; } = "loaded";
        public string? EntryDisabled { get; set; }
        public JsonArray IntegrationEntries { get; } = [];
        public JsonArray? RoleDevices { get; set; }
        public JsonObject EnableResponse { get; set; } = new() { ["require_restart"] = true };
        public bool IgnoreEnable { get; set; }
        public bool ChangeEntityAfterParentRead { get; set; }
        private JsonObject UserForm(string? error = null) => new()
        {
            ["type"] = "form", ["flow_id"] = "flow1", ["handler"] = FlowHandler, ["step_id"] = "user",
            ["errors"] = error is null ? new JsonObject() : new JsonObject { ["base"] = error },
            ["data_schema"] = new JsonArray(new JsonObject { ["name"] = "url" }, new JsonObject { ["name"] = "username" }, new JsonObject { ["name"] = "password", ["default"] = "synthetic-sensitive" })
        };
        public int Grants, Revokes;
        public HashSet<string> ActiveRefresh { get; } = [];
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
            if (path == TimeoutPath) throw new OperationCanceledException("synthetic-sensitive");
            var body = request.Content is not null && path is not ("/auth/token" or "/auth/revoke") ? JsonNode.Parse(await request.Content.ReadAsStringAsync(token)) as JsonObject : null;
            if (request.Method != HttpMethod.Get) HttpWrites++;
            JsonNode? result = new JsonObject();
            if (path == "/api/onboarding" && ViewsAbsent) return new(HttpStatusCode.NotFound) { Content = new StringContent("synthetic-sensitive") };
            if (path == "/api/onboarding")
                result = new JsonArray(new[] { "user", "core_config", "analytics", "integration" }.Select(s => (JsonNode?)new JsonObject { ["step"] = s, ["done"] = done.Contains(s) }).ToArray());
            else if (path == "/api/onboarding/users") { OwnerWrites++; done.Add("user"); if (LoseOwner && OwnerWrites == 1) throw new HttpRequestException("synthetic-sensitive"); result = new JsonObject { ["auth_code"] = "synthetic-code" }; }
            else if (path.StartsWith("/api/onboarding/", StringComparison.Ordinal)) { StepWrites++; var step = path.Split('/')[^1]; done.Add(step); if (step == LoseStep) throw new HttpRequestException("synthetic-sensitive"); }
            else if (path == "/auth/login_flow") result = new JsonObject { ["type"] = "form", ["step_id"] = "init", ["flow_id"] = "fixture-flow" };
            else if (path == "/auth/login_flow/fixture-flow") result = body?["password"]?.GetValue<string>() == "synthetic-password" ? new JsonObject { ["type"] = "create_entry", ["result"] = "synthetic-code" } : new JsonObject { ["type"] = "form", ["errors"] = new JsonObject { ["base"] = "invalid_auth" } };
            else if (path == "/auth/token")
            {
                var form = await request.Content!.ReadAsStringAsync(token);
                if (form.Contains("grant_type=refresh_token", StringComparison.Ordinal))
                {
                    var value = Uri.UnescapeDataString(form.Split('&').Single(s => s.StartsWith("refresh_token=", StringComparison.Ordinal))[14..]);
                    if (!ActiveRefresh.Contains(value)) return new(HttpStatusCode.BadRequest);
                    result = new JsonObject { ["access_token"] = "synthetic-access", ["token_type"] = "Bearer" };
                }
                else
                {
                    var value = "synthetic-refresh-" + ++Grants; ActiveRefresh.Add(value);
                    result = new JsonObject { ["access_token"] = "synthetic-access", ["refresh_token"] = value, ["token_type"] = InvalidTokenType ? "Basic" : "Bearer" };
                }
            }
            else if (path == "/auth/revoke")
            {
                Revokes++;
                Assert.Null(request.Headers.Authorization);
                if (RevokeFailure == "transport") throw new HttpRequestException("synthetic-sensitive");
                if (RevokeFailure == "status") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic-sensitive") };
                var form = await request.Content!.ReadAsStringAsync(token);
                Assert.StartsWith("token=", form); ActiveRefresh.Remove(Uri.UnescapeDataString(form[6..]));
                if (RevokeFailure == "lost-response") throw new HttpRequestException("synthetic-sensitive");
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            else if (DeviceMode && path.StartsWith("/api/config/config_entries/", StringComparison.Ordinal))
            {
                result = DeviceResponse(request, body);
                if (result is null) return new(HttpStatusCode.NotFound);
            }
            else if (path == "/api/config/config_entries/entry")
            {
                result = IntegrationEntries.DeepClone();
                if (ChangeEntityAfterParentRead) { Entities[0]!["unique_id"] = "foreign"; ChangeEntityAfterParentRead = false; }
            }
            else if (path == "/api/config/config_entries/flow") { FlowStarts++; result = UserForm(); }
            else if (path == "/api/config/config_entries/flow/flow1")
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (FlowComplete) return new(HttpStatusCode.NotFound);
                    result = UserForm(); // HA reruns async_step_user(None), clearing earlier errors.
                }
                else
                {
                    FlowSubmits++;
                    if (FlowError is not null || body?["password"]?.GetValue<string>() != "synthetic-password") result = UserForm(FlowError ?? "invalid_auth");
                    else
                    {
                        FlowComplete = true;
                        var entry = new JsonObject { ["entry_id"] = "entry1", ["domain"] = "jellyfin", ["source"] = "user", ["state"] = EntryState, ["disabled_by"] = EntryDisabled, ["title"] = "same-title" };
                        IntegrationEntries.Add(entry.DeepClone());
                        result = new JsonObject { ["type"] = "create_entry", ["flow_id"] = "flow1", ["handler"] = "jellyfin", ["result"] = entry };
                    }
                }
            }
            else if (path == "/api/config/core/check_config") result = new JsonObject { ["result"] = BadConfig ? "invalid" : "valid", ["errors"] = BadConfig ? "synthetic-sensitive" : null, ["warnings"] = null };
            else throw new InvalidOperationException("Unexpected fixture HTTP route");
            if (request.Method == HttpMethod.Post && path == LoseFlow)
            {
                LoseFlow = null;
                if (CancelFlow) { CallerToCancel!.Cancel(); throw new OperationCanceledException(token); }
                throw new HttpRequestException("synthetic-sensitive");
            }
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
                    case "config_entries/flow/progress": result = f.DeviceDiscovery.DeepClone(); break;
                    case "config/entity_registry/list": result = f.Entities.DeepClone(); break;
                    case "config/device_registry/list": result = f.RoleDevices?.DeepClone() ?? new JsonArray(new JsonObject { ["config_entries"] = new JsonArray("entry1"), ["identifiers"] = new JsonArray(new JsonArray("jellyfin", f.ServerId)), ["sw_version"] = "12.2.0" }); break;
                    case "config/entity_registry/update":
                        f.RegistryWrites++;
                        var selected = f.Entities.Select(n => (JsonObject)n!).Single(e => e["entity_id"]!.GetValue<string>() == msg["entity_id"]!.GetValue<string>());
                        if (msg.ContainsKey("new_entity_id")) selected["entity_id"] = msg["new_entity_id"]!.DeepClone();
                        if (msg.ContainsKey("disabled_by") && !f.IgnoreEnable) selected["disabled_by"] = msg["disabled_by"]?.DeepClone();
                        result = msg.ContainsKey("disabled_by") ? f.EnableResponse.DeepClone() : new JsonObject();
                        break;
                    case "lovelace/dashboards/list": result = f.Dashboards.DeepClone(); break;
                    case "lovelace/dashboards/create": f.DashboardCreates++; var d = (JsonObject)msg.DeepClone(); d.Remove("type"); d["id"] = "dashboard1"; f.Dashboards.Add(d); result = d.DeepClone(); break;
                    case "lovelace/config": result = f.DashboardConfig?.DeepClone(); if (result is null) error = "config_not_found"; break;
                    case "lovelace/config/save": f.DashboardSaves++; f.DashboardConfig = msg["config"]!.DeepClone(); break;
                    case "http/config": result = new JsonObject { ["default"] = f.Default.DeepClone(), ["stable"] = f.Stable?.DeepClone(), ["pending"] = f.Pending?.DeepClone(), ["active_config_type"] = f.Active, ["revert_at"] = f.Pending is null ? null : "fixture-deadline" }; break;
                    case "http/config/configure": f.Configures++; if (f.RejectConfigure) { error = "invalid_config"; break; } f.Pending = (JsonObject)msg["config"]!.DeepClone(); f.Active = "pending"; result = new JsonObject { ["restart"] = true }; break;
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
                if (current is not null && current == f.LoseCommand) { f.LoseCommand = null; if (f.CancelLoss) { f.CallerToCancel!.Cancel(); throw new OperationCanceledException(token); } throw new HttpRequestException("synthetic-sensitive"); }
                return Task.FromResult(next);
            }
            public void Dispose() { }
        }
    }
}
