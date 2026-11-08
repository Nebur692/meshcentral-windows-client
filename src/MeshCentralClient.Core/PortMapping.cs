using System.Net;
using System.Net.Sockets;

namespace MeshCentralClient.Core;

/// <summary>What a port mapping points at. / A dónde apunta una redirección de puerto.</summary>
/// <param name="NodeId">The device whose agent carries the connections. / El equipo cuyo agente lleva las conexiones.</param>
/// <param name="DeviceName">Its name, to show. / Su nombre, para enseñarlo.</param>
/// <param name="RemotePort">The port reached from the device. / El puerto al que se llega desde el equipo.</param>
/// <param name="RemoteHost">Another machine the device reaches, or null for the device itself. / Otra máquina a la que llega el equipo, o null para el propio equipo.</param>
/// <param name="LocalPort">The port on this PC, or 0 for any free one. / El puerto en este PC, o 0 para uno libre cualquiera.</param>
public sealed record PortTarget(string NodeId, string DeviceName, int RemotePort, string? RemoteHost = null, int LocalPort = 0)
{
    public string Destination => $"{(string.IsNullOrWhiteSpace(RemoteHost) ? DeviceName : RemoteHost)}:{RemotePort}";
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
    private readonly TcpListener _listener;
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
            try { client = await _listener.AcceptTcpClientAsync(_life.Token).ConfigureAwait(false); }
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
        _listener.Stop();
        Task[] running;
        lock (_connections) running = _connections.ToArray();
        try { await Task.WhenAll(running.Append(_accepting)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _life.Dispose();
    }
}
