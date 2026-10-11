using HhHomelab.Wizard.Domain;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantDeviceBindingTests
{
    private const string Uuid = "00000000-0000-0000-0000-000000000001";
    private static HomeAssistantDeviceBinding Selection(HomeAssistantDeviceIntegration integration) => new(integration, "entry", "device", integration switch
    { HomeAssistantDeviceIntegration.SynologyDsm => "SERIAL", HomeAssistantDeviceIntegration.AndroidTvRemote => "AA-BB-CC-DD-EE-FF", _ => Uuid });
    private static HomeAssistantRoleInventory Inventory(HomeAssistantDeviceIntegration integration) => new([], [new("device", ["entry"],
        [new(HomeAssistantDeviceBindingVerifier.Domain(integration), HomeAssistantDeviceBindingVerifier.RegistryIdentifier(integration, Selection(integration).Identifier))], null, null)],
        [new("entry", HomeAssistantDeviceBindingVerifier.Domain(integration), "loaded", null)]);
    private static HomeAssistantDeviceBindingStatus Verify(HomeAssistantRoleInventory snapshot, HomeAssistantDeviceBinding selection) => HomeAssistantDeviceBindingVerifier.Verify(snapshot, selection).Status;

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm)]
    [InlineData(HomeAssistantDeviceIntegration.LgWebOs)]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote)]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast)]
    public void ExplicitTechnicalDeviceIdentityMatchesWithoutNamesOrPlayers(HomeAssistantDeviceIntegration integration) =>
        Assert.Equal(HomeAssistantDeviceBindingStatus.Matched, Verify(Inventory(integration), Selection(integration)));

    [Fact]
    public void CastDeviceUsesUuidWithoutHyphensWhileEntityIdMayUseUuidWithHyphens()
    {
        Assert.Equal("00000000000000000000000000000001", HomeAssistantDeviceBindingVerifier.RegistryIdentifier(HomeAssistantDeviceIntegration.GoogleCast, Uuid));
        var snapshot = Inventory(HomeAssistantDeviceIntegration.GoogleCast);
        Assert.Equal(HomeAssistantDeviceBindingStatus.IdentityMismatch, Verify(snapshot with { Devices = [snapshot.Devices[0] with { Identifiers = [new("cast", Uuid)] }] }, Selection(HomeAssistantDeviceIntegration.GoogleCast)));
    }

    [Theory]
    [InlineData("entry-missing", HomeAssistantDeviceBindingStatus.EntryMissing)]
    [InlineData("entry-disabled", HomeAssistantDeviceBindingStatus.EntryDisabled)]
    [InlineData("setup_retry", HomeAssistantDeviceBindingStatus.EntryPending)]
    [InlineData("setup_in_progress", HomeAssistantDeviceBindingStatus.EntryPending)]
    [InlineData("setup_error", HomeAssistantDeviceBindingStatus.EntryFailed)]
    [InlineData("device-missing", HomeAssistantDeviceBindingStatus.DeviceMissing)]
    [InlineData("device-disabled", HomeAssistantDeviceBindingStatus.DeviceDisabled)]
    [InlineData("domain", HomeAssistantDeviceBindingStatus.IdentityMismatch)]
    [InlineData("identifier", HomeAssistantDeviceBindingStatus.IdentityMismatch)]
    [InlineData("entry-link", HomeAssistantDeviceBindingStatus.IdentityMismatch)]
    [InlineData("child", HomeAssistantDeviceBindingStatus.IdentityMismatch)]
    [InlineData("duplicate-entry", HomeAssistantDeviceBindingStatus.Ambiguous)]
    [InlineData("duplicate-device", HomeAssistantDeviceBindingStatus.Ambiguous)]
    [InlineData("duplicate-identifier", HomeAssistantDeviceBindingStatus.Ambiguous)]
    [InlineData("extra-domain-identifier", HomeAssistantDeviceBindingStatus.Ambiguous)]
    public void MissingPendingDisabledForeignAndAmbiguousDoNotMatch(string change, HomeAssistantDeviceBindingStatus expected)
    {
        var snapshot = Inventory(HomeAssistantDeviceIntegration.SynologyDsm); var entry = snapshot.Entries[0]; var device = snapshot.Devices[0];
        snapshot = change switch
        {
            "entry-missing" => snapshot with { Entries = [] }, "entry-disabled" => snapshot with { Entries = [entry with { DisabledBy = "user" }] },
            "setup_retry" or "setup_in_progress" or "setup_error" => snapshot with { Entries = [entry with { State = change }] },
            "domain" => snapshot with { Entries = [entry with { Domain = "foreign" }] },
            "device-missing" => snapshot with { Devices = [] }, "device-disabled" => snapshot with { Devices = [device with { DisabledBy = "user" }] },
            "identifier" => snapshot with { Devices = [device with { Identifiers = [new("synology_dsm", "OTHER")] }] },
            "entry-link" => snapshot with { Devices = [device with { ConfigEntries = ["foreign"] }] },
            "child" => snapshot with { Devices = [device with { ViaDeviceId = "parent" }] },
            "duplicate-entry" => snapshot with { Entries = [entry, entry] }, "duplicate-device" => snapshot with { Devices = [device, device] },
            "duplicate-identifier" => snapshot with { Devices = [device, device with { Id = "other" }] },
            "extra-domain-identifier" => snapshot with { Devices = [device with { Identifiers = [new("synology_dsm", "SERIAL"), new("synology_dsm", "OTHER")] }] },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, Verify(snapshot, Selection(HomeAssistantDeviceIntegration.SynologyDsm)));
    }

    [Fact]
    public void SeparateAndroidAndCastIdentitiesDoNotProveTheyAreTheSamePhysicalGx1()
    {
        var android = Inventory(HomeAssistantDeviceIntegration.AndroidTvRemote);
        var device = android.Devices[0] with { Identifiers = [.. android.Devices[0].Identifiers, new("cast", Uuid.Replace("-", ""))], ConfigEntries = ["entry", "cast-entry"] };
        var snapshot = android with { Devices = [device], Entries = [.. android.Entries, new("cast-entry", "cast", "loaded", null)] };
        Assert.Equal(HomeAssistantDeviceBindingStatus.Matched, Verify(snapshot, Selection(HomeAssistantDeviceIntegration.AndroidTvRemote)));
        Assert.Equal(HomeAssistantDeviceBindingStatus.Matched, Verify(snapshot, Selection(HomeAssistantDeviceIntegration.GoogleCast) with { EntryId = "cast-entry" }));
        // These two independent registry matches still need a user-confirmed relation to GX1.
    }

    [Theory]
    [InlineData(HomeAssistantDeviceIntegration.GoogleCast, "cast")]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote, "not-a-mac")]
    [InlineData(HomeAssistantDeviceIntegration.AndroidTvRemote, "AA:BB-CC:DD:EE:FF")]
    [InlineData(HomeAssistantDeviceIntegration.SynologyDsm, "SERIAL\n")]
    public void InvalidPhysicalIdentifierIsRejected(HomeAssistantDeviceIntegration integration, string identifier) =>
        Assert.Throws<ArgumentException>(() => HomeAssistantDeviceBindingVerifier.RegistryIdentifier(integration, identifier));
}
