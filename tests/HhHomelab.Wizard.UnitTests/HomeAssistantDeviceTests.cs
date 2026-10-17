using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed partial class HomeAssistantContractTests
{
    private static HomeAssistantDeviceTarget DeviceTarget(
        HomeAssistantDeviceIntegration integration
    ) =>
        new(
            integration,
            integration == HomeAssistantDeviceIntegration.GoogleCast ? null : "nas-or-tv.example"
        );

    private static Task<HomeAssistantDeviceProgress> StartDevice(
        HomeAssistantContractClient ha,
        HomeAssistantDeviceIntegration integration
    ) => ha.StartDeviceSetupAsync(DeviceTarget(integration), Token);

    private static async Task<HomeAssistantDeviceProgress> AdvanceDevice(
        HomeAssistantContractClient ha,
        HomeAssistantDeviceIntegration integration
    )
    {
        if (integration == HomeAssistantDeviceIntegration.SynologyDsm)
            return await ha.SubmitDsmCredentialsAsync(
                "wizard",
                "synthetic-password",
                5001,
                true,
                Token
            );
        if (integration == HomeAssistantDeviceIntegration.GoogleCast)
            return await ha.SubmitCastHostsAsync([], Token);
        var host = await ha.SubmitDeviceHostAsync(integration, Token);
        Assert.Equal(
            integration == HomeAssistantDeviceIntegration.LgWebOs
                ? HomeAssistantDeviceInput.LgConfirmation
                : HomeAssistantDeviceInput.AndroidPin,
            host.Input
        );
        return integration == HomeAssistantDeviceIntegration.LgWebOs
            ? await ha.ConfirmLgPairingAsync(Token)
            : await ha.SubmitAndroidPinAsync("ABCDEF", Token);
    }

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast)]
    public async Task DeviceFlowsLoadOnlySelectedIntegrationAndRepeatReadsWithoutAnotherWrite(
        HomeAssistantDeviceIntegration integration
    )
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        Assert.Equal(
            HomeAssistantDeviceSetupStage.AwaitingInput,
            (await StartDevice(ha, integration)).Stage
        );
        var progress = await AdvanceDevice(ha, integration);
        Assert.Equal(HomeAssistantDeviceSetupStage.EntryLoaded, progress.Stage);
        Assert.Equal("entry1", progress.EntryId);
        var writes = f.FlowSubmits;
        Assert.Equal(progress, await StartDevice(ha, integration));
        Assert.Equal(progress, await ha.ObserveDeviceEntryAsync(integration, "entry1", Token));
        Assert.Equal(1, f.FlowStarts);
        Assert.Equal(writes, f.FlowSubmits);
        if (integration == HomeAssistantDeviceIntegration.SynologyDsm)
        {
            Assert.True(f.DeviceInputs[0]["verify_ssl"]!.GetValue<bool>());
            Assert.True(f.DeviceInputs[0]["ssl"]!.GetValue<bool>());
            Assert.Equal("5001", f.DeviceInputs[0]["port"]!.GetValue<string>());
        }
        if (integration == HomeAssistantDeviceIntegration.GoogleCast)
            Assert.Empty((JsonArray)f.DeviceInputs[0]["known_hosts"]!);
    }

    [Fact]
    public async Task DsmOtpAndBackupRequireExplicitInputWithTechnicalShareSelection()
    {
        using var f = new Fixture
        {
            DeviceMode = true,
            DsmOtp = true,
            DsmBackup = true,
        };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.SynologyDsm);
        Assert.Equal(
            HomeAssistantDeviceInput.DsmOtp,
            (
                await ha.SubmitDsmCredentialsAsync(
                    "wizard",
                    "synthetic-password",
                    5001,
                    true,
                    Token
                )
            ).Input
        );
        Assert.Equal(
            HomeAssistantDeviceInput.DsmOtp,
            (
                await ha.ObserveDeviceSetupAsync(HomeAssistantDeviceIntegration.SynologyDsm, Token)
            ).Input
        );
        var rejected = await ha.SubmitDsmOtpAsync("000000", Token);
        Assert.Equal(HomeAssistantDeviceProblem.OtpRejected, rejected.Problem);
        Assert.Equal(
            rejected,
            await ha.ObserveDeviceSetupAsync(HomeAssistantDeviceIntegration.SynologyDsm, Token)
        );
        var backup = await ha.SubmitDsmOtpAsync("123456", Token);
        Assert.Equal(HomeAssistantDeviceInput.DsmBackupLocation, backup.Input);
        Assert.Equal(new[] { "/volume1/backups", "/volume2/archive" }, backup.BackupShares);
        Assert.DoesNotContain(
            "synthetic-sensitive",
            System.Text.Json.JsonSerializer.Serialize(backup)
        );
        var e = await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitDsmBackupLocationAsync("/volume1/foreign", "hh-homelab", Token)
        );
        Assert.Equal("ha.device.backup-share-conflict", e.Code);
        Assert.Equal(3, f.FlowSubmits);
        Assert.Equal(
            HomeAssistantDeviceSetupStage.EntryLoaded,
            (await ha.SubmitDsmBackupLocationAsync("/volume1/backups", "hh-homelab", Token)).Stage
        );
        Assert.Equal(0, f.OtpReads);
    }

    [Theory]
    [InlineData(
        HomeAssistantDeviceIntegration.SynologyDsm,
        "username",
        "invalid_auth",
        HomeAssistantDeviceProblem.InvalidAuth
    )]
    [InlineData(
        HomeAssistantDeviceIntegration.SynologyDsm,
        "host",
        "cannot_connect",
        HomeAssistantDeviceProblem.CannotConnect
    )]
    [InlineData(
        HomeAssistantDeviceIntegration.SynologyDsm,
        "base",
        "missing_data",
        HomeAssistantDeviceProblem.MissingData
    )]
    [InlineData(
        HomeAssistantDeviceIntegration.LgWebOs,
        "base",
        "error_pairing",
        HomeAssistantDeviceProblem.PairingRejected
    )]
    [InlineData(
        HomeAssistantDeviceIntegration.LgWebOs,
        "base",
        "cannot_connect",
        HomeAssistantDeviceProblem.CannotConnect
    )]
    [InlineData(
        HomeAssistantDeviceIntegration.AndroidTvRemote,
        "base",
        "invalid_auth",
        HomeAssistantDeviceProblem.InvalidAuth
    )]
    public async Task AcknowledgedRejectionRemainsVisibleWhenGetClearsErrorsAndAllowsExplicitCorrection(
        HomeAssistantDeviceIntegration integration,
        string field,
        string error,
        HomeAssistantDeviceProblem problem
    )
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, integration);
        if (integration != HomeAssistantDeviceIntegration.SynologyDsm)
            await ha.SubmitDeviceHostAsync(integration, Token);
        f.DeviceError = new() { [field] = error };
        var rejected =
            integration == HomeAssistantDeviceIntegration.SynologyDsm
                ? await ha.SubmitDsmCredentialsAsync(
                    "wizard",
                    "synthetic-password",
                    5001,
                    true,
                    Token
                )
            : integration == HomeAssistantDeviceIntegration.LgWebOs
                ? await ha.ConfirmLgPairingAsync(Token)
            : await ha.SubmitAndroidPinAsync("ABCDEF", Token);
        Assert.Equal(problem, rejected.Problem);
        Assert.Equal(problem, (await ha.ObserveDeviceSetupAsync(integration, Token)).Problem);
        var corrected =
            integration == HomeAssistantDeviceIntegration.SynologyDsm
                ? await ha.SubmitDsmCredentialsAsync(
                    "wizard",
                    "synthetic-password",
                    5001,
                    true,
                    Token
                )
            : integration == HomeAssistantDeviceIntegration.LgWebOs
                ? await ha.ConfirmLgPairingAsync(Token)
            : await ha.SubmitAndroidPinAsync("ABCDEF", Token);
        Assert.Equal(HomeAssistantDeviceSetupStage.EntryLoaded, corrected.Stage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnknownStartNeverCreatesASecondFlow(bool cancelled, bool cast)
    {
        using var f = new Fixture
        {
            DeviceMode = true,
            LoseFlow = "/api/config/config_entries/flow",
            CancelFlow = cancelled,
        };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        using var caller = new CancellationTokenSource();
        f.CallerToCancel = caller;
        var integration = cast
            ? HomeAssistantDeviceIntegration.GoogleCast
            : HomeAssistantDeviceIntegration.LgWebOs;
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ha.StartDeviceSetupAsync(DeviceTarget(integration), caller.Token)
            );
        else
            Assert.Equal(
                "ha.device.start.result-unknown",
                (
                    await Assert.ThrowsAsync<MediaContractException>(() =>
                        StartDevice(ha, integration)
                    )
                ).Code
            );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await StartDevice(ha, integration)).Stage
        );
        Assert.Equal(1, f.FlowStarts);
        Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm, false)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs, false)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote, false)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast, false)]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm, true)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs, true)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote, true)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast, true)]
    public async Task LostFinalResponseOrCancellationNeverAdoptsEntryOrResendsPairing(
        HomeAssistantDeviceIntegration integration,
        bool cancelled
    )
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, integration);
        if (
            integration
            is HomeAssistantDeviceIntegration.LgWebOs
                or HomeAssistantDeviceIntegration.AndroidTvRemote
        )
            await ha.SubmitDeviceHostAsync(integration, Token);
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        f.CancelFlow = cancelled;
        using var caller = new CancellationTokenSource();
        f.CallerToCancel = caller;
        Task<HomeAssistantDeviceProgress> Submit(CancellationToken token) =>
            integration switch
            {
                HomeAssistantDeviceIntegration.SynologyDsm => ha.SubmitDsmCredentialsAsync(
                    "wizard",
                    "synthetic-password",
                    5001,
                    true,
                    token
                ),
                HomeAssistantDeviceIntegration.LgWebOs => ha.ConfirmLgPairingAsync(token),
                HomeAssistantDeviceIntegration.AndroidTvRemote => ha.SubmitAndroidPinAsync(
                    "ABCDEF",
                    token
                ),
                _ => ha.SubmitCastHostsAsync([], token),
            };
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Submit(caller.Token));
        else
            Assert.Equal(
                "ha.device.submit.result-unknown",
                (await Assert.ThrowsAsync<MediaContractException>(() => Submit(Token))).Code
            );
        var writes = f.FlowSubmits;
        Assert.Single(f.IntegrationEntries);
        Assert.Equal(HomeAssistantDeviceSetupStage.OutcomeUnknown, (await Submit(Token)).Stage);
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.ObserveDeviceSetupAsync(integration, Token)).Stage
        );
        Assert.Equal(writes, f.FlowSubmits);
    }

    [Fact]
    public async Task LostHostResponseDoesNotContinueWithPinEvenIfServerEnteredPairStep()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.AndroidTvRemote);
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitDeviceHostAsync(HomeAssistantDeviceIntegration.AndroidTvRemote, Token)
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitAndroidPinAsync("ABCDEF", Token)).Stage
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Fact]
    public async Task LostRejectedPinIsStillUnknownAndNeverAutomaticallyCorrected()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.AndroidTvRemote);
        await ha.SubmitDeviceHostAsync(HomeAssistantDeviceIntegration.AndroidTvRemote, Token);
        f.DeviceError = new() { ["base"] = "invalid_auth" };
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitAndroidPinAsync("BADBAD", Token)
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitAndroidPinAsync("ABCDEF", Token)).Stage
        );
        Assert.Empty(f.IntegrationEntries);
        Assert.Equal(2, f.FlowSubmits);
    }

    [Fact]
    public async Task LostOtpAcknowledgementDoesNotResendOrSelectAPath()
    {
        using var f = new Fixture
        {
            DeviceMode = true,
            DsmOtp = true,
            DsmBackup = true,
        };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.SynologyDsm);
        await ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token);
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitDsmOtpAsync("123456", Token)
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitDsmOtpAsync("123456", Token)).Stage
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitDsmBackupLocationAsync("/volume1/backups", "hh-homelab", Token)).Stage
        );
        Assert.Equal(2, f.FlowSubmits);
        Assert.Equal(0, f.OtpReads);
    }

    [Fact]
    public async Task ExpiredFormCannotBeResurrectedByAnotherStartOrSubmit()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.LgWebOs);
        f.FlowComplete = true;
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitDeviceHostAsync(HomeAssistantDeviceIntegration.LgWebOs, Token)).Stage
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await StartDevice(ha, HomeAssistantDeviceIntegration.LgWebOs)).Stage
        );
        Assert.Equal(1, f.FlowStarts);
        Assert.Equal(0, f.FlowSubmits);
    }

    [Fact]
    public async Task MalformedSuccessfulCreationDoesNotAdoptTheEntryOrRetry()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.GoogleCast);
        f.AlterDeviceResult = r =>
        {
            if (r["type"]?.GetValue<string>() == "create_entry")
                r["result"]!["source"] = "foreign";
        };
        Assert.Equal(
            "ha.device.response-invalid",
            (
                await Assert.ThrowsAsync<MediaContractException>(() =>
                    ha.SubmitCastHostsAsync([], Token)
                )
            ).Code
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitCastHostsAsync([], Token)).Stage
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Theory]
    [InlineData("/volume1/backups", "/volume1/backups")]
    [InlineData("/volume1/../backups", "/volume2/archive")]
    public async Task DuplicateOrUnsafeBackupSelectorBlocksContinuation(string first, string second)
    {
        using var f = new Fixture { DeviceMode = true, DsmBackup = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.SynologyDsm);
        f.AlterDeviceResult = r =>
        {
            if (r["step_id"]?.GetValue<string>() != "backup_share")
                return;
            ((JsonArray)r["data_schema"]!)[0]!["selector"]!["select"]!["options"] = new JsonArray(
                new JsonObject { ["value"] = first },
                new JsonObject { ["value"] = second }
            );
        };
        await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token)
        );
        Assert.Equal(
            HomeAssistantDeviceSetupStage.OutcomeUnknown,
            (await ha.SubmitDsmBackupLocationAsync("/volume1/backups", "hh-homelab", Token)).Stage
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Fact]
    public async Task NewClientDoesNotAdoptUserEntryButCanReadItsExplicitId()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.GoogleCast);
        var progress = await ha.SubmitCastHostsAsync([], Token);
        using var h2 = f.Http();
        await using var second = f.Client(h2);
        await Bootstrap(second);
        Assert.Equal(
            "ha.device.existing-entry",
            (
                await Assert.ThrowsAsync<MediaContractException>(() =>
                    StartDevice(second, HomeAssistantDeviceIntegration.GoogleCast)
                )
            ).Code
        );
        Assert.Equal(
            progress,
            await second.ObserveDeviceEntryAsync(
                HomeAssistantDeviceIntegration.GoogleCast,
                "entry1",
                Token
            )
        );
        Assert.Equal(1, f.FlowStarts);
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("flow_id")]
    [InlineData("schema")]
    [InlineData("step_id")]
    [InlineData("errors")]
    public async Task ChangedFlowContractNeverSendsCredentials(string change)
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.SynologyDsm);
        f.AlterDeviceResult = r =>
        {
            if (change == "schema")
                ((JsonArray)r["data_schema"]!).Add(new JsonObject { ["name"] = "foreign" });
            else if (change == "errors")
                r["errors"] = new JsonObject { ["base"] = "synthetic-sensitive" };
            else
                r[change] = "foreign";
        };
        var e = await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token)
        );
        Assert.DoesNotContain("synthetic-sensitive", e.ToString());
        Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingEntryOrDiscoveryFlowBlocksFreshSetup(bool discovery)
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        if (discovery)
            f.DeviceDiscovery.Add(
                new JsonObject { ["handler"] = "webostv", ["flow_id"] = "foreign" }
            );
        else
            f.IntegrationEntries.Add(new JsonObject { ["domain"] = "webostv" });
        var e = await Assert.ThrowsAsync<MediaContractException>(() =>
            StartDevice(ha, HomeAssistantDeviceIntegration.LgWebOs)
        );
        Assert.Equal(
            discovery ? "ha.device.discovery-flow-present" : "ha.device.existing-entry",
            e.Code
        );
        Assert.Equal(0, f.FlowStarts);
    }

    [Fact]
    public async Task EntryAppearingBetweenStepsBlocksPairingWithoutDeletingOrAdoptingIt()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.LgWebOs);
        await ha.SubmitDeviceHostAsync(HomeAssistantDeviceIntegration.LgWebOs, Token);
        f.IntegrationEntries.Add(new JsonObject { ["domain"] = "webostv" });
        Assert.Equal(
            "ha.device.existing-entry",
            (
                await Assert.ThrowsAsync<MediaContractException>(() =>
                    ha.ConfirmLgPairingAsync(Token)
                )
            ).Code
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Theory]
    [InlineData("setup_retry")]
    [InlineData("setup_in_progress")]
    public async Task SetupPendingIsNotDeviceReadinessAndLoadedReadbackDoesNotWrite(string state)
    {
        using var f = new Fixture { DeviceMode = true, EntryState = state };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.GoogleCast);
        Assert.Equal(
            HomeAssistantDeviceSetupStage.AwaitingSetup,
            (await ha.SubmitCastHostsAsync([], Token)).Stage
        );
        f.IntegrationEntries[0]!["state"] = "loaded";
        Assert.Equal(
            HomeAssistantDeviceSetupStage.EntryLoaded,
            (
                await ha.ObserveDeviceSetupAsync(HomeAssistantDeviceIntegration.GoogleCast, Token)
            ).Stage
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("failed")]
    [InlineData("source")]
    [InlineData("duplicate")]
    public async Task EntryReadbackRejectsConflicts(string reason)
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.GoogleCast);
        await ha.SubmitCastHostsAsync([], Token);
        if (reason == "disabled")
            f.IntegrationEntries[0]!["disabled_by"] = "user";
        if (reason == "failed")
            f.IntegrationEntries[0]!["state"] = "setup_error";
        if (reason == "source")
            f.IntegrationEntries[0]!["source"] = "discovery";
        if (reason == "duplicate")
            f.IntegrationEntries.Add(f.IntegrationEntries[0]!.DeepClone());
        await Assert.ThrowsAsync<MediaContractException>(() =>
            ha.ObserveDeviceSetupAsync(HomeAssistantDeviceIntegration.GoogleCast, Token)
        );
        Assert.Equal(1, f.FlowSubmits);
    }

    [Theory]
    [InlineData("https://nas.example")]
    [InlineData("user@nas")]
    [InlineData("nas:5001")]
    [InlineData("nas/path")]
    [InlineData("nas\n")]
    public async Task InvalidHostNeverContactsServer(string host)
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ha.StartDeviceSetupAsync(new(HomeAssistantDeviceIntegration.SynologyDsm, host), Token)
        );
        Assert.Equal(0, f.HttpWrites);
    }

    [Fact]
    public async Task WrongStepTargetAndInvalidInputsNeverDispatchMutation()
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.AndroidTvRemote);
        Assert.Equal(
            "ha.device.wrong-step",
            (
                await Assert.ThrowsAsync<MediaContractException>(() =>
                    ha.SubmitAndroidPinAsync("ABCDEF", Token)
                )
            ).Code
        );
        Assert.Equal(
            "ha.device.intent-conflict",
            (
                await Assert.ThrowsAsync<MediaContractException>(() =>
                    ha.StartDeviceSetupAsync(
                        new(HomeAssistantDeviceIntegration.AndroidTvRemote, "foreign.example"),
                        Token
                    )
                )
            ).Code
        );
        await Assert.ThrowsAsync<ArgumentException>(() => ha.SubmitAndroidPinAsync("ABC\n", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => ha.SubmitDsmOtpAsync("ABCDEF", Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 0, true, Token)
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ha.SubmitDsmBackupLocationAsync("/volume1/backups", "../foreign", Token)
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ha.SubmitCastHostsAsync(["a.example", "A.example"], Token)
        );
        Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData("already_configured", HomeAssistantDeviceProblem.AlreadyConfigured)]
    [InlineData("already_in_progress", HomeAssistantDeviceProblem.AlreadyInProgress)]
    [InlineData("cannot_connect", HomeAssistantDeviceProblem.CannotConnect)]
    public async Task AbortIsTerminalAndNeverTreatedAsLoaded(
        string reason,
        HomeAssistantDeviceProblem expected
    )
    {
        using var f = new Fixture { DeviceMode = true };
        using var h = f.Http();
        await using var ha = f.Client(h);
        await Bootstrap(ha);
        await StartDevice(ha, HomeAssistantDeviceIntegration.LgWebOs);
        await ha.SubmitDeviceHostAsync(HomeAssistantDeviceIntegration.LgWebOs, Token);
        f.DeviceAbort = reason;
        var progress = await ha.ConfirmLgPairingAsync(Token);
        Assert.Equal(HomeAssistantDeviceSetupStage.Aborted, progress.Stage);
        Assert.Equal(expected, progress.Problem);
        Assert.Equal(progress, await ha.ConfirmLgPairingAsync(Token));
        Assert.Equal(2, f.FlowSubmits);
    }

    private sealed partial class Fixture
    {
        public bool DeviceMode { get; init; }
        public bool DsmOtp { get; init; }
        public bool DsmBackup { get; init; }
        public int OtpReads { get; private set; }
        public JsonArray DeviceDiscovery { get; } = [];
        public List<JsonObject> DeviceInputs { get; } = [];
        public JsonObject? DeviceError { get; set; }
        public string? DeviceAbort { get; set; }
        public Action<JsonObject>? AlterDeviceResult { get; set; }
        private string deviceDomain = "";
        private string deviceStep = "";
        private string deviceSource = "user";
        private string deviceFlowId = "flow1";
        private bool otpAccepted;

        private JsonObject DeviceForm(JsonObject? errors = null)
        {
            string[] names = (deviceDomain, deviceStep) switch
            {
                ("synology_dsm", "user") =>
                [
                    "host",
                    "username",
                    "password",
                    "port",
                    "ssl",
                    "verify_ssl",
                ],
                ("synology_dsm", "link") => ["username", "password", "port", "ssl", "verify_ssl"],
                (_, "2sa") => ["otp_code"],
                (_, "backup_share") => ["backup_share", "backup_path"],
                (_, "zeroconf_confirm" or "confirm") => [],
                (_, "pairing") => [],
                (_, "pair") => ["pin"],
                (_, "config") => ["known_hosts"],
                _ => ["host"],
            };
            var schema = new JsonArray(
                names
                    .Select(name =>
                        (JsonNode?)
                            new JsonObject
                            {
                                ["name"] = name,
                                ["default"] = "synthetic-sensitive",
                                ["type"] = "string",
                            }
                    )
                    .ToArray()
            );
            if (deviceDomain == "synology_dsm" && deviceSource != "user" && deviceStep == "user")
                schema[0]!["default"] = "192.0.2.1";
            if (deviceStep == "backup_share")
                schema[0]!["selector"] = new JsonObject
                {
                    ["select"] = new JsonObject
                    {
                        ["options"] = new JsonArray(
                            new JsonObject
                            {
                                ["value"] = "/volume1/backups",
                                ["label"] = "synthetic-sensitive",
                            },
                            new JsonObject
                            {
                                ["value"] = "/volume2/archive",
                                ["label"] = "synthetic-sensitive",
                            }
                        ),
                    },
                };
            return new()
            {
                ["type"] = "form",
                ["handler"] = deviceDomain,
                ["flow_id"] = deviceFlowId,
                ["step_id"] = deviceStep,
                ["data_schema"] = schema,
                ["errors"] =
                    errors
                    ?? (
                        deviceDomain == "webostv" && deviceStep == "user" || deviceDomain == "cast"
                            ? null
                            : new JsonObject()
                    ),
                ["description_placeholders"] = new JsonObject
                {
                    ["name"] = "synthetic-sensitive",
                    ["host"] = "192.0.2.1",
                },
            };
        }

        private JsonNode? DeviceResponse(HttpRequestMessage request, JsonObject? body)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonObject result;
            if (path.EndsWith("/entry", StringComparison.Ordinal))
            {
                var domain = request.RequestUri.Query.Split('=')[^1];
                return new JsonArray(
                    IntegrationEntries
                        .Select(n => (JsonObject)n!)
                        .Where(e => domain == "" || e["domain"]?.GetValue<string>() == domain)
                        .Select(e => e.DeepClone())
                        .ToArray()
                );
            }
            if (path.EndsWith("/flow", StringComparison.Ordinal))
            {
                FlowStarts++;
                deviceSource = "user";
                deviceFlowId = "flow1";
                deviceDomain = body!["handler"]!.GetValue<string>();
                deviceStep = deviceDomain == "cast" ? "config" : "user";
                result = DeviceForm();
            }
            else if (request.Method == HttpMethod.Get)
            {
                if (deviceSource != "user")
                {
                    var flow = DeviceDiscovery
                        .Select(n => (JsonObject)n!)
                        .SingleOrDefault(f =>
                            f["flow_id"]?.GetValue<string>() == path.Split('/')[^1]
                        );
                    if (flow is null)
                        return null;
                    deviceFlowId = flow["flow_id"]!.GetValue<string>();
                    deviceStep = flow["step_id"]!.GetValue<string>();
                    deviceDomain = flow["handler"]!.GetValue<string>();
                    deviceSource = flow["context"]!["source"]!.GetValue<string>();
                }
                if (FlowComplete)
                    return null;
                if (deviceStep == "2sa")
                {
                    OtpReads++;
                    throw new InvalidOperationException("Pinned DSM OTP GET must not be called");
                }
                result = DeviceForm(); // GET clears previously acknowledged errors.
            }
            else
            {
                FlowSubmits++;
                DeviceInputs.Add((JsonObject)body!.DeepClone());
                if (DeviceAbort is { } reason)
                {
                    FlowComplete = true;
                    result = new()
                    {
                        ["type"] = "abort",
                        ["handler"] = deviceDomain,
                        ["flow_id"] = deviceFlowId,
                        ["reason"] = reason,
                    };
                }
                else if (DeviceError is { } errors)
                {
                    result = DeviceForm(errors);
                    DeviceError = null;
                }
                else if (deviceStep == "user" && deviceDomain is "webostv" or "androidtv_remote")
                {
                    deviceStep = deviceDomain == "webostv" ? "pairing" : "pair";
                    result = DeviceForm();
                }
                else if (deviceStep == "zeroconf_confirm")
                {
                    deviceStep = "pair";
                    result = DeviceForm();
                }
                else if (deviceDomain == "synology_dsm" && DsmOtp && !otpAccepted)
                {
                    var otp = body["otp_code"]?.GetValue<string>();
                    deviceStep = "2sa";
                    if (otp == "123456")
                    {
                        otpAccepted = true;
                        deviceStep = DsmBackup ? "backup_share" : "done";
                        result = DsmBackup ? DeviceForm() : CreateDeviceEntry();
                    }
                    else
                        result = DeviceForm(
                            otp is null ? null : new JsonObject { ["otp_code"] = "otp_failed" }
                        );
                }
                else if (
                    deviceDomain == "synology_dsm"
                    && DsmBackup
                    && deviceStep != "backup_share"
                )
                {
                    deviceStep = "backup_share";
                    result = DeviceForm();
                }
                else
                    result = CreateDeviceEntry();
            }
            AlterDeviceResult?.Invoke(result);
            if (deviceSource != "user")
            {
                var selected = DeviceDiscovery
                    .Select(n => (JsonObject)n!)
                    .SingleOrDefault(f => f["flow_id"]?.GetValue<string>() == deviceFlowId);
                if (selected is not null)
                {
                    if (FlowComplete)
                        DeviceDiscovery.Remove(selected);
                    else
                        selected["step_id"] = deviceStep;
                }
            }
            return result;
        }

        private JsonObject CreateDeviceEntry()
        {
            FlowComplete = true;
            var entry = new JsonObject
            {
                ["entry_id"] = "entry1",
                ["domain"] = deviceDomain,
                ["source"] = deviceSource,
                ["state"] = EntryState,
                ["disabled_by"] = EntryDisabled,
                ["title"] = "synthetic-sensitive",
                ["data"] = new JsonObject { ["password"] = "synthetic-sensitive" },
            };
            IntegrationEntries.Add(entry.DeepClone());
            return new()
            {
                ["type"] = "create_entry",
                ["handler"] = deviceDomain,
                ["flow_id"] = deviceFlowId,
                ["result"] = entry,
            };
        }
    }
}
