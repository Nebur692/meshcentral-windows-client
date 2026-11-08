using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>What a relay tunnel carries (MeshCentral "protocol"). / Lo que lleva un túnel de relé («protocol» de MeshCentral).</summary>
public enum TunnelProtocol
{
    /// <summary>Shell: cmd on Windows, the root shell elsewhere. / Shell: cmd en Windows, la shell de root en el resto.</summary>
    Terminal = 1,
    Desktop = 2,
    Files = 5,
    PowerShell = 6,
    /// <summary>Shell of the logged-in user (not Windows). / Shell del usuario con sesión (no Windows).</summary>
    UserShell = 7,
    UserPowerShell = 9,
}

/// <summary>
/// A relay tunnel to a device's agent through meshrelay.ashx, as the official website opens them: ask the agent
/// over the control channel, connect the browser side, wait for "c", send the options and the protocol.
/// Un túnel de relé al agente de un equipo por meshrelay.ashx, como los abre la web oficial: se le pide al agente
/// por el canal de control, se conecta el lado del navegador, se espera «c» y se mandan las opciones y el protocolo.
/// </summary>
public sealed class MeshTunnel : IAsyncDisposable
{
    private const string ControlChannel = "102938";
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private Task? _loop;
    private int _closed;

    private MeshTunnel(ClientWebSocket ws, string nodeId, TunnelProtocol protocol)
    {
        _ws = ws;
        NodeId = nodeId;
        Protocol = protocol;
    }

    public string NodeId { get; }
    public TunnelProtocol Protocol { get; }

    /// <summary>The server records this session (it answered "cr"). / El servidor graba esta sesión (contestó «cr»).</summary>
    public bool IsRecorded { get; private set; }

    public bool IsOpen => _ws.State == WebSocketState.Open && Volatile.Read(ref _closed) == 0;

    /// <summary>Binary data from the agent. Raised from the receive thread. / Datos binarios del agente, desde el hilo de recepción.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Text frames that are not control messages. / Mensajes de texto que no son de control.</summary>
    public event EventHandler<string>? TextReceived;

    /// <summary>Control messages other than ping/pong (e.g. "console"). / Mensajes de control que no son ping/pong (p. ej. «console»).</summary>
    public event EventHandler<JsonObject>? ControlReceived;

    /// <summary>Raised once when the tunnel ends, except through DisposeAsync. / Se lanza una vez al acabar el túnel, salvo con DisposeAsync.</summary>
    public event EventHandler<MeshClientException>? Closed;

    public static async Task<MeshTunnel> OpenAsync(MeshServerSession session, string nodeId, TunnelProtocol protocol,
        JsonObject? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cookies = await session.GetRelayCookiesAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var p = (int)protocol;

        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = session.Trust.Validate;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.CollectHttpResponseDetails = true;
        var url = session.Server.WebSocketUri("meshrelay.ashx");
        var browserSide = new Uri($"{url}?browser=1&p={p}&nodeid={Uri.EscapeDataString(nodeId)}&id={id}&auth={Uri.EscapeDataString(cookies.Cookie)}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(session.Timeout);
        var tunnel = new MeshTunnel(ws, nodeId, protocol);
        try
        {
            await ws.ConnectAsync(browserSide, cts.Token).ConfigureAwait(false);
            // The agent connects the other side to the same id. / El agente conecta el otro lado al mismo id.
            await session.SendAsync(new JsonObject
            {
                ["action"] = "msg",
                ["type"] = "tunnel",
                ["nodeid"] = nodeId,
                ["usage"] = p,
                ["value"] = $"*{session.Server.BaseUri.AbsolutePath}meshrelay.ashx?p={p}&nodeid={nodeId}&id={id}&rauth={cookies.RelayCookie}",
            }, cts.Token).ConfigureAwait(false);

            var hello = await ReceiveTextAsync(ws, cts.Token).ConfigureAwait(false);
            if (hello is not ("c" or "cr"))
                throw new MeshClientException(MeshFailure.UnexpectedResponse, "tunel/abrir-detalle/respuesta-ilegible", $"The relay answered \"{Short(hello)}\" instead of connecting.");
            tunnel.IsRecorded = hello == "cr";

            if (options is not null)
            {
                options["type"] = "options";
                await tunnel.SendTextAsync(options.ToJsonString(), cts.Token).ConfigureAwait(false);
            }
            await tunnel.SendTextAsync(p.ToString(System.Globalization.CultureInfo.InvariantCulture), cts.Token).ConfigureAwait(false);
            tunnel._loop = Task.Run(tunnel.ReceiveLoopAsync);
            return tunnel;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw new MeshClientException(MeshFailure.Timeout, "tunel/abrir-detalle/dispositivo-mudo",
                "The device did not answer: its agent may be offline, busy or not allowed to open this kind of session.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw new MeshClientException(MeshFailure.ConnectionLost, "tunel/abrir-detalle/conexion-cortada",
                "The server closed the tunnel before it opened (no rights on this device, or the device refused).", ex);
        }
        catch
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Short(string? s) => s is null ? "(closed)" : s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Waits for the first text frame; binary frames and control messages before it are skipped.
    /// Espera el primer mensaje de texto; se saltan los binarios y los de control anteriores.</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var text = r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length) : null;
            ms.SetLength(0);
            if (text is null || IsControl(text, out _)) continue;
            return text;
        }
    }

