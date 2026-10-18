using System.Text;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class NasPreflightTests
{
    [Fact]
    public void CompleteInventoryIsObservedButDoesNotProveVersionCompatibilityOrInstallationReadiness()
    {
        var facts = NasPreflightProtocol.Parse(0, PreflightData.Output);
        var snapshot = PreflightData.Snapshot(facts);
        var checks = NasPreflightAssessment.Evaluate(snapshot);
        Assert.Equal("7.4.1-90080", facts.Dsm.Version);
        Assert.Equal("24.0.2-1535", facts.ContainerManager.Version);
        Assert.Empty(facts.ContainerIds);
        Assert.Contains(checks, check => check.Code == "preflight.containers.empty" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "preflight.dsm" && check.Status == PreflightCheckStatus.Unverified);
        Assert.Contains(checks, check => check.Code == "preflight.paths-acl" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(checks, check => check.Code == "preflight.ownership" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.False(checks.All(check => check.Status == PreflightCheckStatus.Passed));
    }

    [Fact]
    public void MissingSoftwareAndUnreadableSoftwareRemainDifferentFacts()
    {
        var output = PreflightData.Output.Replace("dsm=available:7.4.1-90080", "dsm=missing", StringComparison.Ordinal)
            .Replace("docker=available:24.0.2", "docker=unavailable", StringComparison.Ordinal)
            .Replace("containers=available", "containers=unavailable", StringComparison.Ordinal);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Equal(PreflightFactStatus.Missing, facts.Dsm.Status);
        Assert.Equal(PreflightFactStatus.Unavailable, facts.DockerServer.Status);
        Assert.Null(facts.DockerServer.Version);
        Assert.Contains(NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts)),
            check => check.Code == "preflight.containers.unavailable" && check.Status == PreflightCheckStatus.Blocking);
    }

    [Theory]
    [InlineData("hh-homelab-wizard:nas-preflight:v1", "unexpected-marker")]
    [InlineData("kernel=Linux", "kernel=Linux\nextra=ignored")]
    [InlineData("architecture=x86_64", "architecture=x86_64\u0000")]
    [InlineData("unix-time=1792368000", "unix-time=-1")]
    [InlineData("unix-time=1792368000", "unix-time=999999999999999999")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=available:")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=available:7.4.1;payload")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=unknown")]
    [InlineData("container-manager=available:24.0.2-1535", "dsm=missing")]
    [InlineData("containers=available", "containers=missing")]
    [InlineData("containers=available", "containers=available\ncontainer=short-id")]
    [InlineData("docker=available:24.0.2", "docker=missing")]
    [InlineData("end", "end\ntrailer")]
    public void MalformedOrInconsistentOutputIsRejectedWithoutReturningPartialFacts(string original, string replacement)
    {
        var output = PreflightData.Output.Replace(original, replacement, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, output));
        Assert.Equal("preflight.response.invalid", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(126)]
    public void CompleteOutputWithUnsuccessfulExitIsStillRejected(int? exitCode)
    {
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(exitCode, PreflightData.Output));
    }

    [Fact]
    public void InventoryIsBoundedAndDuplicatesOrPartialInventoriesAreNeverAnEmptySuccess()
    {
        var ids = Enumerable.Range(0, 128).Select(index => index.ToString("x64")).ToArray();
        var output = PreflightData.WithContainers(ids);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Equal(128, facts.ContainerIds.Count);
        Assert.Contains(NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts)),
            check => check.Code == "preflight.containers.ownership-required" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, PreflightData.WithContainers(ids.Append(new string('f', 64)))));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, PreflightData.WithContainers([new string('a', 64), new string('A', 64)])));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, output.Replace("containers=available", "containers=unavailable", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, new string('x', 17000)));
    }

    [Fact]
    public void FactsCopyAndCanonicalizeContainerIdsAndDoNotExposeAMutableArray()
    {
        var ids = new[] { new string('B', 64), new string('a', 64) };
        var facts = PreflightData.Facts(ids);
        ids[0] = new string('c', 64);
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) }, facts.ContainerIds);
        var collection = Assert.IsAssignableFrom<IList<string>>(facts.ContainerIds);
        Assert.Throws<NotSupportedException>(() => collection[0] = new string('d', 64));
    }

    [Fact]
    public void EnvironmentFingerprintIgnoresClockTickButDetectsIdentityVersionOrResourceDrift()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts([new string('a', 64)]));
        var nextTime = PreflightData.Now.AddSeconds(10);
        var next = new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, nextTime, nextTime,
            PreflightData.Facts([new string('a', 64)], nextTime));
        Assert.Equal(snapshot.EnvironmentFingerprint, next.EnvironmentFingerprint);
        Assert.NotEqual(snapshot.EnvironmentFingerprint, PreflightData.Snapshot(PreflightData.Facts([new string('b', 64)])).EnvironmentFingerprint);
        var changed = NasPreflightProtocol.Parse(0, PreflightData.Output.Replace("docker=available:24.0.2", "docker=available:25.0.0", StringComparison.Ordinal));
        Assert.NotEqual(PreflightData.Snapshot(PreflightData.Facts()).EnvironmentFingerprint, PreflightData.Snapshot(changed).EnvironmentFingerprint);
        var rotated = PreflightData.Snapshot(PreflightData.Facts(), PreflightData.OtherPin);
        Assert.NotEqual(PreflightData.Snapshot(PreflightData.Facts()).EnvironmentFingerprint, rotated.EnvironmentFingerprint);
    }

    [Fact]
    public void FreshnessRequiresSameHostKeyProfileAndAnEntireObservationInsideTheAgeWindow()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts());
        var age = TimeSpan.FromMinutes(2);
        Assert.True(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(1), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.OtherPin, PreflightData.Profile, PreflightData.Now, age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, new string('b', 64), PreflightData.Now, age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(-1), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddMinutes(3), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, TimeSpan.FromDays(1)));
        var otherUser = PreflightData.Target with { Username = "other-synthetic-user" };
        Assert.False(snapshot.IsCurrentFor(otherUser, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, age));
        var slow = new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddMinutes(-3), PreflightData.Now, snapshot.Facts);
        Assert.False(slow.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, age));
    }

    [Theory]
    [InlineData("Darwin", "x86_64", 0, "preflight.kernel")]
    [InlineData("Linux", "aarch64", 0, "preflight.architecture")]
    [InlineData("Linux", "x86_64", 121, "preflight.clock-window")]
    [InlineData("Linux", "x86_64", -121, "preflight.clock-window")]
    public void WrongPlatformOrClockSkewBlocksPrerequisites(string kernel, string architecture, int offset, string code)
    {
        var facts = PreflightData.Facts(kernel: kernel, architecture: architecture, serverTime: PreflightData.Now.AddSeconds(offset));
        var checks = NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts));
        Assert.Contains(checks, check => check.Code == code && check.Status == PreflightCheckStatus.Blocking);
    }

    [Fact]
    public void DomainRejectsInvalidBindingsAndReversedObservationWindow()
    {
        Assert.Throws<ArgumentException>(() => new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, "invalid", PreflightData.Now, PreflightData.Now, PreflightData.Facts()));
        Assert.Throws<ArgumentException>(() => new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(1), PreflightData.Now, PreflightData.Facts()));
        Assert.Throws<ArgumentException>(() => NasPreflightResult.Failed(SshProbeResult.Ready(PreflightData.Pin, new(1000, false))));
        Assert.False(new PreflightVersion((PreflightFactStatus)100).IsValid);
        Assert.False(new PreflightVersion(PreflightFactStatus.Missing, "24.0.2").IsValid);
    }
}

