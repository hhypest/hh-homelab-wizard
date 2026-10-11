using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed partial class HomeAssistantContractTests
{
    private static HomeAssistantDiscoveredFlow Discovered(HomeAssistantDeviceIntegration integration, string id = "flow1") => integration switch
    {
        HomeAssistantDeviceIntegration.SynologyDsm => new(id, integration, HomeAssistantDiscoverySource.Ssdp, "link", "AABBCCDDEEFF"),
        HomeAssistantDeviceIntegration.LgWebOs => new(id, integration, HomeAssistantDiscoverySource.Ssdp, "pairing", "00000000-0000-0000-0000-000000000001"),
        HomeAssistantDeviceIntegration.AndroidTvRemote => new(id, integration, HomeAssistantDiscoverySource.Zeroconf, "zeroconf_confirm", "aa:bb:cc:dd:ee:ff"),
        _ => new(id, integration, HomeAssistantDiscoverySource.Zeroconf, "confirm", "cast")
    };
    private static Task<HomeAssistantDeviceProgress> SubmitDiscovery(HomeAssistantContractClient ha, HomeAssistantDeviceIntegration integration) => integration switch
    {
        HomeAssistantDeviceIntegration.SynologyDsm => ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token),
        HomeAssistantDeviceIntegration.LgWebOs => ha.ConfirmLgPairingAsync(Token),
        HomeAssistantDeviceIntegration.AndroidTvRemote => ha.ConfirmAndroidDiscoveryAsync(Token),
        _ => ha.ConfirmCastDiscoveryAsync(Token)
    };

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast)]
    public async Task ExplicitDiscoverySelectionContinuesOriginalFlowAndPreservesEntrySource(HomeAssistantDeviceIntegration integration)
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(integration)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var candidates = await ha.GetDiscoveredDeviceFlowsAsync(Token); Assert.Single(candidates);
        Assert.DoesNotContain("synthetic-sensitive", System.Text.Json.JsonSerializer.Serialize(candidates));
        var selected = candidates[0]; Assert.Equal(HomeAssistantDeviceSetupStage.AwaitingInput, (await ha.SelectDiscoveredDeviceFlowAsync(selected, Token)).Stage);
        var progress = await SubmitDiscovery(ha, integration);
        if (integration == HomeAssistantDeviceIntegration.AndroidTvRemote)
        { Assert.Equal(HomeAssistantDeviceInput.AndroidPin, progress.Input); progress = await ha.SubmitAndroidPinAsync("ABCDEF", Token); }
        Assert.Equal(HomeAssistantDeviceSetupStage.EntryLoaded, progress.Stage);
        Assert.Equal(progress, await ha.SelectDiscoveredDeviceFlowAsync(selected, Token));
        Assert.Equal(progress, await ha.ObserveDiscoveredDeviceEntryAsync(selected, "entry1", Token));
        Assert.Equal(0, f.FlowStarts);
        if (integration == HomeAssistantDeviceIntegration.SynologyDsm) Assert.False(f.DeviceInputs[0].ContainsKey("host"));
        Assert.Equal("ha.device.entry-missing-or-conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.ObserveDeviceEntryAsync(integration, "entry1", Token))).Code);
    }

    [Fact]
    public async Task DiscoveryListUsesTechnicalIdentityAndAllowsExplicitSecondChoice()
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.LgWebOs));
        var second = Discovered(HomeAssistantDeviceIntegration.LgWebOs, "flow2") with { UniqueId = "00000000-0000-0000-0000-000000000002" }; f.AddDiscovery(second);
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var candidates = await ha.GetDiscoveredDeviceFlowsAsync(Token); Assert.Equal(2, candidates.Count);
        Assert.Equal(HomeAssistantDeviceInput.LgConfirmation, (await ha.SelectDiscoveredDeviceFlowAsync(candidates.Single(c => c.FlowId == "flow2"), Token)).Input);
        Assert.Equal("ha.device.intent-conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SelectDiscoveredDeviceFlowAsync(candidates[0], Token))).Code); Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData("flow_id")]
    [InlineData("handler")]
    [InlineData("step_id")]
    [InlineData("source")]
    [InlineData("unique_id")]
    public async Task ChangedDiscoverySnapshotBlocksSelection(string field)
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.LgWebOs)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = (await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single();
        if (field is "source" or "unique_id") f.DeviceDiscovery[0]!["context"]![field] = field == "source" ? "reauth" : "00000000-0000-0000-0000-000000000002";
        else f.DeviceDiscovery[0]![field] = field == "handler" ? "foreign" : field == "step_id" ? "reauth_confirm" : "flow2";
        Assert.Equal("ha.discovery.selection-conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SelectDiscoveredDeviceFlowAsync(selected, Token))).Code); Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("step_id")]
    [InlineData("source")]
    [InlineData("unique_id")]
    [InlineData("removed")]
    public async Task SelectedDiscoveryIdentityIsRecheckedBeforePairing(string field)
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.LgWebOs)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        await ha.SelectDiscoveredDeviceFlowAsync((await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single(), Token);
        if (field == "removed") f.DeviceDiscovery.Clear();
        else if (field is "source" or "unique_id") f.DeviceDiscovery[0]!["context"]![field] = "foreign";
        else f.DeviceDiscovery[0]![field] = "foreign";
        Assert.Equal("ha.discovery.selection-conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.ConfirmLgPairingAsync(Token))).Code); Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast)]
    public async Task LostDiscoverySubmitDoesNotRepeatOrAdoptCreatedEntry(HomeAssistantDeviceIntegration integration)
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(integration)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = (await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single(); await ha.SelectDiscoveredDeviceFlowAsync(selected, Token);
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        await Assert.ThrowsAsync<MediaContractException>(() => SubmitDiscovery(ha, integration));
        Assert.Equal(HomeAssistantDeviceSetupStage.OutcomeUnknown, (await SubmitDiscovery(ha, integration)).Stage);
        Assert.Equal(HomeAssistantDeviceSetupStage.OutcomeUnknown, (await ha.SelectDiscoveredDeviceFlowAsync(selected, Token)).Stage); Assert.Equal(1, f.FlowSubmits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostSelectionGetNeverContinuesTheUnknownFlow(bool cancelled)
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.LgWebOs)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = (await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single();
        using var caller = new CancellationTokenSource(); f.CallerToCancel = caller;
        f.DeviceSelectionLoss = true; f.CancelFlow = cancelled;
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.SelectDiscoveredDeviceFlowAsync(selected, caller.Token));
        else Assert.Equal("ha.discovery.select.transport-failed", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SelectDiscoveredDeviceFlowAsync(selected, Token))).Code);
        Assert.Equal(HomeAssistantDeviceSetupStage.OutcomeUnknown, (await ha.ConfirmLgPairingAsync(Token)).Stage);
        Assert.Equal(0, f.FlowStarts); Assert.Equal(0, f.FlowSubmits);
    }

    [Fact]
    public async Task ExistingEntryAndUnfinishedOnboardingCannotBeBypassedByDiscoverySelection()
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.GoogleCast)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = (await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single(); f.MarkOnboardingIncomplete();
        Assert.Equal("ha.discovery.onboarding-required", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SelectDiscoveredDeviceFlowAsync(selected, Token))).Code);
        await Bootstrap(ha); f.IntegrationEntries.Add(new JsonObject { ["domain"] = "cast" });
        Assert.Equal("ha.device.existing-entry", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SelectDiscoveredDeviceFlowAsync(selected, Token))).Code); Assert.Equal(0, f.FlowSubmits);
    }

    [Fact]
    public async Task ReauthForeignAndAdvancedFlowsAreExcludedAndDuplicateIdsRejected()
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.LgWebOs)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        f.DeviceDiscovery.Add(new JsonObject { ["handler"] = "foreign" });
        var reauth = f.DeviceDiscovery[0]!.DeepClone(); reauth["context"]!["source"] = "reauth"; f.DeviceDiscovery.Add(reauth);
        var advanced = f.DeviceDiscovery[0]!.DeepClone(); advanced["step_id"] = "other_step"; f.DeviceDiscovery.Add(advanced);
        Assert.Single(await ha.GetDiscoveredDeviceFlowsAsync(Token));
        f.DeviceDiscovery.Add(f.DeviceDiscovery[0]!.DeepClone());
        Assert.Equal("ha.discovery.snapshot-invalid", (await Assert.ThrowsAsync<MediaContractException>(() => ha.GetDiscoveredDeviceFlowsAsync(Token))).Code);
    }

    [Fact]
    public async Task DsmDiscoveryOtpCanReturnUserFormAndRetryOnlyTheAcknowledgedHost()
    {
        using var f = new Fixture { DeviceMode = true, DsmOtp = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.SynologyDsm)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        await ha.SelectDiscoveredDeviceFlowAsync((await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single(), Token);
        await ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token);
        f.AlterDeviceResult = r =>
        {
            if (r["step_id"]?.GetValue<string>() != "2sa") return;
            f.ReturnDsmUserForm(r);
        };
        Assert.Equal(HomeAssistantDeviceProblem.InvalidAuth, (await ha.SubmitDsmOtpAsync("000000", Token)).Problem);
        f.AlterDeviceResult = null;
        Assert.Equal(HomeAssistantDeviceInput.DsmOtp, (await ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token)).Input);
        Assert.Equal("192.0.2.1", f.DeviceInputs[^1]["host"]!.GetValue<string>()); Assert.Equal(0, f.OtpReads);
    }

    [Fact]
    public async Task ChangedDiscoveredDsmHostBlocksCredentials()
    {
        using var f = new Fixture { DeviceMode = true }; f.AddDiscovery(Discovered(HomeAssistantDeviceIntegration.SynologyDsm)); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        await ha.SelectDiscoveredDeviceFlowAsync((await ha.GetDiscoveredDeviceFlowsAsync(Token)).Single(), Token);
        f.AlterDeviceResult = r => r["description_placeholders"]!["host"] = "192.0.2.2";
        Assert.Equal("ha.discovery.host-conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.SubmitDsmCredentialsAsync("wizard", "synthetic-password", 5001, true, Token))).Code); Assert.Equal(0, f.FlowSubmits);
    }

    [Fact]
    public async Task ReadOnlyBindingDoesNotReturnNamesCredentialsOrWriteToRegistry()
    {
        using var f = new Fixture { RoleDevices = [] }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var result = await ha.ObserveDeviceBindingAsync(new(HomeAssistantDeviceIntegration.SynologyDsm, "missing", "missing", "SERIAL"), Token);
        Assert.Equal(HomeAssistantDeviceBindingStatus.EntryMissing, result.Status); Assert.Equal(0, f.RegistryWrites);
        Assert.DoesNotContain("synthetic", result.ToString());
    }

    private sealed partial class Fixture
    {
        public bool DeviceSelectionLoss { get; set; }
        public void MarkOnboardingIncomplete() => done.Remove("integration");
        public void AddDiscovery(HomeAssistantDiscoveredFlow flow)
        {
            deviceSource = flow.Source == HomeAssistantDiscoverySource.Ssdp ? "ssdp" : "zeroconf";
            deviceDomain = HomeAssistantDeviceBindingVerifier.Domain(flow.Integration); deviceFlowId = flow.FlowId; deviceStep = flow.StepId;
            DeviceDiscovery.Add(new JsonObject { ["flow_id"] = flow.FlowId, ["handler"] = deviceDomain, ["step_id"] = flow.StepId,
                ["context"] = new JsonObject { ["source"] = deviceSource, ["unique_id"] = flow.UniqueId, ["title_placeholders"] = new JsonObject { ["password"] = "synthetic-sensitive" }, ["other"] = "synthetic-sensitive" } });
        }
        public void ReturnDsmUserForm(JsonObject result)
        {
            deviceStep = "user";
            var replacement = DeviceForm(new() { ["username"] = "invalid_auth" }); result.Clear();
            foreach (var field in replacement) result.Add(field.Key, field.Value?.DeepClone());
        }
    }
}
