using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Verified upstream bytes, not an installer or a compatibility guarantee.</summary>
public sealed class MonitorDockerPackage
{
    public static string Commit => PinnedRuntimeProfile.Component("monitor_docker").Commit;

    public static string ManifestVersion => PinnedRuntimeProfile.Component("monitor_docker").ManifestVersion;
    private const int MaximumArchiveBytes = 4 * 1024 * 1024;
    private const int MaximumFileBytes = 512 * 1024;
    private static string Prefix => "monitor_docker-" + Commit + "/";
    private const string ComponentPrefix = "custom_components/monitor_docker/";
    private readonly Dictionary<string, byte[]> files;
    internal IReadOnlyDictionary<string, byte[]> Files => files;
    public IReadOnlyList<string> Paths => files.Keys.Order(StringComparer.Ordinal).ToArray();

    private MonitorDockerPackage(Dictionary<string, byte[]> files) => this.files = files;

    /// <summary>Fixed HTTPS source; redirects are rejected, responses and expanded files bounded.</summary>
    public static async Task<MonitorDockerPackage> FetchAsync(CancellationToken token = default)
    {
        _ = ReadLock();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        using var response = await client.GetAsync(
            "https://codeload.github.com/ualex73/monitor_docker/zip/" + Commit,
            HttpCompletionOption.ResponseHeadersRead,
            token
        );
        if (
            response.StatusCode != System.Net.HttpStatusCode.OK
            || response.Content.Headers.ContentLength > MaximumArchiveBytes
        )
            throw new InvalidDataException("monitor-docker.download.rejected");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await ReadBoundedAsync(stream, MaximumArchiveBytes, token);
        return VerifyArchive(bytes, token);
    }

    internal static MonitorDockerPackage VerifyArchive(
        byte[] archive,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        if (archive.Length > MaximumArchiveBytes)
            throw new InvalidDataException("monitor-docker.archive.too-large");
        var expected = ReadLock();
        using var zip = new ZipArchive(
            new MemoryStream(archive, writable: false),
            ZipArchiveMode.Read
        );
        if (zip.Entries.Count > 200)
            throw new InvalidDataException("monitor-docker.archive.too-many-entries");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verified = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            // Check all paths, including repository material that will not be installed.
            if (!entry.FullName.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidDataException("monitor-docker.archive.path.invalid");
            if (!seen.Add(entry.FullName))
                throw new InvalidDataException("monitor-docker.archive.path.invalid");
            var pathInvalid =
                entry.FullName.Contains('\\')
                || entry.FullName.Contains(':')
                || entry.FullName.Split('/').Any(s => s is "." or "..");
            if (pathInvalid)
                throw new InvalidDataException("monitor-docker.archive.path.invalid");
            var path = entry.FullName[Prefix.Length..];
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            var directory = path.Length == 0 || path.EndsWith('/');
            if (type != 0 && type != (directory ? 0x4000 : 0x8000))
                throw new InvalidDataException("monitor-docker.archive.file-type.invalid");
            if (directory)
                continue;
            if (!expected.TryGetValue(path, out var hash))
            {
                if (path.StartsWith(ComponentPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("monitor-docker.archive.unexpected-file");
                continue;
            }
            if (entry.Length > MaximumFileBytes)
                throw new InvalidDataException("monitor-docker.archive.file.too-large");
            using var content = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = content.Read(chunk)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (buffer.Length + read > MaximumFileBytes)
                    throw new InvalidDataException("monitor-docker.archive.file.too-large");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
                throw new InvalidDataException("monitor-docker.archive.hash.invalid");
            var target = path == "LICENSE.md" ? ComponentPrefix + "LICENSE.upstream.md" : path;
            verified.Add(target, bytes);
        }
        if (verified.Count != expected.Count)
            throw new InvalidDataException("monitor-docker.archive.incomplete");
        using var manifest = JsonDocument.Parse(verified[ComponentPrefix + "manifest.json"]);
        var root = manifest.RootElement;
        if (
            root.GetProperty("domain").GetString() != "monitor_docker"
            || root.GetProperty("version").GetString() != ManifestVersion
            || !root.GetProperty("requirements")
                .EnumerateArray()
                .Select(e => e.GetString())
                .SequenceEqual(new[] { "aiodocker==0.25.0", "python-dateutil==2.9.0.post0" })
        )
            throw new InvalidDataException("monitor-docker.manifest.invalid");
        return new MonitorDockerPackage(verified);
    }

    internal static Dictionary<string, string> ReadLock()
    {
        var component = PinnedRuntimeProfile.Component("monitor_docker");
        if (component.Repository != "https://github.com/ualex73/monitor_docker")
            throw new InvalidDataException("monitor-docker.lock.invalid");
        using var stream =
            typeof(MonitorDockerPackage).Assembly.GetManifestResourceStream(
                "monitor-docker.lock.json"
            ) ?? throw new InvalidDataException("monitor-docker.lock.missing");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("monitor-docker.lock.invalid");
        if (root.GetProperty("commit").GetString() != Commit)
            throw new InvalidDataException("monitor-docker.lock.invalid");
        if (
            root.GetProperty("repository").GetString()
            != component.Repository
        )
            throw new InvalidDataException("monitor-docker.lock.invalid");
        if (root.GetProperty("manifestVersion").GetString() != ManifestVersion)
            throw new InvalidDataException("monitor-docker.lock.invalid");
        if (root.GetProperty("license").GetString() != component.License)
            throw new InvalidDataException("monitor-docker.lock.invalid");
        return root.GetProperty("files")
            .EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("path").GetString()!,
                e => e.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal
            );
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximum,
        CancellationToken token
    )
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > maximum)
                throw new InvalidDataException("monitor-docker.download.too-large");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token);
        }
        return buffer.ToArray();
    }
}
