using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

public sealed partial class HomeAssistantContractClient
{
    /// <summary>Restricted progress metadata only. Never invokes a flow step or adopts entries.</summary>
    public async Task<IReadOnlyList<HomeAssistantDiscoveredFlow>> GetDiscoveredDeviceFlowsAsync(CancellationToken token)
    {
        var result = new List<HomeAssistantDiscoveredFlow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Array(await CommandAsync("config_entries/flow/progress", null, false, token)))
        {
            var flow = Object(node);
            var domain = Text(flow, "handler");
            var integration = domain switch { "synology_dsm" => HomeAssistantDeviceIntegration.SynologyDsm, "webostv" => HomeAssistantDeviceIntegration.LgWebOs,
                "androidtv_remote" => HomeAssistantDeviceIntegration.AndroidTvRemote, "cast" => HomeAssistantDeviceIntegration.GoogleCast, _ => (HomeAssistantDeviceIntegration?)null };
            if (integration is null) continue;
            var context = Object(flow["context"]);
            var source = Text(context, "source") switch { "ssdp" => HomeAssistantDiscoverySource.Ssdp, "zeroconf" => HomeAssistantDiscoverySource.Zeroconf, _ => (HomeAssistantDiscoverySource?)null };
            if (source is null) continue; // Reauth/reconfigure are outside fresh-install discovery selection.
            if (Text(flow, "step_id") != InitialDiscoveryStep(integration.Value)) continue; // Already advanced flows are not new choices.
            var candidate = new HomeAssistantDiscoveredFlow(DeviceIdentifier(flow, "flow_id"), integration.Value, source.Value, Text(flow, "step_id"), Text(context, "unique_id"));
            ValidateDiscoveredFlow(candidate);
            if (!seen.Add(candidate.FlowId)) throw new MediaContractException("ha.discovery.snapshot-invalid");
            result.Add(candidate);
        }
        return result.AsReadOnly();
    }

    /// <summary>Explicitly selected native discovery flow in an owned, fully onboarded HA.
    /// GET reruns its step; intent is recorded before GET and an unknown result remains blocked.</summary>
    public async Task<HomeAssistantDeviceProgress> SelectDiscoveredDeviceFlowAsync(HomeAssistantDiscoveredFlow selected, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateDiscoveredFlow(selected);
        _ = await CommandAsync("get_config", null, false, token);
        if (deviceIntents.TryGetValue(selected.Integration, out var prior))
        {
            if (prior.Discovery != selected || prior.OwnerId != OwnerId) throw new MediaContractException("ha.device.intent-conflict");
            return await ObserveDeviceSetupAsync(selected.Integration, token);
        }
        if ((await OnboardingAsync(token)).Values.Any(done => !done)) throw new MediaContractException("ha.discovery.onboarding-required");
        if ((await DeviceEntriesAsync(DeviceDomain(selected.Integration), token)).Count != 0) throw new MediaContractException("ha.device.existing-entry");
        var current = await GetDiscoveredDeviceFlowsAsync(token);
        if (current.Count(c => c == selected) != 1) throw new MediaContractException("ha.discovery.selection-conflict");
        var intent = new DeviceIntent(new(selected.Integration, null), OwnerId!) { FlowId = selected.FlowId, Discovery = selected };
        deviceIntents.Add(selected.Integration, intent);
        var result = Object(await HttpAsync(HttpMethod.Get, "api/config/config_entries/flow/" + selected.FlowId, null, true, "ha.discovery.select", token));
        return AcceptDeviceResult(result, intent, starting: true);
    }

    public Task<HomeAssistantDeviceProgress> ConfirmAndroidDiscoveryAsync(CancellationToken token) =>
        SubmitDeviceAsync(HomeAssistantDeviceIntegration.AndroidTvRemote, HomeAssistantDeviceInput.AndroidDiscoveryConfirmation, new(), token);
    public Task<HomeAssistantDeviceProgress> ConfirmCastDiscoveryAsync(CancellationToken token) =>
        SubmitDeviceAsync(HomeAssistantDeviceIntegration.GoogleCast, HomeAssistantDeviceInput.CastDiscoveryConfirmation, new(), token);

    /// <summary>Explicit read-only source/entry confirmation after restart. Not permission to resume writes.</summary>
    public Task<HomeAssistantDeviceProgress> ObserveDiscoveredDeviceEntryAsync(HomeAssistantDiscoveredFlow discovery, string entryId, CancellationToken token)
    {
        ValidateDiscoveredFlow(discovery);
        return ObserveDeviceEntryCoreAsync(discovery.Integration, entryId, DiscoverySource(discovery.Source), token);
    }

    public async Task<HomeAssistantDeviceBindingObservation> ObserveDeviceBindingAsync(HomeAssistantDeviceBinding binding, CancellationToken token) =>
        HomeAssistantDeviceBindingVerifier.Verify(await GetRoleInventoryAsync(token), binding);

    private async Task VerifySelectedDiscoveryAsync(DeviceIntent intent, CancellationToken token)
    {
        var selected = intent.Discovery!;
        var matches = Array(await CommandAsync("config_entries/flow/progress", null, false, token)).Select(Object)
            .Where(f => Text(f, "flow_id") == selected.FlowId).ToArray();
        if (matches.Length != 1) throw new MediaContractException("ha.discovery.selection-conflict");
        var context = Object(matches[0]["context"]);
        var step = Text(matches[0], "step_id");
        var expectedStep = intent.Progress?.Input switch
        {
            HomeAssistantDeviceInput.DsmCredentials => step is "link" or "user",
            HomeAssistantDeviceInput.DsmOtp => step == "2sa",
            HomeAssistantDeviceInput.DsmBackupLocation => step == "backup_share",
            HomeAssistantDeviceInput.LgConfirmation => step == "pairing",
            HomeAssistantDeviceInput.AndroidDiscoveryConfirmation => step == "zeroconf_confirm",
            HomeAssistantDeviceInput.AndroidPin => step == "pair",
            HomeAssistantDeviceInput.CastDiscoveryConfirmation => step == "confirm",
            _ => false
        };
        if (!expectedStep || Text(matches[0], "handler") != DeviceDomain(selected.Integration) ||
            Text(context, "source") != intent.Source || Text(context, "unique_id") != selected.UniqueId)
            throw new MediaContractException("ha.discovery.selection-conflict");
    }

    private static string InitialDiscoveryStep(HomeAssistantDeviceIntegration integration) => integration switch
    {
        HomeAssistantDeviceIntegration.SynologyDsm => "link", HomeAssistantDeviceIntegration.LgWebOs => "pairing",
        HomeAssistantDeviceIntegration.AndroidTvRemote => "zeroconf_confirm", HomeAssistantDeviceIntegration.GoogleCast => "confirm",
        _ => throw new ArgumentException("ha.discovery.integration-invalid")
    };

    private static string DiscoverySource(HomeAssistantDiscoverySource source) => source switch
    { HomeAssistantDiscoverySource.Ssdp => "ssdp", HomeAssistantDiscoverySource.Zeroconf => "zeroconf", _ => throw new ArgumentException("ha.discovery.source-invalid") };

    private static HomeAssistantDeviceInput DiscoveryInput(HomeAssistantDiscoveredFlow discovery) => discovery.Integration switch
    {
        HomeAssistantDeviceIntegration.SynologyDsm => HomeAssistantDeviceInput.DsmCredentials,
        HomeAssistantDeviceIntegration.LgWebOs => HomeAssistantDeviceInput.LgConfirmation,
        HomeAssistantDeviceIntegration.AndroidTvRemote => HomeAssistantDeviceInput.AndroidDiscoveryConfirmation,
        HomeAssistantDeviceIntegration.GoogleCast => HomeAssistantDeviceInput.CastDiscoveryConfirmation,
        _ => throw new ArgumentException("ha.discovery.integration-invalid")
    };

    private static void ValidateDiscoveredFlow(HomeAssistantDiscoveredFlow discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery); DeviceIdentifierValue(discovery.FlowId);
        var valid = discovery.Integration switch
        {
            HomeAssistantDeviceIntegration.SynologyDsm => discovery.Source is HomeAssistantDiscoverySource.Ssdp or HomeAssistantDiscoverySource.Zeroconf && discovery.StepId == "link" && discovery.UniqueId is { Length: 12 } && discovery.UniqueId.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c)),
            HomeAssistantDeviceIntegration.LgWebOs => discovery.Source == HomeAssistantDiscoverySource.Ssdp && discovery.StepId == "pairing" && (Guid.TryParseExact(discovery.UniqueId, "D", out _) || Guid.TryParseExact(discovery.UniqueId, "N", out _)),
            HomeAssistantDeviceIntegration.AndroidTvRemote => discovery.Source == HomeAssistantDiscoverySource.Zeroconf && discovery.StepId == "zeroconf_confirm" && discovery.UniqueId is { Length: 17 } && HomeAssistantDeviceBindingVerifier.RegistryIdentifier(discovery.Integration, discovery.UniqueId) == discovery.UniqueId,
            HomeAssistantDeviceIntegration.GoogleCast => discovery.Source == HomeAssistantDiscoverySource.Zeroconf && discovery.StepId == "confirm" && discovery.UniqueId == "cast",
            _ => false
        };
        if (!valid) throw new MediaContractException("ha.discovery.contract-unsupported");
    }
}
