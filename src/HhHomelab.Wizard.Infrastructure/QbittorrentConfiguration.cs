using System.Net;
using System.Text.Json;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

// M0 contract operations, not an installation engine: caller must own the target resources.
// Writes are never retried. After an unknown result, observe state before deciding what to do.
public sealed partial class QbittorrentContractClient
{
    public async Task<QbittorrentDownloadSettings> GetDownloadSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        RequireSession();
        using var json = await ReadJsonAsync(
            "api/v2/app/preferences",
            "qb.preferences",
            cancellationToken
        );
        try
        {
            var root = json.RootElement;
            return new(
                root.GetProperty("save_path").GetString() ?? throw new InvalidOperationException(),
                root.GetProperty("temp_path").GetString() ?? throw new InvalidOperationException(),
                root.GetProperty("temp_path_enabled").GetBoolean()
            );
        }
        catch (Exception exception)
            when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new QbittorrentContractException("qb.preferences.response-invalid");
        }
    }

    /// <returns>False if the observed settings already match; true after a verified write.</returns>
    public async Task<bool> ConfigureDownloadsAsync(
        QbittorrentDownloadSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        if (!settings.IsValid())
            throw new ArgumentException("qb.download-path.invalid", nameof(settings));
        RequireSession();
        _ = await GetVersionAsync(cancellationToken);
        if (settings.Matches(await GetDownloadSettingsAsync(cancellationToken)))
            return false;
        await WritePreferencesAsync(
            new Dictionary<string, object>
            {
                ["save_path"] = settings.SavePath,
                ["temp_path"] = settings.TemporaryPath,
                ["temp_path_enabled"] = settings.TemporaryPathEnabled,
            },
            cancellationToken
        );
        if (!settings.Matches(await GetDownloadSettingsAsync(cancellationToken)))
            throw new QbittorrentContractException("qb.preferences.not-applied");
        return true;
    }

    /// <summary>Requires independent login with a fresh client to verify the new credentials.</summary>
    public async Task ChangeCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        var usernameInvalid =
            string.IsNullOrWhiteSpace(username)
            || username.Length < 3
            || username.Contains(':')
            || username.Any(char.IsControl);
        if (usernameInvalid)
            throw new ArgumentException("qb.credentials.invalid");
        if (password is null || password.Length < 6 || password.Any(char.IsControl))
            throw new ArgumentException("qb.credentials.invalid");
        RequireSession();
        _ = await GetVersionAsync(cancellationToken);
        // A timeout/cancellation may occur after the server changed credentials. Retire this
        // client even on failure: reconcile using fresh clients, never blindly repeat the write.
        authenticated = false;
        await WritePreferencesAsync(
            new Dictionary<string, object>
            {
                ["web_ui_username"] = username,
                ["web_ui_password"] = password,
            },
            cancellationToken
        );
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCategoriesAsync(
        CancellationToken cancellationToken = default
    )
    {
        RequireSession();
        using var json = await ReadJsonAsync(
            "api/v2/torrents/categories",
            "qb.categories",
            cancellationToken
        );
        try
        {
            return json
                .RootElement.EnumerateObject()
                .ToDictionary(
                    p => p.Name,
                    p =>
                        p.Value.GetProperty("savePath").GetString()
                        ?? throw new InvalidOperationException(),
                    StringComparer.Ordinal
                );
        }
        catch (Exception exception)
            when (exception
                    is KeyNotFoundException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            throw new QbittorrentContractException("qb.categories.response-invalid");
        }
    }

    /// <summary>Observe before creating; an existing category with a different path is a conflict.</summary>
    public async Task<bool> EnsureCategoryAsync(
        string category,
        string savePath,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(category) || category.Any(char.IsControl))
            throw new ArgumentException("qb.category.invalid");
        var pathInvalid =
            category.Contains('/')
            || category.Contains('\\')
            || !QbittorrentDownloadSettings.IsDataPath(savePath);
        if (pathInvalid)
            throw new ArgumentException("qb.category.invalid");
        RequireSession();
        _ = await GetVersionAsync(cancellationToken);
        var categories = await GetCategoriesAsync(cancellationToken);
        if (categories.TryGetValue(category, out var existing))
        {
            if (existing.TrimEnd('/') != savePath.TrimEnd('/'))
                throw new QbittorrentContractException("qb.category.conflict");
            return false;
        }
        await PostFormAsync(
            "api/v2/torrents/createCategory",
            new Dictionary<string, string> { ["category"] = category, ["savePath"] = savePath },
            "qb.category",
            cancellationToken
        );
        categories = await GetCategoriesAsync(cancellationToken);
        if (
            !categories.TryGetValue(category, out existing)
            || existing.TrimEnd('/') != savePath.TrimEnd('/')
        )
            throw new QbittorrentContractException("qb.category.not-applied");
        return true;
    }

    private void RequireSession()
    {
        if (!authenticated)
            throw new QbittorrentContractException("qb.session.required");
    }

    private Task WritePreferencesAsync(
        Dictionary<string, object> values,
        CancellationToken cancellationToken
    ) =>
        PostFormAsync(
            "api/v2/app/setPreferences",
            new Dictionary<string, string> { ["json"] = JsonSerializer.Serialize(values) },
            "qb.preferences",
            cancellationToken
        );

    private async Task PostFormAsync(
        string path,
        Dictionary<string, string> fields,
        string code,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Referrer = client.BaseAddress;
        request.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new QbittorrentContractException(code + ".http-error", response.StatusCode);
        }
        catch (HttpRequestException)
        {
            throw new QbittorrentContractException(code + ".result-unknown");
        }
    }

    private async Task<JsonDocument> ReadJsonAsync(
        string path,
        string code,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Referrer = client.BaseAddress;
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new QbittorrentContractException(code + ".http-error", response.StatusCode);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new QbittorrentContractException(code + ".response-invalid");
        }
    }
}
