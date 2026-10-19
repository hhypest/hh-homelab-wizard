namespace HhHomelab.Wizard.Domain;

/// <summary>Create one directory of this installation (with missing parents) below its own marked
/// configuration root. The specification binds root, relative path, mode and owner. A missing directory is
/// created with exactly this mode; an existing one is accepted only with the same mode and is never
/// re-moded, re-owned or emptied. Paths follow the same ASCII rules as delivered files.</summary>
public sealed class ConfigurationDirectoryRequest
{
    /// <summary>Owner-only, group-readable and world-readable directories: 700, 750, 755.</summary>
    public static IReadOnlyList<int> AllowedModes { get; } = Array.AsReadOnly(new[] { 0x1C0, 0x1E8, 0x1ED });

    public ConfigurationDirectoryRequest(OperationIdentity identity, MediaPathLayout layout, InstallationOwner owner,
        string relativePath, int mode)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length > 255)
            throw new ArgumentException("directory.path.too-long");
        Identity = identity;
        Layout = layout;
        Owner = owner;
        RelativePath = relativePath;
        Mode = mode;
    }

    public OperationIdentity Identity { get; }

    public MediaPathLayout Layout { get; }

    public InstallationOwner Owner { get; }

    public string RelativePath { get; }

    /// <summary>Permission bits as a number, for example 0x1E8 for octal 750.</summary>
    public int Mode { get; }

    public OwnershipMarker Marker => new(OwnershipMarker.CurrentSchema, Owner.InstallationId, Owner.ProfileFingerprint);

    public OperationDescriptor Descriptor => new(Identity, ConfigurationOperationKind.ConfigurationDirectory,
        ConfigurationRequest.Fingerprint(new
        {
            Kind = ConfigurationOperationKind.ConfigurationDirectory,
            Layout.ConfigurationRoot,
            RelativePath,
            Mode,
            Owner.InstallationId,
            Owner.ProfileFingerprint,
        }));

    public bool IsValid()
    {
        if (!IsRelativePath(RelativePath) || !Owner.IsValid || !Descriptor.IsValid || Identity.Resource.Service != "nas")
            return false;
        if (Identity.Resource.InstallationId != Owner.InstallationId || Identity.Resource.ProfileFingerprint != Owner.ProfileFingerprint)
            return false;
        return AllowedModes.Contains(Mode);
    }

    /// <summary>The delivered-file path rules, and no dash-leading component: mkdir never sees an option.</summary>
    public static bool IsRelativePath(string? path) =>
        ConfigurationFileRequest.IsRelativePath(path) && path!.Split('/').All(component => component[0] != '-');
}
