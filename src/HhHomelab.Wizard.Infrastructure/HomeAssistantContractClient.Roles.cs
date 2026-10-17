using System.Globalization;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

public sealed partial class HomeAssistantContractClient
{
    public async Task<HomeAssistantRoleInventory> GetRoleInventoryAsync(CancellationToken token)
    {
        var entities = Array(await CommandAsync("config/entity_registry/list", null, false, token))
            .Select(Object)
            .Select(e => new HomeAssistantRoleEntity(
                ReadRoleIdentity(e),
                NullableText(e, "translation_key"),
                NullableText(e, "hidden_by")
            ))
            .ToArray();
        var devices = Array(await CommandAsync("config/device_registry/list", null, false, token))
            .Select(Object)
            .Select(d => new HomeAssistantRoleDevice(
                Text(d, "id"),
                Array(d["config_entries"]).Select(n => n!.GetValue<string>()).ToArray(),
                Array(d["identifiers"])
                    .Select(n => Array(n))
                    .Select(pair =>
                        pair.Count == 2
                            ? new HomeAssistantDeviceIdentifier(
                                pair[0]!.GetValue<string>(),
                                pair[1]!.GetValue<string>()
                            )
                            : throw new MediaContractException("ha.roles.snapshot-invalid")
                    )
                    .ToArray(),
                NullableText(d, "via_device_id"),
                NullableText(d, "disabled_by")
            ))
            .ToArray();
        var entries = Array(
                await HttpAsync(
                    HttpMethod.Get,
                    "api/config/config_entries/entry",
                    null,
                    true,
                    "ha.roles.entries",
                    token
                )
            )
            .Select(Object)
            .Select(e => new HomeAssistantRoleEntry(
                Text(e, "entry_id"),
                Text(e, "domain"),
                Text(e, "state"),
                NullableText(e, "disabled_by")
            ))
            .ToArray();
        var entityIdsDuplicated =
            entities.Select(e => e.Identity.RegistryId).Distinct().Count() != entities.Length
            || entities.Select(e => e.Identity.EntityId).Distinct().Count() != entities.Length;
        var deviceIdsDuplicated = devices.Select(d => d.Id).Distinct().Count() != devices.Length;
        var entryIdsDuplicated = entries.Select(e => e.Id).Distinct().Count() != entries.Length;
        if (entityIdsDuplicated || deviceIdsDuplicated || entryIdsDuplicated)
            throw new MediaContractException("ha.roles.snapshot-invalid");
        return new(entities, devices, entries);
    }

    /// <summary>Explicit selected identity only. Registry enable is not proof of live measurements.
    /// A lost response requires observation; it never causes an automatic transport retry.</summary>
    public async Task<HomeAssistantEnableStage> EnsureEntityEnabledAsync(
        HomeAssistantEntity identity,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        var selectionInvalid =
            !HomeAssistantDashboard.IsEntityId(identity.EntityId)
            || string.IsNullOrWhiteSpace(identity.RegistryId)
            || string.IsNullOrWhiteSpace(identity.UniqueId)
            || string.IsNullOrWhiteSpace(identity.Platform);
        if (selectionInvalid)
            throw new ArgumentException("ha.entity.selection.invalid");
        var observed = (await GetEntitiesAsync(token))
            .Where(e => e.RegistryId == identity.RegistryId)
            .ToArray();
        if (observed.Length != 1)
            throw new MediaContractException("ha.entity.conflict");
        var current = observed[0];
        var identityChanged = current with { DisabledBy = identity.DisabledBy } != identity;
        var disabledUnexpectedly =
            current.DisabledBy != identity.DisabledBy && current.DisabledBy is not null;
        if (identityChanged || disabledUnexpectedly)
            throw new MediaContractException("ha.entity.conflict");
        if (current.DisabledBy is null)
            return HomeAssistantEnableStage.Unchanged;
        if (current.DisabledBy is not ("integration" or "user"))
            throw new MediaContractException("ha.entity.enable-blocked");
        if (identity.ConfigEntryId is not null || identity.DeviceId is not null)
        {
            var inventory = await GetRoleInventoryAsync(token);
            if (
                (
                    identity.ConfigEntryId is { } entryId
                    && !inventory.Entries.Any(e =>
                        e.Id == entryId
                        && e.Domain == identity.Platform
                        && e.State == "loaded"
                        && e.DisabledBy is null
                    )
                )
                || (
                    identity.DeviceId is { } deviceId
                    && !inventory.Devices.Any(d =>
                        d.Id == deviceId
                        && d.DisabledBy is null
                        && (
                            identity.ConfigEntryId is null
                            || d.ConfigEntries.Contains(identity.ConfigEntryId)
                        )
                    )
                )
            )
                throw new MediaContractException("ha.entity.parent-disabled-or-missing");
            // Revalidate after the additional reads; never write to a renamed/reassigned entity.
            var latest = (await GetEntitiesAsync(token))
                .Where(e => e.RegistryId == identity.RegistryId)
                .ToArray();
            if (latest.Length != 1 || latest[0] != observed[0])
                throw new MediaContractException("ha.entity.conflict");
        }
        var result = Object(
            await CommandAsync(
                "config/entity_registry/update",
                new() { ["entity_id"] = identity.EntityId, ["disabled_by"] = null },
                true,
                token
            )
        );
        var after = await GetEntitiesAsync(token);
        if (
            after.Count(e => e == identity with { DisabledBy = null }) != 1
            || after.Count(e => e.RegistryId == identity.RegistryId) != 1
        )
            throw new MediaContractException("ha.entity.enable-not-applied");
        if (
            result["require_restart"] is JsonValue restart
            && restart.TryGetValue<bool>(out var required)
            && required
        )
            return HomeAssistantEnableStage.RestartRequired;
        if (
            result["reload_delay"] is JsonValue delay
            && double.TryParse(
                delay.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds
            )
            && double.IsFinite(seconds)
            && seconds >= 0
        )
            return HomeAssistantEnableStage.ReloadScheduled;
        throw new MediaContractException("ha.entity.enable-activation-unknown");
    }

    private static HomeAssistantEntity ReadRoleIdentity(JsonObject e) =>
        new(
            Text(e, "id"),
            Text(e, "entity_id"),
            Text(e, "unique_id"),
            Text(e, "platform"),
            NullableText(e, "config_entry_id"),
            NullableText(e, "device_id"),
            NullableText(e, "disabled_by")
        );

    // Test helper only; generic player disabling remains blocked until dependency analysis exists.
    internal Task DisableProbeHelperAsync(HomeAssistantEntity identity, CancellationToken token)
    {
        if (
            identity.Platform != "input_boolean"
            || identity.ConfigEntryId is not null
            || identity.DeviceId is not null
        )
            throw new ArgumentException("ha.fixture.identity-invalid");
        return CommandAsync(
            "config/entity_registry/update",
            new() { ["entity_id"] = identity.EntityId, ["disabled_by"] = "user" },
            true,
            token
        );
    }

    internal async Task<bool> IsProbeHelperAvailableAsync(string entityId, CancellationToken token)
    {
        if (
            !HomeAssistantDashboard.IsEntityId(entityId)
            || !entityId.StartsWith("input_boolean.", StringComparison.Ordinal)
        )
            throw new ArgumentException("ha.fixture.identity-invalid");
        return Array(await CommandAsync("get_states", null, false, token))
                .Select(Object)
                .Count(s => Text(s, "entity_id") == entityId && Text(s, "state") is "on" or "off")
            == 1;
    }
}
