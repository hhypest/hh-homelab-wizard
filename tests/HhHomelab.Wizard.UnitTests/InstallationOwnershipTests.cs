using System.Text;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class InstallationOwnershipTests
{
    private static readonly Guid Installation = Guid.Parse("4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b");

    private static readonly InstallationOwner Owner = new(Installation, PreflightData.Profile);

    private const string OwnedConfiguration = "entry=directory:0:same:42:1026:100:755:not-empty";

    [Fact]
    public void AbsentOrEmptyConfigurationRootWithoutMarkerIsANewInstallation()
    {
        Assert.Equal(new PreflightCheck("ownership.new-installation", PreflightCheckStatus.Passed), Evaluate(PathData.Output(), Owner));
        var empty = PathData.Output(configuration: "entry=directory:0:same:42:1026:100:755:empty");
        Assert.Equal(PreflightCheckStatus.Passed, Evaluate(empty, null).Status);
    }

    [Fact]
    public void ExactInstallationAndProfileMatchMakesExistingContentsOwn()
    {
        var output = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Installation, PreflightData.Profile)));
        var snapshot = PathData.Snapshot(output);
        Assert.Equal(new OwnershipMarker(1, Installation, PreflightData.Profile), snapshot.Ownership.Marker);
        var checks = RemotePathAssessment.Evaluate(snapshot, Owner);
        Assert.Contains(checks, check => check.Code == "ownership.own-installation" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void MarkerWithoutAnExpectedOwnerIsNeverAdopted()
    {
        var output = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Installation, PreflightData.Profile)));
        Assert.Equal(new PreflightCheck("ownership.foreign-installation", PreflightCheckStatus.Blocking), Evaluate(output, null));
    }

    [Fact]
    public void OtherInstallationOrChangedProfileBlocks()
    {
        var foreign = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Guid.NewGuid(), PreflightData.Profile)));
        Assert.Equal("ownership.foreign-installation", Evaluate(foreign, Owner).Code);
        var changed = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Installation, new string('c', 64))));
        Assert.Equal(new PreflightCheck("ownership.profile-changed", PreflightCheckStatus.Blocking), Evaluate(changed, Owner));
    }

    [Fact]
    public void OwnerFromAJournalOfAnotherProfileNeverMatchesEvenWithItsOwnMarker()
    {
        var oldProfile = new string('c', 64);
        var oldOwner = new InstallationOwner(Installation, oldProfile);
        var output = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Installation, oldProfile)));
        Assert.Equal(new PreflightCheck("ownership.profile-changed", PreflightCheckStatus.Blocking), Evaluate(output, oldOwner));
        Assert.Equal(new PreflightCheck("ownership.profile-changed", PreflightCheckStatus.Blocking), Evaluate(PathData.Output(), oldOwner));
        foreach (var marker in new[] { "marker=unavailable", "marker=redirected", "marker=invalid" })
        {
            var unusable = PathData.Output(configuration: OwnedConfiguration, marker: marker);
            Assert.Equal(new PreflightCheck("ownership.profile-changed", PreflightCheckStatus.Blocking), Evaluate(unusable, oldOwner));
        }
    }

    [Fact]
    public void NonEmptyRootWithoutMarkerIsUnmarkedContentsEvenForTheExpectedOwner()
    {
        var output = PathData.Output(configuration: OwnedConfiguration);
        Assert.Equal(new PreflightCheck("ownership.unmarked-contents", PreflightCheckStatus.Blocking), Evaluate(output, Owner));
    }

    [Theory]
    [InlineData("marker=invalid", "ownership.marker-invalid")]
    [InlineData("marker=redirected", "ownership.marker-redirected")]
    [InlineData("marker=unavailable", "ownership.marker-unavailable")]
    public void UnreadableOrSuspiciousMarkerBlocks(string marker, string code)
    {
        var output = PathData.Output(configuration: OwnedConfiguration, marker: marker);
        Assert.Equal(new PreflightCheck(code, PreflightCheckStatus.Blocking), Evaluate(output, Owner));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":2,\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"PROFILE\"}")]
    [InlineData("{\"schema\":\"1\",\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"PROFILE\"}")]
    [InlineData("{\"schema\":1,\"installationId\":\"not-a-guid\",\"profileFingerprint\":\"PROFILE\"}")]
    [InlineData("{\"schema\":1,\"installationId\":\"00000000-0000-0000-0000-000000000000\",\"profileFingerprint\":\"PROFILE\"}")]
    [InlineData("{\"schema\":1,\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"short\"}")]
    [InlineData("{\"schema\":1,\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"PROFILE\",\"extra\":true}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"PROFILE\"}")]
    [InlineData("{\"schema\":1,\"installationId\":\"4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b\",\"profileFingerprint\":\"PROFILE\"} trailing")]
    public void MalformedMarkerContentIsAnInvalidMarkerNotOwnership(string json)
    {
        var content = json.Replace("PROFILE", PreflightData.Profile, StringComparison.Ordinal);
        var output = PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(content));
        var snapshot = PathData.Snapshot(output);
        Assert.Equal(OwnershipMarkerState.Invalid, snapshot.Ownership.State);
        Assert.Equal("ownership.marker-invalid", InstallationOwnershipAssessment.Evaluate(snapshot, Owner).Code);
    }

    [Fact]
    public void InvalidUtf8MarkerIsInvalidAndOversizedHexRejectsTheResponse()
    {
        var invalidUtf8 = PathData.Output(configuration: OwnedConfiguration, marker: "marker=present\nmarker-content=ff");
        Assert.Equal(OwnershipMarkerState.Invalid, PathData.Snapshot(invalidUtf8).Ownership.State);
        var oversized = "marker=present\nmarker-content=" + new string('2', OwnershipMarker.MaximumBytes * 2 + 2);
        Assert.Throws<InvalidDataException>(() => NasPathProtocol.Parse(PathData.Layout, 0, PathData.Output(marker: oversized)));
    }

    [Fact]
    public void MarkerParticipatesInTheEnvironmentFingerprint()
    {
        var absent = PathData.Snapshot(PathData.Output(configuration: OwnedConfiguration));
        var present = PathData.Snapshot(PathData.Output(configuration: OwnedConfiguration, marker: MarkerLines(Json(Installation, PreflightData.Profile))));
        Assert.NotEqual(absent.EnvironmentFingerprint, present.EnvironmentFingerprint);
    }

    [Fact]
    public void InvalidExpectedOwnerIsRejected()
    {
        var snapshot = PathData.Snapshot(PathData.Output());
        Assert.Throws<ArgumentException>(() => InstallationOwnershipAssessment.Evaluate(snapshot, new(Guid.Empty, PreflightData.Profile)));
        Assert.False(new OwnershipObservation(OwnershipMarkerState.Absent, new(1, Installation, PreflightData.Profile)).IsValid);
    }

    private static PreflightCheck Evaluate(string output, InstallationOwner? owner) =>
        InstallationOwnershipAssessment.Evaluate(PathData.Snapshot(output), owner);

    private static string Json(Guid installation, string profile) =>
        "{\"schema\":1,\"installationId\":\"" + installation.ToString("D") + "\",\"profileFingerprint\":\"" + profile + "\"}";

    private static string MarkerLines(string json) =>
        "marker=present\nmarker-content=" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(json));
}
