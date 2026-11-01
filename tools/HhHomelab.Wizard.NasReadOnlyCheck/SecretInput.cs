using System.Text;
using HhHomelab.Wizard.Infrastructure;

namespace HhHomelab.Wizard.NasReadOnlyCheck;

/// <summary>Reads the password or the key passphrase at a prompt without echo. Nothing is written to the
/// report, the console or a file; the private key is read from the given local file only.</summary>
internal static class SecretInput
{
    internal static async Task<SshProbeCredentials> ReadCredentialsAsync(CheckOptions options, CancellationToken token)
    {
        if (options.Credential == CredentialKind.Password)
            return SshProbeCredentials.Password(Prompt("SSH password: "));

        var key = await File.ReadAllBytesAsync(options.KeyPath!, token);
        var passphrase = Prompt("Key passphrase (Enter if none): ");
        return SshProbeCredentials.PrivateKey(key, passphrase.Length == 0 ? null : passphrase);
    }

    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        if (Console.IsInputRedirected)
            return Console.In.ReadLine() ?? "";

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                text.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return text.ToString();
    }
}
