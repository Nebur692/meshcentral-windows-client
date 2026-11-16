using System.Net;
using System.Net.Sockets;

namespace MeshCentralClient.Core;

/// <summary>What a port mapping points at. / A dónde apunta una redirección de puerto.</summary>
/// <param name="NodeId">The device whose agent carries the connections. / El equipo cuyo agente lleva las conexiones.</param>
/// <param name="DeviceName">Its name, to show. / Su nombre, para enseñarlo.</param>
/// <param name="RemotePort">The port reached from the device. / El puerto al que se llega desde el equipo.</param>
/// <param name="RemoteHost">Another machine the device reaches, or null for the device itself. / Otra máquina a la que llega el equipo, o null para el propio equipo.</param>
/// <param name="LocalPort">The port on this PC, or 0 for any free one. / El puerto en este PC, o 0 para uno libre cualquiera.</param>
/// <param name="Udp">UDP instead of TCP. / UDP en vez de TCP.</param>
public sealed record PortTarget(string NodeId, string DeviceName, int RemotePort, string? RemoteHost = null, int LocalPort = 0, bool Udp = false)
{
    public string Destination => $"{(string.IsNullOrWhiteSpace(RemoteHost) ? DeviceName : RemoteHost)}:{RemotePort}" + (Udp ? " (UDP)" : "");
}

/// <summary>
/// A port on this PC (127.0.0.1 only, so nothing else on the network can use it) whose every connection goes, through
/// the device's agent, to <see cref="PortTarget.RemotePort"/>: MeshCentral Router's port mapping, inside the app.
/// Un puerto de este PC (solo 127.0.0.1, para que nadie más de la red lo use) cuyas conexiones van, por el agente del
/// equipo, al puerto remoto: la redirección de puertos de MeshCentral Router, dentro de la app.
/// </summary>
public sealed class PortMapping : IAsyncDisposable
{
    private readonly MeshServerSession _session;
    private readonly TcpListener? _listener;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _life = new();
    private readonly Task _accepting;
    private readonly List<Task> _connections = new();
    private int _active, _total;
    private long _up, _down;

    private PortMapping(MeshServerSession session, PortTarget target, TcpListener listener)
    {
        _session = session;
        _listener = listener;
        Target = target;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    private PortMapping(MeshServerSession session, PortTarget target, UdpClient udp)
    {
        _session = session;
        _udp = udp;
        Target = target;
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _accepting = Task.Run(ReceiveDatagramsAsync);
    }

    /// <summary>A UDP flow with no traffic this long is closed (UDP has no end of its own). / Sin tráfico este rato, se cierra.</summary>
    public static readonly TimeSpan UdpIdle = TimeSpan.FromMinutes(2);

    /// <summary>
    /// UDP: each local sender (address and port) gets its own relay, as MeshCentral Router does, so answers go back to who
    /// asked; its datagrams queue while the relay opens.
    /// UDP: cada remitente local tiene su propio relé, como MeshCentral Router, para que las respuestas vuelvan a quien
    /// preguntó; sus datagramas esperan en cola mientras se abre el relé.
    /// </summary>
    private async Task ReceiveDatagramsAsync()
    {
        var flows = new System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, System.Threading.Channels.Channel<byte[]>>();
        while (!_life.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(_life.Token).ConfigureAwait(false); }
            catch (SocketException) { continue; } // a "port unreachable" from a previous send / un «puerto inalcanzable» de un envío anterior
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            var from = r.RemoteEndPoint;
            var flow = flows.GetOrAdd(from, ep =>
            {
                var c = System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
                lock (_connections)
                {
                    _connections.RemoveAll(t => t.IsCompleted);
                    _connections.Add(Task.Run(async () => { try { await CarryDatagramsAsync(ep, c.Reader).ConfigureAwait(false); } finally { flows.TryRemove(ep, out _); c.Writer.TryComplete(); } }));
                }
                return c;
            });
            flow.Writer.TryWrite(r.Buffer);
        }
    }

