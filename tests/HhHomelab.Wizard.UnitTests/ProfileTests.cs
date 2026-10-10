using System.Text.Json;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ProfileTests
{
    private static string Json => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "research-profile.json"));

    [Fact]
    public void InventoryCoversEveryUpstreamStepAndNineServices()
    {
        var profile = ProfileReader.Parse(Json);
        Assert.Equal(55, profile.Coverage.Count(s => s.Id.StartsWith("media.", StringComparison.Ordinal)));
        Assert.Equal(63, profile.Coverage.Count(s => s.Id.StartsWith("ha.", StringComparison.Ordinal)));
        Assert.Equal(9, profile.Services.Length);
        Assert.All(profile.Coverage, s => Assert.Equal("NotImplemented", s.Status));
        Assert.Equal("ghcr.io/vuetorrent/vuetorrent-lsio-mod:2.36.1", Assert.Single(profile.Services.Single(s => s.Id == "qbittorrent").DockerMods));
        Assert.Equal("1.20b3", Assert.Single(profile.Components).ManifestVersion);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("source", "null")]
    [InlineData("artifacts", "null")]
    [InlineData("services", "[]")]
    [InlineData("coverage", "[]")]
    public void RejectsIncompleteOrUnsupportedRoot(string property, string value)
    {
        var node = JsonNode.Parse(Json)!;
        node[property] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => ProfileReader.Parse(node.ToJsonString()));
    }

    [Fact]
    public void RejectsUnrecognizedFieldsAndMissingConstructorValues()
    {
        var node = JsonNode.Parse(Json)!.AsObject();
        node["password"] = "synthetic-value";
        Assert.Throws<JsonException>(() => ProfileReader.Parse(node.ToJsonString()));
        node.Remove("password");
        node.Remove("source");
        Assert.Throws<JsonException>(() => ProfileReader.Parse(node.ToJsonString()));
    }

    [Fact]
    public void RejectsDuplicateIdsAndInvalidCoverageReferences()
    {
        var profile = ProfileReader.Parse(Json);
        var invalid = profile with
        {
            Services = [profile.Services[0], profile.Services[0]],
            Coverage = [profile.Coverage[0] with { SourcePath = "unknown.html" }]
        };
        var errors = ResearchProfileValidator.Validate(invalid);
        Assert.Contains("service.id.invalid", errors);
        Assert.Contains("step.invalid", errors);
    }

    [Theory]
    [InlineData("../secrets.yaml")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/secret")]
    [InlineData("config\\secret")]
    [InlineData("config//secret")]
    [InlineData("config/./secret")]
    public void RejectsPathsOutsideCanonicalRelativeFormat(string value) => Assert.False(ResearchProfileValidator.IsRelativePath(value));

    [Theory]
    [InlineData("image:latest")]
    [InlineData("image:stable")]
    [InlineData("registry.example:5000/image")]
    [InlineData("image:")]
    [InlineData("image:1.*")]
    [InlineData("image@sha256:invalid")]
    [InlineData("image")]
    public void RejectsFloatingAndMissingTags(string value) => Assert.False(ResearchProfileValidator.IsPinnedImage(value));

    [Fact]
    public async Task CanonicalHashesPermitCrLfButDetectChangedAndMissingContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "hh-wizard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string hash = "edeaaff3f1774ad2888673770c6d64097e391bc362d7d6fb34982ddf0efd18cb"; // SHA256("abc\n")
            var profile = ProfileReader.Parse(Json) with
            {
                Artifacts = [new("example.yaml", hash)],
                Coverage = [new("test", "example.yaml", "test", "Automatic", "test", "NotImplemented")]
            };
            var file = Path.Combine(root, "example.yaml");
            await File.WriteAllTextAsync(file, "abc\r\n", TestContext.Current.CancellationToken);
            Assert.Empty(await ProfileReader.VerifySourceAsync(profile, root, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(file, "changed\n", TestContext.Current.CancellationToken);
            Assert.Contains("changed:example.yaml", await ProfileReader.VerifySourceAsync(profile, root, TestContext.Current.CancellationToken));
            File.Delete(file);
            Assert.Contains("missing:example.yaml", await ProfileReader.VerifySourceAsync(profile, root, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellationDoesNotReadArtifacts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProfileReader.VerifySourceAsync(ProfileReader.Parse(Json), Path.GetTempPath(), cancellation.Token));
    }
}
