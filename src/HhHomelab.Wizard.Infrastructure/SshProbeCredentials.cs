using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using Renci.SshNet;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Caller-owned, memory-only input. Never serialized. Dispose after probing; reconnect
/// requires new local input. Managed password/passphrase strings cannot be guaranteed zeroed.</summary>
public sealed class SshProbeCredentials : IDisposable
{
    private readonly object gate = new();

    private string? password;

    private byte[]? privateKey;

    private string? passphrase;

    private bool disposed;

    private SshProbeCredentials(string? password, byte[]? privateKey, string? passphrase)
    {
        this.password = password;
        this.privateKey = privateKey;
        this.passphrase = passphrase;
    }

    public static SshProbeCredentials Password(string password)
    {
        if (password is not { Length: > 0 and <= 4096 })
            throw new ArgumentException("ssh.credentials.invalid");
        return new(password, null, null);
    }

    public static SshProbeCredentials PrivateKey(ReadOnlySpan<byte> key, string? passphrase = null)
    {
        if (key.Length is <= 0 or > 1024 * 1024 || passphrase is { Length: > 4096 })
            throw new ArgumentException("ssh.credentials.invalid");
        return new(null, key.ToArray(), passphrase);
    }

    internal SshAuthenticationLease CreateAuthentication(SshConnectionTarget target, TimeSpan timeout)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PrivateKeyFile? keyFile = null;
            try
            {
                AuthenticationMethod authentication;
                if (privateKey is not null)
                {
                    using var stream = new MemoryStream(privateKey, writable: false);
                    keyFile = passphrase is null ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
                    authentication = new PrivateKeyAuthenticationMethod(target.Username, keyFile);
                }
                else
                {
                    authentication = new PasswordAuthenticationMethod(target.Username, password!);
                }
                var connection = new ConnectionInfo(target.Host, target.Port, target.Username, authentication)
                {
                    Timeout = timeout,
                };
                RestrictAlgorithms(connection);
                return new(connection, keyFile);
            }
            catch (Exception)
            {
                keyFile?.Dispose();
                throw new ArgumentException("ssh.credentials.invalid");
            }
        }
    }

    private static void RestrictAlgorithms(ConnectionInfo connection)
    {
        connection.HostKeyAlgorithms.Remove("ssh-dss");
        connection.HostKeyAlgorithms.Remove("ssh-rsa");
        foreach (var algorithm in connection.KeyExchangeAlgorithms.Keys.Where(name => name.EndsWith("sha1", StringComparison.Ordinal)).ToArray())
            connection.KeyExchangeAlgorithms.Remove(algorithm);
        foreach (var algorithm in connection.Encryptions.Keys.Where(name => name.EndsWith("-cbc", StringComparison.Ordinal)).ToArray())
            connection.Encryptions.Remove(algorithm);
        foreach (var algorithm in connection.HmacAlgorithms.Keys.Where(name => name.Contains("md5", StringComparison.Ordinal) || name.Contains("sha1", StringComparison.Ordinal)).ToArray())
            connection.HmacAlgorithms.Remove(algorithm);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            password = null;
            passphrase = null;
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            privateKey = null;
        }
    }

    public override string ToString() => "ssh.credentials.redacted";
}

internal sealed class SshAuthenticationLease(ConnectionInfo connectionInfo, PrivateKeyFile? keyFile) : IDisposable
{
    internal ConnectionInfo ConnectionInfo { get; } = connectionInfo;

    public void Dispose()
    {
        foreach (var method in ConnectionInfo.AuthenticationMethods)
        {
            if (method is IDisposable disposable)
                disposable.Dispose();
        }
        keyFile?.Dispose();
    }
}
