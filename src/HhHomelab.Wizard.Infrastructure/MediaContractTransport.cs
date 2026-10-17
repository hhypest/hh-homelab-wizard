using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HhHomelab.Wizard.Infrastructure;

public sealed class MediaContractException : InvalidOperationException
{
    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }

    internal MediaContractException(string code, HttpStatusCode? status = null)
        : base(code)
    {
        Code = code;
        StatusCode = status;
    }
}

internal sealed class MediaContractTransport(
    HttpClient http,
    string prefix,
    Action<HttpRequestMessage>? authenticate = null
)
{
    internal async Task<JsonNode?> ReadAsync(string route, CancellationToken token) =>
        await ExchangeAsync(
            HttpMethod.Get,
            route,
            null,
            HttpStatusCode.OK,
            false,
            prefix + ".read",
            token
        );

    internal async Task<JsonNode?> WriteAsync(
        HttpMethod method,
        string route,
        JsonObject? payload,
        HttpStatusCode expected,
        bool readJson,
        string code,
        CancellationToken token
    ) => await ExchangeAsync(method, route, payload, expected, readJson, code, token);

    private async Task<JsonNode?> ExchangeAsync(
        HttpMethod method,
        string route,
        JsonObject? payload,
        HttpStatusCode expected,
        bool readJson,
        string code,
        CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(method, route);
        authenticate?.Invoke(request);
        if (payload is not null)
            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );
        try
        {
            using var response = await http.SendAsync(request, token);
            if (response.StatusCode != expected)
                throw new MediaContractException(code + ".http-error", response.StatusCode);
            if (method != HttpMethod.Get && !readJson)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonNode.ParseAsync(stream, cancellationToken: token);
        }
        catch (HttpRequestException)
        {
            throw new MediaContractException(
                code + (method == HttpMethod.Get ? ".transport-failed" : ".result-unknown")
            );
        }
        catch (JsonException)
        {
            throw new MediaContractException(
                code + (method == HttpMethod.Get ? ".response-invalid" : ".result-unknown")
            );
        }
        // Cancellation is not a rollback; writes may have reached the server.
    }
}

internal static class MediaJson
{
    internal static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Invalid();

    internal static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw Invalid();

    internal static string Text(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null
            ? s
            : throw Invalid();

    internal static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw Invalid();

    internal static int Int(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw Invalid();

    internal static void Credentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl))
            throw new ArgumentException("media.credentials.invalid");
        if (password is null || password.Length < 8 || password.Any(char.IsControl))
            throw new ArgumentException("media.credentials.invalid");
    }

    private static MediaContractException Invalid() => new("media.response-invalid");
}
