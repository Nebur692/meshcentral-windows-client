using System.Net.WebSockets;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Cancelling a request never costs the connection: a message that started goes whole, one that did not start never goes.
/// Cancelar una petición nunca cuesta la conexión: un mensaje que empezó sale entero, uno que no empezó no sale.
/// </summary>
public class WholeMessageTests
{
    /// <summary>As strict as the real one: a token cancelled during a send aborts the socket. / Tan estricto como el real: un token cancelado durante un envío aborta el socket.</summary>
    private sealed class StrictSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public readonly List<byte[]> Sent = new();
        public TaskCompletionSource? Gate;
        public readonly TaskCompletionSource Sending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            if (_state != WebSocketState.Open) throw new WebSocketException(WebSocketError.InvalidState);
            using var _ = ct.Register(Abort);
            Sending.TrySetResult();
            if (Gate is { } g) await g.Task.ConfigureAwait(false);
            if (_state != WebSocketState.Open) throw new OperationCanceledException(ct);
            Sent.Add(buffer.ToArray());
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public override void Dispose() { }
    }

    private static readonly byte[] Hello = "{\"action\":\"ack\"}"u8.ToArray();

    [Fact]
    public async Task Cancelled_while_it_goes_the_message_goes_whole_and_the_socket_lives()
    {
        var ws = new StrictSocket { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cts = new CancellationTokenSource();
        var send = WholeMessage.SendAsync(ws, new SemaphoreSlim(1, 1), Hello, WebSocketMessageType.Text, cts.Token);
        await ws.Sending.Task;
        cts.Cancel();
        ws.Gate.SetResult();
        await send;
        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.Equal(Hello, Assert.Single(ws.Sent));
    }

    [Fact]
    public async Task Already_cancelled_nothing_goes_and_the_socket_lives()
    {
        var ws = new StrictSocket();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WholeMessage.SendAsync(ws, new SemaphoreSlim(1, 1), Hello, WebSocketMessageType.Text, new CancellationToken(true)));
        Assert.Empty(ws.Sent);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Cancelled_while_waiting_its_turn_it_never_goes_and_the_one_before_finishes()
    {
        var ws = new StrictSocket { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var turn = new SemaphoreSlim(1, 1);
        var first = WholeMessage.SendAsync(ws, turn, Hello, WebSocketMessageType.Text, CancellationToken.None);
        await ws.Sending.Task;
        using var cts = new CancellationTokenSource();
        var second = WholeMessage.SendAsync(ws, turn, "{\"second\":1}"u8.ToArray(), WebSocketMessageType.Text, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        ws.Gate.SetResult();
        await first;
        Assert.Equal(Hello, Assert.Single(ws.Sent));
        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.Equal(1, turn.CurrentCount);
    }
}
