using System.Formats.Tar;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Sensitive payload. Serialize only to an owned destination; never log or persist locally.</summary>
public sealed class HomeAssistantPayload
{
    private readonly Dictionary<string, byte[]> files;

    internal HomeAssistantPayload(Dictionary<string, byte[]> files) => this.files = files;

    public IReadOnlyList<string> Paths => files.Keys.Order(StringComparer.Ordinal).ToArray();

    public async Task WriteTarAsync(Stream destination, CancellationToken token = default)
    {
        using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var content = new MemoryStream(bytes, writable: false);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = content,
                Mode =
                    path == "secrets.yaml"
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                        : UnixFileMode.UserRead
                            | UnixFileMode.UserWrite
                            | UnixFileMode.GroupRead
                            | UnixFileMode.OtherRead,
                ModificationTime = DateTimeOffset.UnixEpoch,
            };
            await writer.WriteEntryAsync(entry, token);
        }
    }
}
