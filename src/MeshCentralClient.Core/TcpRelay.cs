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

    /// <param name="udp">
    /// A UDP relay instead (udpport/udpaddr): the agent opens a UDP socket to the port, and each binary message is one
    /// datagram, both ways. Only through an agent: the server's own local relay carries TCP only.
    /// Un relé UDP: el agente abre un socket UDP al puerto y cada mensaje binario es un datagrama, en los dos sentidos.
    /// Solo por un agente: el relé local del propio servidor solo lleva TCP.
    /// </param>
    public static async Task<TcpRelay> OpenAsync(MeshServerSession session, string nodeId, int port, string? host = null, CancellationToken ct = default, bool udp = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (port is < 1 or > 65535)
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/llevar-conexion/dato-invalido", $"{port} is not a {(udp ? "UDP" : "TCP")} port (1 to 65535).");
        var p = udp ? "udp" : "tcp";
        // Where the tunnel goes. A device with its agent: meshrelay.ashx to its agent. A device without one: through the
        // group's relay agent to its address, or (no relay) localrelay.ashx with a cookie for that device and port, and the
        // server connects itself; a device without an agent has no machines behind it, so host is ignored.
        // Adónde va el túnel. Con agente: a su agente. Sin agente: por el agente relé del grupo hasta su dirección, o (sin
        // relé) localrelay.ashx con una cookie de ese equipo y puerto, y conecta el propio servidor.
        var device = await session.KnownDeviceAsync(nodeId, ct).ConfigureAwait(false);
        var local = device is not null && session.IsLocal(device) ? device : null;
        string endpoint, query;
        if (local is null)
        {
            var cookies = await session.GetRelayCookiesAsync(ct).ConfigureAwait(false);
            endpoint = "meshrelay.ashx";
            query = $"nodeid={Uri.EscapeDataString(nodeId)}&{p}port={port}"
                    + (string.IsNullOrWhiteSpace(host) ? "" : $"&{p}addr={Uri.EscapeDataString(host.Trim())}")
                    + $"&auth={Uri.EscapeDataString(cookies.Cookie)}";
        }
        else
        {
            var address = string.IsNullOrWhiteSpace(local.Address)
                ? throw new MeshClientException(MeshFailure.Rejected, "redireccion/llevar-conexion/dato-invalido", $"\"{local.Name}\" has no address.")
                : local.Address.Trim();
            if (session.RelayOf(local) is { } agent)
            {
                var cookies = await session.GetRelayCookiesAsync(ct).ConfigureAwait(false);
                endpoint = "meshrelay.ashx";
                query = $"nodeid={Uri.EscapeDataString(agent)}&{p}port={port}&{p}addr={Uri.EscapeDataString(address)}&auth={Uri.EscapeDataString(cookies.Cookie)}";
            }
            else if (udp)
            {
                throw new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/no-implementado",
                    $"UDP to \"{local.Name}\" needs an agent in its group to relay it: the server alone carries TCP only.");
            }
            else
            {
                endpoint = "localrelay.ashx";
                query = $"auth={Uri.EscapeDataString(await session.LocalCookieAsync(nodeId, port, ct).ConfigureAwait(false))}";
            }
        }
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = session.Trust.Validate;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var relay = new TcpRelay(ws);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(session.Timeout);
        try
        {
            await ws.ConnectAsync(new Uri($"{session.Server.WebSocketUri(endpoint)}?{query}"), cts.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) throw local is not null ? Unreachable(local, port) : Refused(host, port, null);
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
            throw local is not null ? Unreachable(local, port) : Refused(host, port, ex);
        }
        catch
        {
            await relay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A device without an agent that the server (or the group's agent) cannot reach at its address.
    /// Un equipo sin agente al que el servidor (o el agente del grupo) no llega en su dirección.</summary>
    public const string UnreachableKey = "redireccion/llevar-conexion/sin-conexion";

    private static MeshClientException Unreachable(Device local, int port) =>
        new(MeshFailure.Rejected, "redireccion/llevar-conexion/sin-conexion",
            $"Could not reach {local.Address}:{port} (from the server or the group's agent): check the address and that the port is open.");

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

    /// <summary>
    /// For a UDP relay: each datagram from <paramref name="fromLocal"/> goes as one binary message, and each whole message
    /// from the device goes to <paramref name="toLocal"/> as one datagram, until the channel ends, the device closes,
    /// nothing passes for <paramref name="idle"/> (UDP has no end of its own) or <paramref name="ct"/> is cancelled.
    /// Para un relé UDP: cada datagrama va como un mensaje binario y cada mensaje entero del equipo vuelve como un datagrama,
    /// hasta que acaba el canal, cierra el equipo, no pasa nada en <paramref name="idle"/> o se cancela.
    /// </summary>
    public async Task PumpDatagramsAsync(System.Threading.Channels.ChannelReader<byte[]> fromLocal, Func<ReadOnlyMemory<byte>, Task> toLocal,
        TimeSpan idle, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var last = Environment.TickCount64;
        async Task Up()
        {
            await foreach (var d in fromLocal.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await WholeMessage.SendAsync(_ws, _turn, d, WebSocketMessageType.Binary, cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref _up, d.Length);
                Volatile.Write(ref last, Environment.TickCount64);
                Traffic?.Invoke(this, EventArgs.Empty);
            }
        }
        async Task Down()
        {
            var buffer = new byte[64 * 1024];
            while (!cts.IsCancellationRequested)
            {
                using var whole = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    whole.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Binary || whole.Length == 0) continue;
                await toLocal(whole.GetBuffer().AsMemory(0, (int)whole.Length)).ConfigureAwait(false);
                Interlocked.Add(ref _down, whole.Length);
                Volatile.Write(ref last, Environment.TickCount64);
                Traffic?.Invoke(this, EventArgs.Empty);
            }
        }
        async Task Quiet()
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idle.TotalSeconds)), cts.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref last) > idle.TotalMilliseconds) return;
            }
        }
        var tasks = new[] { Task.Run(Up, CancellationToken.None), Task.Run(Down, CancellationToken.None), Task.Run(Quiet, CancellationToken.None) };
        await Task.WhenAny(tasks).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException) { }
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