    private static bool IsControl(string text, out JsonObject? msg)
    {
        msg = null;
        if (!text.StartsWith('{') || !text.Contains(ControlChannel, StringComparison.Ordinal)) return false;
        try { msg = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return false; }
        return msg?["ctrlChannel"]?.ToString() == ControlChannel;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => SendRawAsync(data, WebSocketMessageType.Binary, ct);

    public Task SendTextAsync(string text, CancellationToken ct = default) => SendRawAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

    public Task SendControlAsync(JsonObject message, CancellationToken ct = default)
    {
        message["ctrlChannel"] = ControlChannel;
        return SendTextAsync(message.ToJsonString(), ct);
    }

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open)
            throw new MeshClientException(MeshFailure.ConnectionLost, "tunel/ver-pantalla/conexion-cortada", "The tunnel is closed.");
        try { await WholeMessage.SendAsync(_ws, _sendLock, data, type, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, "tunel/ver-pantalla/conexion-cortada", "The tunnel was closed.", ex);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        Exception? error = null;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _life.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var bytes = ms.ToArray();
                ms.SetLength(0);
                if (r.MessageType == WebSocketMessageType.Binary) { Raise(DataReceived, (ReadOnlyMemory<byte>)bytes); continue; }
                var text = Encoding.UTF8.GetString(bytes);
                if (IsControl(text, out var ctl))
                {
                    var type = ctl!["type"]?.ToString();
                    if (type == "ping") { try { await SendControlAsync(new JsonObject { ["type"] = "pong" }).ConfigureAwait(false); } catch (MeshClientException) { } }
                    else if (type == "close") break;
                    else if (type is not ("pong" or "rtt")) Raise(ControlReceived, ctl);
                    continue;
                }
                Raise(TextReceived, text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or IOException) { error = ex; }
        finally
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0 && !_life.IsCancellationRequested)
            {
                var reason = new MeshClientException(MeshFailure.ConnectionLost, "tunel/ver-pantalla/conexion-cortada", "The tunnel was closed by the device or the server.", error);
                try { Closed?.Invoke(this, reason); } catch (Exception) { }
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null) return;
        try { handler(this, value); } catch (Exception) { /* a subscriber must not kill the tunnel / un suscriptor no puede tumbar el túnel */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_life.IsCancellationRequested) _life.Cancel();
        Interlocked.Exchange(ref _closed, 1);
        if (_ws.State == WebSocketState.Open)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _ws.SendAsync(Encoding.UTF8.GetBytes("{\"ctrlChannel\":\"102938\",\"type\":\"close\"}"), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (Exception) { } }
        _ws.Dispose();
        _sendLock.Dispose();
        _life.Dispose();
    }
}

/// <summary>A terminal session over a tunnel. / Una sesión de terminal sobre un túnel.</summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private TerminalSession(MeshTunnel tunnel) => Tunnel = tunnel;

    public MeshTunnel Tunnel { get; }

    public static async Task<TerminalSession> OpenAsync(MeshServerSession session, string nodeId, TunnelProtocol kind,
        int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (kind is not (TunnelProtocol.Terminal or TunnelProtocol.PowerShell or TunnelProtocol.UserShell or TunnelProtocol.UserPowerShell))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var options = new JsonObject { ["protocol"] = (int)kind, ["cols"] = cols, ["rows"] = rows, ["xterm"] = true };
        return new TerminalSession(await MeshTunnel.OpenAsync(session, nodeId, kind, options, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What the person types. / Lo que escribe la persona.</summary>
    public Task WriteAsync(string text, CancellationToken ct = default) => Tunnel.SendAsync(Encoding.UTF8.GetBytes(text), ct);

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) =>
        Tunnel.SendControlAsync(new JsonObject { ["type"] = "termsize", ["cols"] = cols, ["rows"] = rows }, ct);

    public ValueTask DisposeAsync() => Tunnel.DisposeAsync();
}
