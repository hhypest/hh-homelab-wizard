using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

internal interface IHomeAssistantSocket : IDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken token);
    Task SendAsync(JsonObject message, CancellationToken token);
    Task<JsonObject> ReceiveAsync(CancellationToken token);
}

internal sealed class HomeAssistantSocket : IHomeAssistantSocket
{
    private readonly ClientWebSocket socket = new();
    private bool disposed;

    public async Task ConnectAsync(Uri endpoint, CancellationToken token)
    {
        socket.Options.Proxy = null;
        await socket.ConnectAsync(endpoint, token);
    }

    public async Task SendAsync(JsonObject message, CancellationToken token) =>
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message.ToJsonString()),
            WebSocketMessageType.Text,
            true,
            token
        );

    public async Task<JsonObject> ReceiveAsync(CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var frame = await socket.ReceiveAsync(bytes, token);
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new MediaContractException("ha.ws.message-invalid");
            if (buffer.Length + frame.Count > 1024 * 1024)
                throw new MediaContractException("ha.ws.message-too-large");
            buffer.Write(bytes, 0, frame.Count);
            if (frame.EndOfMessage)
                break;
        }
        buffer.Position = 0;
        return Object(await JsonNode.ParseAsync(buffer, cancellationToken: token));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        socket.Abort();
        socket.Dispose();
    }
}

/// <summary>One request at a time; no subscriptions, retries or generic public command API.</summary>
internal sealed class HomeAssistantConnection : IDisposable
{
    private readonly IHomeAssistantSocket socket;
    private readonly TimeSpan requestTimeout;
    private int sequence;
    private bool ready;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal HomeAssistantConnection(IHomeAssistantSocket socket)
        : this(socket, TimeSpan.FromSeconds(30)) { }

    internal HomeAssistantConnection(IHomeAssistantSocket socket, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        this.socket = socket;
        this.requestTimeout = requestTimeout;
    }

    internal async Task AuthenticateAsync(Uri endpoint, string secret, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        try
        {
            await socket.ConnectAsync(endpoint, deadline.Token);
            var hello = await socket.ReceiveAsync(deadline.Token);
            if (Text(hello, "type") != "auth_required")
                throw new MediaContractException("ha.ws.authentication-context.invalid");
            if (Text(hello, "ha_version") != "2026.10.0")
                throw new MediaContractException("ha.version.unsupported");
            await socket.SendAsync(
                new() { ["type"] = "auth", ["access_token"] = secret },
                deadline.Token
            );
            var answer = await socket.ReceiveAsync(deadline.Token);
            if (Text(answer, "type") != "auth_ok")
                throw new MediaContractException("ha.ws.authentication-rejected");
            if (Text(answer, "ha_version") != "2026.10.0")
                throw new MediaContractException("ha.version.unsupported");
            ready = true;
        }
        catch (MediaContractException)
        {
            Dispose();
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Dispose();
            throw new MediaContractException("ha.ws.authentication-failed");
        }
        catch (OperationCanceledException)
        {
            Dispose();
            throw;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or JsonException)
        {
            Dispose();
            throw new MediaContractException("ha.ws.authentication-failed");
        }
    }

    internal async Task<JsonNode?> CommandAsync(
        string type,
        JsonObject? fields,
        bool write,
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        try
        {
            if (!ready)
                throw new MediaContractException("ha.ws.session.required");
            var id = ++sequence;
            var message = fields?.DeepClone() as JsonObject ?? new();
            message["id"] = id;
            message["type"] = type;
            await socket.SendAsync(message, deadline.Token);
            for (var ignored = 0; ignored < 100; ignored++)
            {
                var answer = await socket.ReceiveAsync(deadline.Token);
                if (Text(answer, "type") == "event")
                    continue;
                if (Text(answer, "type") != "result" || Int(answer, "id") != id)
                    throw new MediaContractException("ha.ws.response-invalid");
                if (!Bool(answer, "success"))
                {
                    var code = Text(Object(answer["error"]), "code");
                    // Do not include free-text server errors, unknown codes, request payloads or tokens.
                    throw new MediaContractException(
                        code == "config_not_found" ? "ha.ws.config-not-found"
                        : code == "unauthorized" ? "ha.ws.unauthorized"
                        : code == "not_running" ? "ha.ws.not-running"
                        : "ha.ws.command-rejected"
                    );
                }
                return answer["result"]?.DeepClone();
            }
            throw new MediaContractException("ha.ws.response-invalid");
        }
        catch (MediaContractException e)
            when (e.Code
                    is "ha.ws.response-invalid"
                        or "ha.ws.message-invalid"
                        or "ha.ws.message-too-large"
                        or "media.response-invalid"
            )
        {
            Dispose();
            throw new MediaContractException(
                write ? "ha.ws.result-unknown" : "ha.ws.response-invalid"
            );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Dispose();
            throw new MediaContractException(
                write ? "ha.ws.result-unknown" : "ha.ws.transport-failed"
            );
        }
        catch (OperationCanceledException)
        {
            Dispose();
            throw;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or JsonException)
        {
            Dispose();
            throw new MediaContractException(
                write ? "ha.ws.result-unknown" : "ha.ws.transport-failed"
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        ready = false;
        socket.Dispose();
    }
}
