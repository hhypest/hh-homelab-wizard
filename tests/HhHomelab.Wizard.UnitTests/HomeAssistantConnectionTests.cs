using System.Text.Json;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantConnectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Uri Endpoint => new("ws://ha:8123/api/websocket");

    [Theory]
    [InlineData("auth_ok", "2026.10.0")]
    [InlineData("auth_required", "2026.11.0")]
    public async Task InvalidHelloDoesNotReceiveAccessToken(string type, string version)
    {
        var socket = new Socket { Greeting = new() { ["type"] = type, ["ha_version"] = version } }; using var connection = new HomeAssistantConnection(socket);
        await Assert.ThrowsAsync<MediaContractException>(() => connection.AuthenticateAsync(Endpoint, "synthetic-sensitive", Token)); Assert.Equal(0, socket.Sends); Assert.True(socket.Disposed);
    }
    [Fact]
    public async Task AuthenticationErrorNeverExposesServerMessage()
    {
        var socket = new Socket { RejectAuthentication = true }; using var connection = new HomeAssistantConnection(socket);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => connection.AuthenticateAsync(Endpoint, "synthetic-sensitive", Token)); Assert.Equal("ha.ws.authentication-rejected", e.Code); Assert.DoesNotContain("synthetic-sensitive", e.ToString());
    }
    [Fact]
    public async Task EventsCannotBeConfusedWithCommandResult()
    {
        var socket = new Socket { EventFirst = true }; using var connection = new HomeAssistantConnection(socket); await connection.AuthenticateAsync(Endpoint, "synthetic-access", Token);
        var result = await connection.CommandAsync("get_config", null, false, Token); Assert.Equal("confirmed", result!["value"]!.GetValue<string>()); Assert.Equal(2, socket.Sends);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedResultIdRetiresSocketAndNeverRetries(bool write)
    {
        var socket = new Socket { WrongId = true }; using var connection = new HomeAssistantConnection(socket); await connection.AuthenticateAsync(Endpoint, "synthetic-access", Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => connection.CommandAsync("command", null, write, Token)); Assert.Equal(write ? "ha.ws.result-unknown" : "ha.ws.response-invalid", e.Code); Assert.True(socket.Disposed); Assert.Equal(2, socket.Sends);
        e = await Assert.ThrowsAsync<MediaContractException>(() => connection.CommandAsync("command", null, true, Token)); Assert.Equal("ha.ws.session.required", e.Code); Assert.Equal(2, socket.Sends);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedJsonNeverLeaksOrRetries(bool write)
    {
        var socket = new Socket { Malformed = true }; using var connection = new HomeAssistantConnection(socket); await connection.AuthenticateAsync(Endpoint, "synthetic-access", Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => connection.CommandAsync("command", null, write, Token)); Assert.DoesNotContain("synthetic-sensitive", e.ToString()); Assert.Equal(write ? "ha.ws.result-unknown" : "ha.ws.transport-failed", e.Code); Assert.Equal(2, socket.Sends); Assert.True(socket.Disposed);
    }
    [Fact]
    public async Task UnknownServerErrorCodeAndTextAreSanitized()
    {
        var socket = new Socket { RejectCommand = true }; using var connection = new HomeAssistantConnection(socket); await connection.AuthenticateAsync(Endpoint, "synthetic-access", Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => connection.CommandAsync("command", null, true, Token)); Assert.Equal("ha.ws.command-rejected", e.Code); Assert.DoesNotContain("synthetic-sensitive", e.ToString());
    }

    private sealed class Socket : IHomeAssistantSocket
    {
        public JsonObject Greeting { get; init; } = new() { ["type"] = "auth_required", ["ha_version"] = "2026.10.0" };
        public bool RejectAuthentication { get; init; }
        public bool EventFirst { get; init; }
        public bool WrongId { get; init; }
        public bool Malformed { get; init; }
        public bool RejectCommand { get; init; }
        public int Sends { get; private set; }
        public bool Disposed { get; private set; }
        private readonly Queue<JsonObject> responses = new();
        public Task ConnectAsync(Uri endpoint, CancellationToken token) { responses.Enqueue(Greeting); return Task.CompletedTask; }
        public Task SendAsync(JsonObject message, CancellationToken token)
        {
            Sends++;
            if (message["type"]!.GetValue<string>() == "auth") responses.Enqueue(new() { ["type"] = RejectAuthentication ? "auth_invalid" : "auth_ok", ["ha_version"] = "2026.10.0", ["message"] = "synthetic-sensitive" });
            else
            {
                if (EventFirst) responses.Enqueue(new() { ["type"] = "event", ["id"] = 999 });
                responses.Enqueue(new() { ["type"] = "result", ["id"] = WrongId ? 999 : message["id"]!.DeepClone(), ["success"] = !RejectCommand,
                    ["result"] = new JsonObject { ["value"] = "confirmed" }, ["error"] = new JsonObject { ["code"] = "synthetic-sensitive", ["message"] = "synthetic-sensitive" } });
            }
            return Task.CompletedTask;
        }
        public Task<JsonObject> ReceiveAsync(CancellationToken token)
        { if (Malformed && Sends > 1) throw new JsonException("synthetic-sensitive"); return Task.FromResult(responses.Dequeue()); }
        public void Dispose() => Disposed = true;
    }
}
