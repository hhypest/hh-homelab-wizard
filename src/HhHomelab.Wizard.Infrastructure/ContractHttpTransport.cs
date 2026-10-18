using System.Net;
using System.Text.Json;

namespace HhHomelab.Wizard.Infrastructure;

// Does not own the HttpClient, authentication, cookies, resources or installation intent.
// No retries. The adapter supplies status validation and version-specific response parsing.
internal sealed class ContractHttpTransport(
    HttpClient http,
    Func<string, HttpStatusCode?, ContractFailureKind, ContractException> failure,
    ContractHttpTimeouts? timeouts = null,
    bool preserveCallerCancellation = false
)
{
    private readonly ContractHttpTimeouts limits = timeouts ?? ContractHttpTimeouts.Default;

    internal async Task<T> ExchangeAsync<T>(
        HttpRequestMessage request,
        string code,
        Func<HttpResponseMessage, CancellationToken, Task<T>> consume,
        CancellationToken token,
        string? invalidResponseCode = null
    )
    {
        token.ThrowIfCancellationRequested();
        var isRead = request.Method == HttpMethod.Get;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limits.Budget(isRead, http.Timeout));
        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            return await consume(response, deadline.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw TransportFailure(code, isRead);
        }
        catch (OperationCanceledException)
        {
            var keepCancellation = isRead || preserveCallerCancellation;
            if (keepCancellation && token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            throw TransportFailure(code, isRead);
        }
        catch (JsonException)
        {
            if (!isRead)
                throw failure(code + ".result-unknown", null, ContractFailureKind.Unknown);
            var invalidCode = invalidResponseCode ?? code + ".response-invalid";
            throw failure(invalidCode, null, ContractFailureKind.Contract);
        }
    }

    private ContractException TransportFailure(string code, bool isRead) => failure(
        code + (isRead ? ".transport-failed" : ".result-unknown"),
        null,
        isRead ? ContractFailureKind.Transport : ContractFailureKind.Unknown
    );
}