internal static class PreflightData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792368000);

    internal static readonly string Profile = new('a', 64);

    internal static readonly SshConnectionTarget Target = new("nas.example.invalid", 22, "synthetic-user");

    internal static readonly byte[] PublicKey = Encoding.UTF8.GetBytes("synthetic-preflight-public-key");

    internal static SshHostKey Pin => SshHostKey.FromPublicKey(Target.EndpointFingerprint, "ssh-ed25519", PublicKey);
    internal static SshHostKey OtherPin => SshHostKey.FromPublicKey(Target.EndpointFingerprint, "ssh-ed25519", [1, 2, 3]);

    internal const string Output = "hh-homelab-wizard:nas-preflight:v1\nkernel=Linux\narchitecture=x86_64\nunix-time=1792368000\ndsm=available:7.4.1-90080\ncontainer-manager=available:24.0.2-1535\ndocker=available:24.0.2\ncompose=available:2.20.3\ncontainers=available\nend\n";

    internal static string WithContainers(IEnumerable<string> ids) => Output.Replace("end\n",
        string.Concat(ids.Select(id => "container=" + id + "\n")) + "end\n", StringComparison.Ordinal);

    internal static NasPreflightFacts Facts(IEnumerable<string>? ids = null, DateTimeOffset? serverTime = null,
        string kernel = "Linux", string architecture = "x86_64") => new(kernel, architecture, serverTime ?? Now,
            new(PreflightFactStatus.Available, "7.4.1-90080"), new(PreflightFactStatus.Available, "24.0.2-1535"),
            new(PreflightFactStatus.Available, "24.0.2"), new(PreflightFactStatus.Available, "2.20.3"), PreflightFactStatus.Available, ids ?? []);

    internal static NasPreflightSnapshot Snapshot(NasPreflightFacts facts, SshHostKey? pin = null) => new(Target, pin ?? Pin, Profile, Now, Now, facts);
}
