using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantProfileTests
{
    private const string Root = "monitor_docker-" + MonitorDockerPackage.Commit + "/";

    [Fact]
    public void LockContainsComponentLicenseAndRawHashes()
    {
        var files = MonitorDockerPackage.ReadLock();
        Assert.Equal(39, files.Count);
        Assert.Contains("LICENSE.md", files.Keys);
        Assert.Contains("custom_components/monitor_docker/manifest.json", files.Keys);
        Assert.All(files.Values, hash => Assert.Matches("^[a-f0-9]{64}$", hash));
    }

    [Theory]
    [InlineData("other-root/README.md")]
    [InlineData("../README.md")]
    [InlineData("ROOT../secrets.yaml")]
    [InlineData("ROOTcustom_components/../evil.py")]
    [InlineData("ROOTcustom_components\\evil.py")]
    [InlineData("ROOTC:/evil.py")]
    public void RejectsArchivePaths(string path) =>
        Assert.Equal(
            "monitor-docker.archive.path.invalid",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(path.Replace("ROOT", Root)),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsDuplicateCaseInsensitivePaths() =>
        Assert.Equal(
            "monitor-docker.archive.path.invalid",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(Root + "README.md", Root + "readme.md"),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsSymlinksBeforeHashVerification() =>
        Assert.Equal(
            "monitor-docker.archive.file-type.invalid",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        ZipWithType(0xa000),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsUnpinnedComponentFile() =>
        Assert.Equal(
            "monitor-docker.archive.unexpected-file",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(Root + "custom_components/monitor_docker/evil.py"),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsChangedFile() =>
        Assert.Equal(
            "monitor-docker.archive.hash.invalid",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(Root + "LICENSE.md"),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsIncompleteArchive() =>
        Assert.Equal(
            "monitor-docker.archive.incomplete",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(Root + "README.md"),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsOversizedArchive() =>
        Assert.Equal(
            "monitor-docker.archive.too-large",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        new byte[4 * 1024 * 1024 + 1],
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsTooManyEntries() =>
        Assert.Equal(
            "monitor-docker.archive.too-many-entries",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        Zip(Enumerable.Range(0, 201).Select(i => Root + i + ".txt").ToArray()),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );

    [Fact]
    public void RejectsLargeExpandedFile()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var content = zip.CreateEntry(Root + "LICENSE.md").Open();
            content.Write(new byte[512 * 1024 + 1]);
        }
        Assert.Equal(
            "monitor-docker.archive.file.too-large",
            Assert
                .Throws<InvalidDataException>(() =>
                    MonitorDockerPackage.VerifyArchive(
                        buffer.ToArray(),
                        TestContext.Current.CancellationToken
                    )
                )
                .Message
        );
    }

    [Fact]
    public void CancellationTakesPrecedenceOverParsing() =>
        Assert.Throws<OperationCanceledException>(() =>
            MonitorDockerPackage.VerifyArchive([], new CancellationToken(true))
        );

    [Theory]
    [InlineData("nas.local", "192.0.2.255", "02:00:00:00:00:01", 0, 0)]
    [InlineData("192.0.2.1;rm", "192.0.2.255", "02:00:00:00:00:01", 0, 0)]
    [InlineData("192.0.2.1", "192.0.2.255\nfoo", "02:00:00:00:00:01", 0, 0)]
    [InlineData("192.0.2.1", "192.0.2.255", "bad", 0, 0)]
    [InlineData("192.0.2.1", "192.0.2.255", "02:00:00:00:00:01", 91, 0)]
    [InlineData("192.0.2.1", "192.0.2.255", "02:00:00:00:00:01", 0, -181)]
    [InlineData("192.0.2.1", "192.0.2.255", "02:00:00:00:00:01", double.NaN, 0)]
    public void RejectsUnsafeOrInvalidSettings(
        string nas,
        string broadcast,
        string mac,
        double latitude,
        double longitude
    ) =>
        Assert.Equal(
            "ha.profile.settings.invalid",
            Assert
                .Throws<ArgumentException>(() =>
                    HomeAssistantLocalProfile.Validate(
                        Settings(nas, broadcast, mac, latitude, longitude)
                    )
                )
                .Message
        );

    [Fact]
    public void SettingsDoNotPrintCredentials()
    {
        var settings = Settings();
        HomeAssistantLocalProfile.Validate(settings);
        Assert.DoesNotContain(settings.JellyfinToken, settings.ToString()!);
    }

    [Theory]
    [InlineData("invalid", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb\nheader")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb\"header")]
    public void RejectsInvalidCredentialsBeforeRendering(string user, string credential) =>
        Assert.Throws<ArgumentException>(() =>
            HomeAssistantLocalProfile.Validate(
                new HomeAssistantLocalSettings
                {
                    NasAddress = "192.0.2.1",
                    TvBroadcastAddress = "192.0.2.255",
                    TvMac = "02:00:00:00:00:01",
                    JellyfinUserId = user,
                    JellyfinToken = credential,
                }
            )
        );

    [Fact]
    public async Task MissingSourceFailsBeforeProducingPayload()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "wizard-source-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(
                "ha.profile.source.invalid",
                (
                    await Assert.ThrowsAsync<InvalidDataException>(() =>
                        HomeAssistantLocalProfile.RenderAsync(
                            directory,
                            Settings(),
                            null!,
                            TestContext.Current.CancellationToken
                        )
                    )
                ).Message
            );
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task TarContainsOnlyRegularFilesWithRestrictedSecretPermissions()
    {
        var payload = new HomeAssistantPayload(
            new Dictionary<string, byte[]>
            {
                ["secrets.yaml"] = Encoding.UTF8.GetBytes("synthetic: value\n"),
                ["packages/test.yaml"] = Encoding.UTF8.GetBytes("script: {}\n"),
            }
        );
        Assert.Equal(new[] { "packages/test.yaml", "secrets.yaml" }, payload.Paths);
        Assert.DoesNotContain("synthetic", payload.ToString()!);
        using var stream = new MemoryStream();
        await payload.WriteTarAsync(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new TarReader(stream);
        var regular = reader.GetNextEntry()!;
        Assert.Equal(TarEntryType.RegularFile, regular.EntryType);
        Assert.Equal(
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead,
            regular.Mode
        );
        var secret = reader.GetNextEntry()!;
        Assert.Equal("secrets.yaml", secret.Name);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, secret.Mode);
        Assert.Equal(DateTimeOffset.UnixEpoch, secret.ModificationTime);
        using var text = new StreamReader(secret.DataStream!);
        Assert.Equal(
            "synthetic: value\n",
            await text.ReadToEndAsync(TestContext.Current.CancellationToken)
        );
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task TarSerializationHonorsCancellation() =>
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new HomeAssistantPayload(new() { ["secrets.yaml"] = [] }).WriteTarAsync(
                new MemoryStream(),
                new CancellationToken(true)
            )
        );

    private static HomeAssistantLocalSettings Settings(
        string nas = "192.0.2.1",
        string broadcast = "192.0.2.255",
        string mac = "02:00:00:00:00:01",
        double latitude = 0,
        double longitude = 0
    ) =>
        new()
        {
            NasAddress = nas,
            TvBroadcastAddress = broadcast,
            TvMac = mac,
            Latitude = latitude,
            Longitude = longitude,
            JellyfinUserId = new string('a', 32),
            JellyfinToken = new string('b', 32),
        };

    private static byte[] Zip(params string[] paths)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var path in paths)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write("changed");
            }
        return buffer.ToArray();
    }

    private static byte[] ZipWithType(int type)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry(Root + "README.md").ExternalAttributes = type << 16;
        return buffer.ToArray();
    }
}