    private async Task CarryDatagramsAsync(IPEndPoint peer, System.Threading.Channels.ChannelReader<byte[]> datagrams)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        Raise();
        try
        {
            TcpRelay relay;
            try { relay = await TcpRelay.OpenAsync(_session, Target.NodeId, Target.RemotePort, Target.RemoteHost, _life.Token, udp: true).ConfigureAwait(false); }
            catch (MeshClientException ex) { LastError = ex; return; }
            catch (OperationCanceledException) { return; }
            await using (relay)
            {
                long up = 0, down = 0;
                relay.Traffic += (_, _) =>
                {
                    var u = relay.BytesUp; var d = relay.BytesDown;
                    Interlocked.Add(ref _up, u - Interlocked.Exchange(ref up, u));
                    Interlocked.Add(ref _down, d - Interlocked.Exchange(ref down, d));
                    Raise();
                };
                await relay.PumpDatagramsAsync(datagrams, async d => { try { await _udp!.SendAsync(d, peer, _life.Token).ConfigureAwait(false); } catch (SocketException) { } },
                    UdpIdle, _life.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Raise();
        }
    }

    public PortTarget Target { get; }

    /// <summary>The port actually listening on 127.0.0.1. / El puerto que escucha de verdad en 127.0.0.1.</summary>
    public int LocalPort { get; }

    public int ActiveConnections => Volatile.Read(ref _active);
    public int TotalConnections => Volatile.Read(ref _total);
    public long BytesUp => Interlocked.Read(ref _up);
    public long BytesDown => Interlocked.Read(ref _down);

    /// <summary>The last connection that could not be carried, or null. / La última conexión que no se pudo llevar, o null.</summary>
    public MeshClientException? LastError { get; private set; }

    /// <summary>Connections or traffic changed. / Cambiaron las conexiones o el tráfico.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts listening. A local port already in use fails here, with its own code, before anything reaches the server.
    /// Empieza a escuchar. Un puerto local ya ocupado falla aquí, con su código, antes de llegar al servidor.
    /// </summary>
    public static PortMapping Start(MeshServerSession session, PortTarget target)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        if (target.RemotePort is < 1 or > 65535)
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/dato-invalido", $"{target.RemotePort} is not a TCP port (1 to 65535).");
        if (target.LocalPort is < 0 or > 65535)
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/dato-invalido", $"{target.LocalPort} is not a TCP port (1 to 65535).");
        if (target.Udp)
        {
            UdpClient udp;
            try { udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, target.LocalPort)); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                throw new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/conflicto-estado",
                    $"UDP port {target.LocalPort} of this PC is already in use by another program.", ex);
            }
            // Windows reports an ICMP "port unreachable" as an error on the next receive: off, it would end the loop.
            // Windows da un «puerto inalcanzable» como error en la siguiente recepción: se apaga, o acabaría el bucle.
            if (OperatingSystem.IsWindows()) { const int SioUdpConnreset = -1744830452; udp.Client.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); }
            return new PortMapping(session, target, udp);
        }
        var listener = new TcpListener(IPAddress.Loopback, target.LocalPort);
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/conflicto-estado",
                $"Port {target.LocalPort} of this PC is already in use by another program.", ex);
        }
        return new PortMapping(session, target, listener);
    }

    private async Task AcceptAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_life.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            client.NoDelay = true;
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(Task.Run(() => CarryAsync(client)));
            }
        }
    }

    private async Task CarryAsync(TcpClient client)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        Raise();
        try
        {
            using (client)
            {
                TcpRelay relay;
                try { relay = await TcpRelay.OpenAsync(_session, Target.NodeId, Target.RemotePort, Target.RemoteHost, _life.Token).ConfigureAwait(false); }
                catch (MeshClientException ex) { LastError = ex; return; }
                catch (OperationCanceledException) { return; }
                await using (relay)
                {
                    long up = 0, down = 0;
                    relay.Traffic += (_, _) =>
                    {
                        var u = relay.BytesUp; var d = relay.BytesDown;
                        Interlocked.Add(ref _up, u - Interlocked.Exchange(ref up, u));
                        Interlocked.Add(ref _down, d - Interlocked.Exchange(ref down, d));
                        Raise();
                    };
                    await relay.PumpAsync(client.GetStream(), _life.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Raise();
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { }
    }

    /// <summary>Stops listening and closes every connection. / Deja de escuchar y cierra todas las conexiones.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_life.IsCancellationRequested) return;
        await _life.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _udp?.Dispose();
        Task[] running;
        lock (_connections) running = _connections.ToArray();
        try { await Task.WhenAll(running.Append(_accepting)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _life.Dispose();
    }
}
