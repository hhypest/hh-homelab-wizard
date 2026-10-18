using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HhHomelab.Wizard.Infrastructure;

public sealed class MediaContractException : InvalidOperationException
{
    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }

    public ContractFailureKind Kind { get; }

    internal MediaContractException(
        string code,
        HttpStatusCode? status = null,
        ContractFailureKind kind = ContractFailureKind.Contract
    )
        : base(code)
    {
        Code = code;
        StatusCode = status;
        Kind = kind;
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
        token.ThrowIfCancellationRequested();
        var isRead = method == HttpMethod.Get;
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
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw TransportFailure(code, isRead);
        }
        catch (OperationCanceledException)
        {
            if (isRead && token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            throw TransportFailure(code, isRead);
        }
        catch (JsonException)
        {
            throw new MediaContractException(
                code + (isRead ? ".response-invalid" : ".result-unknown"),
                kind: isRead ? ContractFailureKind.Contract : ContractFailureKind.Unknown
            );
        }
    }

    // Once SendAsync is entered, cancellation cannot prove that the server did not apply a write.
    private static MediaContractException TransportFailure(string code, bool isRead) => new(
        code + (isRead ? ".transport-failed" : ".result-unknown"),
        kind: isRead ? ContractFailureKind.Transport : ContractFailureKind.Unknown
    );
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
