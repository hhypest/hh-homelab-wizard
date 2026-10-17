using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

public static class ProfileReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static ResearchProfile Parse(string json)
    {
        var profile =
            JsonSerializer.Deserialize<ResearchProfile>(json, Options)
            ?? throw new InvalidDataException("profile.empty");
        var errors = ResearchProfileValidator.Validate(profile);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(", ", errors));
        return profile;
    }

    /// <summary>Checks canonical UTF-8/LF content. This does not prove Git repository identity.</summary>
    public static async Task<IReadOnlyList<string>> VerifySourceAsync(
        ResearchProfile profile,
        string sourceDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var validationErrors = ResearchProfileValidator.Validate(profile);
        if (validationErrors.Count > 0)
            return validationErrors;
        var failures = new List<string>();
        var root = Path.GetFullPath(sourceDirectory);
        foreach (var artifact in profile.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.Combine(root, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            // A research source checkout must not redirect reads through symlinks/reparse points.
            var current = root;
            RejectLink(current);
            foreach (var segment in artifact.Path.Split('/'))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                    RejectLink(current);
            }
            if (!File.Exists(file))
            {
                failures.Add("missing:" + artifact.Path);
                continue;
            }
            var content = await File.ReadAllTextAsync(
                file,
                new UTF8Encoding(false, true),
                cancellationToken
            );
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")))
            );
            if (hash != artifact.Sha256)
                failures.Add("changed:" + artifact.Path);
        }
        return failures;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("source.link.not-supported");
    }
}
