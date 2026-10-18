using System.Globalization;
using System.Text;
using HhHomelab.Wizard.Domain;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HhHomelab.Wizard.Infrastructure;

internal sealed class SshNetProbeWire(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan timeout) : ISshProbeWire, INasPreflightWire
{
    internal const string IdentityCommand = "printf 'hh-homelab-wizard:ssh-probe:v1\\n'; uname -s; id -u";

    internal const string SudoCommand = "sudo -n -l";

    private SshAuthenticationLease? authentication;

    private SshClient? client;

    public async Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
    {
        authentication = credentials.CreateAuthentication(target, timeout);
        client = new SshClient(authentication.ConnectionInfo);
        AttachHostKey(client, acceptKey);
        await client.ConnectAsync(token).ConfigureAwait(false);
    }

    public async Task<SshProbeIdentity> ReadIdentityAsync(CancellationToken token)
    {
        var result = await ExecuteAsync(IdentityCommand, retainOutput: true, token).ConfigureAwait(false);
        return ParseIdentity(result.ExitCode, result.Output);
    }

    public async Task<NasPreflightFacts> ReadPreflightAsync(CancellationToken token)
    {
        var result = await ExecuteAsync(NasPreflightProtocol.Command, retainOutput: true, token).ConfigureAwait(false);
        return NasPreflightProtocol.Parse(result.ExitCode, result.Output);
    }

    internal static SshProbeIdentity ParseIdentity(int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > 256)
            throw new InvalidDataException("ssh.identity.invalid");
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length != 3)
            throw new InvalidDataException("ssh.identity.invalid");
        if (lines[0] != "hh-homelab-wizard:ssh-probe:v1" || lines[1] != "Linux")
            throw new InvalidDataException("ssh.identity.invalid");
        if (!int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            throw new InvalidDataException("ssh.identity.invalid");
        return new(userId);
    }

    public async Task<bool> CanListSudoAsync(CancellationToken token)
    {
        var result = await ExecuteAsync(SudoCommand, retainOutput: false, token).ConfigureAwait(false);
        if (result.ExitCode is null)
            throw new InvalidDataException("ssh.sudo.no-exit-status");
        return result.ExitCode == 0;
    }

    public async Task CheckSftpAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
    {
        using var sftpAuthentication = credentials.CreateAuthentication(target, timeout);
        using var sftp = new SftpClient(sftpAuthentication.ConnectionInfo) { OperationTimeout = timeout };
        AttachHostKey(sftp, acceptKey);
        await sftp.ConnectAsync(token).ConfigureAwait(false);
        var attributes = await sftp.GetAttributesAsync(".", token).ConfigureAwait(false);
        if (!attributes.IsDirectory)
            throw new InvalidDataException("ssh.sftp.invalid");
    }

    private static void AttachHostKey(BaseClient connection, Func<string, byte[], bool> acceptKey)
    {
        connection.HostKeyReceived += (_, key) =>
        {
            key.CanTrust = false;
            key.CanTrust = acceptKey(key.HostKeyName, key.HostKey);
        };
    }

    private async Task<CommandEvidence> ExecuteAsync(string fixedCommand, bool retainOutput, CancellationToken token)
    {
        if (client is null)
            throw new InvalidOperationException("ssh.connection.missing");
        using var command = client.CreateCommand(fixedCommand);
        command.CommandTimeout = timeout;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        var execution = command.ExecuteAsync(abort.Token);
        var output = SshProbeOutput.ReadAsync(command.OutputStream, retainOutput, abort);
        var error = SshProbeOutput.ReadAsync(command.ExtendedOutputStream, retain: false, abort);
        try
        {
            await Task.WhenAll(execution, output, error).ConfigureAwait(false);
        }
        catch
        {
            var invalidOutput = output.Exception?.GetBaseException() is InvalidDataException;
            var invalidError = error.Exception?.GetBaseException() is InvalidDataException;
            if (invalidOutput || invalidError)
                throw new InvalidDataException("ssh.response.invalid");
            throw;
        }
        return new(command.ExitStatus, await output.ConfigureAwait(false));
    }

    public void Dispose()
    {
        try
        {
            client?.Dispose();
        }
        finally
        {
            authentication?.Dispose();
        }
    }

    private sealed record CommandEvidence(int? ExitCode, string Output);
}

internal static class SshProbeOutput
{
    internal const int MaximumBytes = 16 * 1024;

    internal static Task<string> ReadAsync(Stream stream, bool retain, CancellationTokenSource abort)
    {
        return Task.Run(() =>
        {
            // SSH.NET PipeStream reads wait synchronously; disposal unblocks an outstanding read.
            using var cancellation = abort.Token.Register(stream.Dispose);
            using var output = new MemoryStream();
            var buffer = new byte[2048];
            var total = 0;
            try
            {
                while (true)
                {
                    abort.Token.ThrowIfCancellationRequested();
                    var count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    total += count;
                    if (total > MaximumBytes)
                    {
                        abort.Cancel();
                        throw new InvalidDataException("ssh.response.size");
                    }
                    if (retain)
                        output.Write(buffer, 0, count);
                }
                return retain ? new UTF8Encoding(false, true).GetString(output.ToArray()) : "";
            }
            catch (DecoderFallbackException)
            {
                abort.Cancel();
                throw new InvalidDataException("ssh.response.encoding");
            }
            catch
            {
                abort.Cancel();
                throw;
            }
        }, CancellationToken.None);
    }
}
