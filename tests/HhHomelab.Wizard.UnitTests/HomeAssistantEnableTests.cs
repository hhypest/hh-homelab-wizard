using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed partial class HomeAssistantContractTests
{
    [Theory]
    [InlineData("integration")]
    [InlineData("user")]
    public async Task EnableSelectedEntityIsConfirmedAndRepeatDoesNotWrite(string reason)
    {
        using var f = new Fixture(); f.Entities[0]!["disabled_by"] = reason;
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = Identity with { DisabledBy = reason };
        Assert.Equal(HomeAssistantEnableStage.RestartRequired, await ha.EnsureEntityEnabledAsync(selected, Token));
        Assert.Equal(HomeAssistantEnableStage.Unchanged, await ha.EnsureEntityEnabledAsync(selected, Token));
        Assert.Equal(1, f.RegistryWrites);
    }

    [Fact]
    public async Task IntegrationReloadIsReportedWithoutClaimingSensorAvailability()
    {
        using var f = new Fixture { EnableResponse = new() { ["reload_delay"] = 30 } }; f.Entities[0]!["disabled_by"] = "user";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal(HomeAssistantEnableStage.ReloadScheduled, await ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token));
    }

    [Theory]
    [InlineData("config_entry")]
    [InlineData("device")]
    [InlineData("hass")]
    [InlineData("unexpected")]
    public async Task UnsupportedDisablerIsNeverCleared(string reason)
    {
        using var f = new Fixture(); f.Entities[0]!["disabled_by"] = reason;
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.entity.enable-blocked", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = reason }, Token))).Code);
        Assert.Equal(0, f.RegistryWrites);
    }

    [Theory]
    [InlineData("unique_id")]
    [InlineData("device_id")]
    [InlineData("config_entry_id")]
    [InlineData("entity_id")]
    [InlineData("platform")]
    [InlineData("disabled_by")]
    public async Task ChangedIdentityOrDisableReasonBlocksEnable(string key)
    {
        using var f = new Fixture(); f.Entities[0]!["disabled_by"] = "integration"; f.Entities[0]![key] = key == "disabled_by" ? "user" : "foreign";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.entity.conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "integration" }, Token))).Code);
        Assert.Equal(0, f.RegistryWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostEnableOrCancellationObservesResultWithoutRepeatingWrite(bool cancel)
    {
        using var f = new Fixture(); f.Entities[0]!["disabled_by"] = "user";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        f.LoseCommand = "config/entity_registry/update"; f.CancelLoss = cancel; f.CallerToCancel = caller;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, caller.Token));
        else Assert.Equal("ha.ws.result-unknown", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token))).Code);
        await ha.ReconnectAsync(Token);
        Assert.Equal(HomeAssistantEnableStage.Unchanged, await ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token));
        Assert.Equal(1, f.RegistryWrites);
    }

    [Fact]
    public async Task IgnoredEnableDoesNotReportSuccess()
    {
        using var f = new Fixture { IgnoreEnable = true }; f.Entities[0]!["disabled_by"] = "user";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.entity.enable-not-applied", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token))).Code);
    }

    [Fact]
    public async Task UnknownActivationResponseDoesNotInventReloadOrRestart()
    {
        using var f = new Fixture { EnableResponse = new() }; f.Entities[0]!["disabled_by"] = "user";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.entity.enable-activation-unknown", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token))).Code);
        Assert.Equal(HomeAssistantEnableStage.Unchanged, await ha.EnsureEntityEnabledAsync(Identity with { DisabledBy = "user" }, Token));
        Assert.Equal(1, f.RegistryWrites);
    }

    [Theory]
    [InlineData("entry-disabled")]
    [InlineData("entry-not-loaded")]
    [InlineData("device-disabled")]
    [InlineData("wrong-device-entry")]
    public async Task DisabledOrForeignParentBlocksEnable(string failure)
    {
        using var f = new Fixture { RoleDevices = new(new JsonObject { ["id"] = "device1", ["config_entries"] = new JsonArray(failure == "wrong-device-entry" ? "foreign" : "entry1"),
            ["identifiers"] = new JsonArray(new JsonArray("input_boolean", "synthetic-device")), ["disabled_by"] = failure == "device-disabled" ? "user" : null }) };
        var selected = Identity with { ConfigEntryId = "entry1", DeviceId = "device1", DisabledBy = "integration" };
        f.Entities[0]!["config_entry_id"] = "entry1"; f.Entities[0]!["device_id"] = "device1"; f.Entities[0]!["disabled_by"] = "integration";
        f.IntegrationEntries.Add(new JsonObject { ["entry_id"] = "entry1", ["domain"] = "input_boolean", ["state"] = failure == "entry-not-loaded" ? "setup_retry" : "loaded",
            ["disabled_by"] = failure == "entry-disabled" ? "user" : null, ["data"] = new JsonObject { ["token"] = "synthetic-sensitive" } });
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.entity.parent-disabled-or-missing", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(selected, Token))).Code);
        Assert.Equal(0, f.RegistryWrites);
        var inventory = await ha.GetRoleInventoryAsync(Token); Assert.DoesNotContain("synthetic-sensitive", System.Text.Json.JsonSerializer.Serialize(inventory));
    }

    [Fact]
    public async Task RoleInventoryExcludesNamesConfigDataAndOptions()
    {
        using var f = new Fixture { RoleDevices = new(new JsonObject { ["id"] = "device1", ["config_entries"] = new JsonArray("entry1"),
            ["identifiers"] = new JsonArray(new JsonArray("input_boolean", "synthetic-device")), ["name"] = "synthetic-sensitive" }) };
        f.Entities[0]!["name"] = "synthetic-sensitive"; f.Entities[0]!["options"] = new JsonObject { ["password"] = "synthetic-sensitive" };
        f.Entities[0]!["translation_key"] = "technical-key";
        f.IntegrationEntries.Add(new JsonObject { ["entry_id"] = "entry1", ["domain"] = "input_boolean", ["state"] = "loaded", ["data"] = new JsonObject { ["token"] = "synthetic-sensitive" } });
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var inventory = await ha.GetRoleInventoryAsync(Token);
        Assert.Equal("technical-key", inventory.Entities[0].TranslationKey);
        Assert.DoesNotContain("synthetic-sensitive", System.Text.Json.JsonSerializer.Serialize(inventory));
        Assert.Equal(0, f.RegistryWrites);
    }

    [Theory]
    [InlineData("entities")]
    [InlineData("devices")]
    [InlineData("entries")]
    public async Task RoleInventoryRejectsAmbiguousIdentifiers(string duplicate)
    {
        using var f = new Fixture { RoleDevices = new(new JsonObject { ["id"] = "device1", ["config_entries"] = new JsonArray(), ["identifiers"] = new JsonArray() }) };
        f.IntegrationEntries.Add(new JsonObject { ["entry_id"] = "entry1", ["domain"] = "input_boolean", ["state"] = "loaded" });
        if (duplicate == "entities") f.Entities.Add(f.Entities[0]!.DeepClone());
        if (duplicate == "devices") f.RoleDevices.Add(f.RoleDevices[0]!.DeepClone());
        if (duplicate == "entries") f.IntegrationEntries.Add(f.IntegrationEntries[0]!.DeepClone());
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal("ha.roles.snapshot-invalid", (await Assert.ThrowsAsync<MediaContractException>(() => ha.GetRoleInventoryAsync(Token))).Code);
        Assert.Equal(0, f.RegistryWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadedParentsAllowEnableButIdentityChangeDuringReadsBlocksIt(bool changed)
    {
        using var f = new Fixture { ChangeEntityAfterParentRead = changed, EnableResponse = new() { ["reload_delay"] = 30 },
            RoleDevices = new(new JsonObject { ["id"] = "device1", ["config_entries"] = new JsonArray("entry1"), ["identifiers"] = new JsonArray() }) };
        f.IntegrationEntries.Add(new JsonObject { ["entry_id"] = "entry1", ["domain"] = "input_boolean", ["state"] = "loaded" });
        f.Entities[0]!["config_entry_id"] = "entry1"; f.Entities[0]!["device_id"] = "device1"; f.Entities[0]!["disabled_by"] = "integration";
        using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var selected = Identity with { ConfigEntryId = "entry1", DeviceId = "device1", DisabledBy = "integration" };
        if (changed)
        {
            Assert.Equal("ha.entity.conflict", (await Assert.ThrowsAsync<MediaContractException>(() => ha.EnsureEntityEnabledAsync(selected, Token))).Code);
            Assert.Equal(0, f.RegistryWrites);
        }
        else
        {
            Assert.Equal(HomeAssistantEnableStage.ReloadScheduled, await ha.EnsureEntityEnabledAsync(selected, Token));
            Assert.Equal(1, f.RegistryWrites);
        }
    }
}
