using System.Net.WebSockets;
using System.Text;

namespace MeshCentralClient.Core;

/// <summary>
/// One TCP connection to a port of a device (or of another machine the device reaches), carried by its agent: what the
/// website and MeshCentral Router call a relay. Measured on MeshCentral 1.3.1 (meshrelay.js and the agent's meshcore.js):
/// the app opens meshrelay.ashx with nodeid, tcpport and tcpaddr; the server checks remote control or relay rights,
/// asks the agent to connect that port and sends "c" (or "cr", recorded) when the agent joins; from then on the bytes go
/// as binary messages both ways (the agent drops text messages). If the agent cannot reach the port, it closes.
/// Una conexión TCP a un puerto de un equipo (o de otra máquina a la que llega el equipo), por su agente: lo que la web
/// y MeshCentral Router llaman relé. Medido en 1.3.1: se abre meshrelay.ashx con nodeid, tcpport y tcpaddr; el servidor
/// comprueba permiso de control remoto o de relé, pide al agente conectar ese puerto y manda «c» («cr» si se graba) al
/// unirse el agente; desde ahí los bytes van en mensajes binarios en los dos sentidos (el agente tira los de texto).
/// Si el agente no llega al puerto, cierra.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private long _up, _down;

    private TcpRelay(ClientWebSocket ws) => _ws = ws;

    /// <summary>The session is being recorded by the server. / El servidor graba la sesión.</summary>
    public bool IsRecorded { get; private set; }

    /// <summary>Bytes sent to the device. / Bytes mandados al equipo.</summary>
    public long BytesUp => Interlocked.Read(ref _up);

    /// <summary>Bytes received from the device. / Bytes recibidos del equipo.</summary>
    public long BytesDown => Interlocked.Read(ref _down);

    /// <summary>Raised as bytes go either way. / Se lanza al pasar bytes en cualquier sentido.</summary>
    public event EventHandler? Traffic;

    public static async Task<TcpRelay> OpenAsync(MeshServerSession session, string nodeId, int port, string? host = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (port is < 1 or > 65535)
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/llevar-conexion/dato-invalido", $"{port} is not a TCP port (1 to 65535).");
        var cookies = await session.GetRelayCookiesAsync(ct).ConfigureAwait(false);
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = session.Trust.Validate;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var query = $"nodeid={Uri.EscapeDataString(nodeId)}&tcpport={port}"
                    + (string.IsNullOrWhiteSpace(host) ? "" : $"&tcpaddr={Uri.EscapeDataString(host.Trim())}")
                    + $"&auth={Uri.EscapeDataString(cookies.Cookie)}";
        var relay = new TcpRelay(ws);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(session.Timeout);
        try
        {
            await ws.ConnectAsync(new Uri($"{session.Server.WebSocketUri("meshrelay.ashx")}?{query}"), cts.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) throw Refused(host, port, null);
                if (r.MessageType != WebSocketMessageType.Text || !r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                if (text is "c" or "cr") { relay.IsRecorded = text == "cr"; return relay; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
            throw new MeshClientException(MeshFailure.Timeout, "redireccion/llevar-conexion/dispositivo-mudo",
                "The device did not answer: its agent may be offline or busy.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
            throw Refused(host, port, ex);
        }
        catch
        {
            await relay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static MeshClientException Refused(string? host, int port, Exception? inner) =>
        new(MeshFailure.ConnectionLost, "redireccion/llevar-conexion/conexion-cortada",
            $"The server closed the connection to {(string.IsNullOrWhiteSpace(host) ? "the device" : host)}:{port} (no remote control or relay rights on this device, or the device refused).", inner);

    /// <summary>
    /// Carries bytes between <paramref name="local"/> and the device until either side ends or <paramref name="ct"/> is
    /// cancelled; then both are closed. / Lleva los bytes entre <paramref name="local"/> y el equipo hasta que uno de los
    /// dos acaba o se cancela; entonces se cierran los dos.
    /// </summary>
    public async Task PumpAsync(Stream local, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(() => UpAsync(local, cts.Token), CancellationToken.None);
        var down = Task.Run(() => DownAsync(local, cts.Token), CancellationToken.None);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
        try { local.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { await Task.WhenAll(up, down).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
    }

    private async Task UpAsync(Stream local, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await local.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return; }
            if (n == 0) return;
            await WholeMessage.SendAsync(_ws, _turn, buffer.AsMemory(0, n), WebSocketMessageType.Binary, ct).ConfigureAwait(false);
            Interlocked.Add(ref _up, n);
            Traffic?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DownAsync(Stream local, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return;
            if (r.MessageType != WebSocketMessageType.Binary || r.Count == 0) continue;
            try { await local.WriteAsync(buffer.AsMemory(0, r.Count), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return; }
            Interlocked.Add(ref _down, r.Count);
            Traffic?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CloseAsync()
    {
        if (_ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _ws.Dispose();
        _turn.Dispose();
    }
}
