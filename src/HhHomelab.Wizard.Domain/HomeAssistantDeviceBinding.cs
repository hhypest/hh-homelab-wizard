namespace HhHomelab.Wizard.Domain;

public enum HomeAssistantDeviceBindingStatus
{
    Matched,
    EntryMissing,
    EntryPending,
    EntryFailed,
    EntryDisabled,
    DeviceMissing,
    DeviceDisabled,
    IdentityMismatch,
    Ambiguous,
}

/// <summary>Explicit expected physical identifier, never inferred from a name, host or first player.</summary>
public sealed record HomeAssistantDeviceBinding(
    HomeAssistantDeviceIntegration Integration,
    string EntryId,
    string DeviceId,
    string Identifier
);

public sealed record HomeAssistantDeviceBindingObservation(HomeAssistantDeviceBindingStatus Status);

/// <summary>Pure registry verification. Matched proves registry links, not availability or physical control.</summary>
public static class HomeAssistantDeviceBindingVerifier
{
    public static HomeAssistantDeviceBindingObservation Verify(
        HomeAssistantRoleInventory inventory,
        HomeAssistantDeviceBinding binding
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(binding);
        var domain = Domain(binding.Integration);
        var identifier = RegistryIdentifier(binding.Integration, binding.Identifier);
        if (
            string.IsNullOrWhiteSpace(binding.EntryId)
            || string.IsNullOrWhiteSpace(binding.DeviceId)
        )
            throw new ArgumentException("ha.binding.selection-invalid");
        if (
            inventory.Entries.Select(e => e.Id).Distinct().Count() != inventory.Entries.Count
            || inventory.Devices.Select(d => d.Id).Distinct().Count() != inventory.Devices.Count
        )
            return Result(HomeAssistantDeviceBindingStatus.Ambiguous);
        var entry = inventory.Entries.SingleOrDefault(e => e.Id == binding.EntryId);
        if (entry is null)
            return Result(HomeAssistantDeviceBindingStatus.EntryMissing);
        if (entry.Domain != domain)
            return Result(HomeAssistantDeviceBindingStatus.IdentityMismatch);
        if (entry.DisabledBy is not null)
            return Result(HomeAssistantDeviceBindingStatus.EntryDisabled);
        if (entry.State is "setup_in_progress" or "setup_retry")
            return Result(HomeAssistantDeviceBindingStatus.EntryPending);
        if (entry.State != "loaded")
            return Result(HomeAssistantDeviceBindingStatus.EntryFailed);
        var device = inventory.Devices.SingleOrDefault(d => d.Id == binding.DeviceId);
        if (device is null)
            return Result(HomeAssistantDeviceBindingStatus.DeviceMissing);
        if (device.DisabledBy is not null)
            return Result(HomeAssistantDeviceBindingStatus.DeviceDisabled);
        var expected = new HomeAssistantDeviceIdentifier(domain, identifier);
        var entryNotLinked = !device.ConfigEntries.Contains(binding.EntryId);
        var identifierMissing = !device.Identifiers.Contains(expected);
        var dsmNestedVia =
            binding.Integration == HomeAssistantDeviceIntegration.SynologyDsm
            && device.ViaDeviceId is not null;
        if (entryNotLinked || identifierMissing || dsmNestedVia)
            return Result(HomeAssistantDeviceBindingStatus.IdentityMismatch);
        if (
            device.Identifiers.Count(i => i.Domain == domain) != 1
            || inventory.Devices.Count(d => d.Identifiers.Contains(expected)) != 1
        )
            return Result(HomeAssistantDeviceBindingStatus.Ambiguous);
        return Result(HomeAssistantDeviceBindingStatus.Matched);
    }

    public static string RegistryIdentifier(
        HomeAssistantDeviceIntegration integration,
        string identifier
    )
    {
        _ = Domain(integration);
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 256)
            throw new ArgumentException("ha.binding.identifier-invalid");
        if (identifier.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("ha.binding.identifier-invalid");
        if (integration == HomeAssistantDeviceIntegration.GoogleCast)
        {
            // Cast entity unique_id is UUID D; device identifier is UUID N in the pinned HA.
            if (
                !Guid.TryParseExact(identifier, "D", out var uuid)
                && !Guid.TryParseExact(identifier, "N", out uuid)
            )
                throw new ArgumentException("ha.binding.identifier-invalid");
            return uuid.ToString("N");
        }
        if (integration == HomeAssistantDeviceIntegration.AndroidTvRemote)
        {
            var hex = identifier
                .Replace(":", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal);
            var hexInvalid = hex.Length != 12 || !hex.All(char.IsAsciiHexDigit);
            var compact = identifier.Length == 12;
            var separated =
                identifier.Length == 17
                && identifier[2] is ':' or '-'
                && Enumerable.Range(0, 5).All(i => identifier[i * 3 + 2] == identifier[2]);
            if (hexInvalid || !(compact || separated))
                throw new ArgumentException("ha.binding.identifier-invalid");
            return string.Join(
                ':',
                Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToLowerInvariant())
            );
        }
        return identifier; // DSM serial and LG deviceUUID retain the integration's exact value.
    }

    public static string Domain(HomeAssistantDeviceIntegration integration) =>
        integration switch
        {
            HomeAssistantDeviceIntegration.SynologyDsm => "synology_dsm",
            HomeAssistantDeviceIntegration.LgWebOs => "webostv",
            HomeAssistantDeviceIntegration.AndroidTvRemote => "androidtv_remote",
            HomeAssistantDeviceIntegration.GoogleCast => "cast",
            _ => throw new ArgumentException("ha.binding.integration-invalid"),
        };

    private static HomeAssistantDeviceBindingObservation Result(
        HomeAssistantDeviceBindingStatus status
    ) => new(status);
}
