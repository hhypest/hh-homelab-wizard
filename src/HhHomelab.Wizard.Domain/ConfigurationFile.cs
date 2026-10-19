using System.Security.Cryptography;

namespace HhHomelab.Wizard.Domain;

/// <summary>Create one configuration file of this installation below its own marked configuration root.
/// The specification binds root, relative path, exact bytes, mode and owner. Only a missing file may be
/// created; an existing file with other content or mode is never replaced by this request, so a later
/// manual change is reported instead of overwritten. Contents are copied into an owned buffer, never
/// exposed as a mutable one, and zeroed on disposal: a file may carry secrets.</summary>
public sealed class ConfigurationFileRequest : IDisposable
{
    public const int MaximumContentBytes = 64 * 1024;

    public const int MaximumRelativeComponents = 8;

    public const int MaximumComponentLength = 64;

    /// <summary>The only modes a delivered file may get: owner-only for secrets, group- or world-readable
    /// for plain configuration. Directories and special bits are never set by this request.</summary>
    public static IReadOnlyList<int> AllowedModes { get; } = Array.AsReadOnly(new[] { 0x180, 0x1A0, 0x1A4 });

    private readonly byte[] content;

    private bool disposed;

    public ConfigurationFileRequest(OperationIdentity identity, MediaPathLayout layout, InstallationOwner owner,
        string relativePath, int mode, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(relativePath);
        // The bound is checked before any copy or hash, so oversized input costs nothing.
        if (content.Length > MaximumContentBytes)
            throw new ArgumentException("file.content.too-large");
        Identity = identity;
        Layout = layout;
        Owner = owner;
        RelativePath = relativePath;
        Mode = mode;
        this.content = content.ToArray();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(this.content));
    }

    public OperationIdentity Identity { get; }

    public MediaPathLayout Layout { get; }

    public InstallationOwner Owner { get; }

    /// <summary>Slash-separated path below the configuration root, for example media/compose.yaml.</summary>
    public string RelativePath { get; }

    /// <summary>Permission bits as a number, for example 0x1A4 for octal 644.</summary>
    public int Mode { get; }

    public string Sha256 { get; }

    public int Length => content.Length;

    public ReadOnlySpan<byte> Content
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return content;
        }
    }

    public OwnershipMarker Marker => new(OwnershipMarker.CurrentSchema, Owner.InstallationId, Owner.ProfileFingerprint);

    public OperationDescriptor Descriptor => new(Identity, ConfigurationOperationKind.ConfigurationFile,
        ConfigurationRequest.Fingerprint(new
        {
            Kind = ConfigurationOperationKind.ConfigurationFile,
            Layout.ConfigurationRoot,
            RelativePath,
            Mode,
            Length,
            Sha256,
            Owner.InstallationId,
            Owner.ProfileFingerprint,
        }));

    public bool IsValid()
    {
        if (!Owner.IsValid || !Descriptor.IsValid || Identity.Resource.Service != "nas")
            return false;
        if (Identity.Resource.InstallationId != Owner.InstallationId || Identity.Resource.ProfileFingerprint != Owner.ProfileFingerprint)
            return false;
        if (disposed || !AllowedModes.Contains(Mode))
            return false;
        return IsRelativePath(RelativePath);
    }

    /// <summary>Zeroes the owned copy; the descriptor stays valid for journal records.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(content);
        disposed = true;
    }

    /// <summary>ASCII components only, so the path means the same bytes to the shell and the parser; no
    /// empty, dot or dot-dot component and nothing inside the wizard's own marker directory.</summary>
    public static bool IsRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 255)
            return false;
        var components = path.Split('/');
        if (components.Length > MaximumRelativeComponents || components[0] == ".hh-homelab-wizard")
            return false;
        return components.All(ValidComponent);
    }

    private static bool ValidComponent(string component)
    {
        if (component.Length is 0 or > MaximumComponentLength || component is "." or "..")
            return false;
        return component.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}
