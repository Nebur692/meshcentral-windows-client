using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MeshCentralClient.Core;

/// <summary>A terminal the app's terminal window can show, whoever carries it. / Una terminal que la ventana de terminal puede enseñar, la lleve quien la lleve.</summary>
public interface ITerminalConnection : IAsyncDisposable
{
    /// <summary>Bytes for the screen. / Bytes para la pantalla.</summary>
    event EventHandler<ReadOnlyMemory<byte>>? Output;

    /// <summary>The terminal ended, with why. / La terminal acabó, con el porqué.</summary>
    event EventHandler<MeshClientException>? Ended;

    /// <summary>The server records this session. / El servidor graba esta sesión.</summary>
    bool IsRecorded { get; }

    /// <summary>What the person types. / Lo que escribe la persona.</summary>
    Task WriteAsync(string text, CancellationToken ct = default);

    Task ResizeAsync(int cols, int rows, CancellationToken ct = default);
}

/// <summary>How to sign in to an SSH server. / Cómo entrar en un servidor SSH.</summary>
/// <param name="User">The account on the SSH server. / La cuenta en el servidor SSH.</param>
/// <param name="Password">Its password, or null. / Su contraseña, o null.</param>
/// <param name="PrivateKeyPath">A private key file (OpenSSH, PuTTY's .ppk is not read), or null. / Un fichero de clave privada (OpenSSH; el .ppk de PuTTY no se lee), o null.</param>
/// <param name="Passphrase">The key's passphrase, or null. / La frase de la clave, o null.</param>
public sealed record SshLogin(string User, string? Password = null, string? PrivateKeyPath = null, string? Passphrase = null);

/// <summary>The identity an SSH server shows. / La identidad que enseña un servidor SSH.</summary>
/// <param name="Algorithm">E.g. ssh-ed25519. / P. ej. ssh-ed25519.</param>
/// <param name="Fingerprint">SHA256:… as OpenSSH writes it. / SHA256:… como lo escribe OpenSSH.</param>
public sealed record SshHostKey(string Algorithm, string Fingerprint);

/// <summary>
/// An SSH terminal to a device, or to a machine the device reaches, carried by the device's agent: the app listens on a
/// private 127.0.0.1 port (<see cref="PortMapping"/>) and its own SSH client (SSH.NET) connects there. Nothing outside
/// the app is used. The server's identity is checked by the caller (<c>trust</c>), as OpenSSH's known_hosts does.
/// Una terminal SSH a un equipo, o a una máquina a la que llega el equipo, por su agente: la app escucha en un puerto
/// privado de 127.0.0.1 y su propio cliente SSH (SSH.NET) conecta ahí. No se usa nada de fuera de la app. La identidad
/// del servidor la comprueba quien llama (<c>trust</c>), como el known_hosts de OpenSSH.
/// </summary>
public sealed class SshTerminal : ITerminalConnection
{
    private readonly PortMapping _mapping;
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly CancellationTokenSource _life = new();
    private EventHandler<ReadOnlyMemory<byte>>? _output;
    private Task? _reading;
    private int _ended;

    private SshTerminal(PortMapping mapping, SshClient client, ShellStream shell)
    {
        _mapping = mapping;
        _client = client;
        _shell = shell;
        _client.ErrorOccurred += (_, e) => End(Lost(e.Exception));
    }

    /// <summary>The output starts flowing when the first reader subscribes, so the server's first lines are not lost.
    /// La salida empieza a correr al suscribirse el primero, para no perder las primeras líneas del servidor.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Output
    {
        add { _output += value; _reading ??= Task.Run(ReadAsync); }
        remove => _output -= value;
    }

    public event EventHandler<MeshClientException>? Ended;

    public bool IsRecorded => false;

    /// <summary>The identity the server showed. / La identidad que enseñó el servidor.</summary>
    public SshHostKey HostKey { get; private init; } = new("", "");

    public static async Task<SshTerminal> OpenAsync(MeshServerSession session, string nodeId, string deviceName, string? host, int port,
        SshLogin login, Func<SshHostKey, bool> trust, int cols, int rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(trust);
        if (string.IsNullOrWhiteSpace(login.User))
            throw new MeshClientException(MeshFailure.Rejected, "ssh/iniciar-sesion/falta-dato", "The SSH account name is missing.");
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(login.PrivateKeyPath))
        {
            try { methods.Add(new PrivateKeyAuthenticationMethod(login.User, new PrivateKeyFile(login.PrivateKeyPath, string.IsNullOrEmpty(login.Passphrase) ? null : login.Passphrase))); }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new MeshClientException(MeshFailure.Rejected, "ssh/iniciar-sesion/dato-invalido",
                    $"The private key \"{Path.GetFileName(login.PrivateKeyPath)}\" could not be read (wrong passphrase, or not an OpenSSH key).", ex);
            }
        }
        if (!string.IsNullOrEmpty(login.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(login.User, login.Password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(login.User);
            interactive.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = login.Password; };
            methods.Add(interactive);
        }
        if (methods.Count == 0)
            throw new MeshClientException(MeshFailure.Rejected, "ssh/iniciar-sesion/falta-dato", "A password or a private key is needed.");

        var mapping = PortMapping.Start(session, new PortTarget(nodeId, deviceName, port, host));
        SshClient? client = null;
        SshHostKey? shown = null;
        var trusted = false;
        try
        {
            client = new SshClient(new ConnectionInfo("127.0.0.1", mapping.LocalPort, login.User, methods.ToArray()) { Timeout = session.Timeout });
            client.HostKeyReceived += (_, e) =>
            {
                shown = new SshHostKey(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
                e.CanTrust = trusted = trust(shown);
            };
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var shell = client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 64 * 1024);
            return new SshTerminal(mapping, client, shell) { HostKey = shown! };
        }
        catch (Exception ex) when (ex is not MeshClientException and not OperationCanceledException)
        {
            client?.Dispose();
            var why = mapping.LastError;
            await mapping.DisposeAsync().ConfigureAwait(false);
            throw ex switch
            {
                SshAuthenticationException => new MeshClientException(MeshFailure.InvalidCredentials, "ssh/iniciar-sesion/credencial-invalida",
                    $"The SSH server did not accept \"{login.User}\" with that password or key.", ex),
                SshConnectionException when shown is not null && !trusted => new MeshClientException(MeshFailure.UntrustedCertificate, "ssh/abrir-ssh/tls",
                    $"The SSH server's identity ({shown.Fingerprint}) was not trusted, so nothing was sent to it.", ex),
                _ when why is not null => why,
                _ => new MeshClientException(MeshFailure.ConnectionLost, "ssh/abrir-ssh/sin-conexion",
                    $"Nothing answered as an SSH server at {(string.IsNullOrWhiteSpace(host) ? deviceName : host)}:{port} (is SSH running there?).", ex),
            };
        }
        catch
        {
            client?.Dispose();
            await mapping.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var n = await _shell.ReadAsync(buffer, _life.Token).ConfigureAwait(false);
                if (n == 0) break;
                _output?.Invoke(this, buffer.AsMemory(0, n).ToArray());
            }
            End(new MeshClientException(MeshFailure.ConnectionLost, "ssh/abrir-ssh/sesion-cerrada", "The SSH session ended."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or SocketException)
        {
            End(Lost(ex));
        }
    }

    private static MeshClientException Lost(Exception ex) =>
        new(MeshFailure.ConnectionLost, "ssh/abrir-ssh/conexion-cortada", "The SSH connection was lost.", ex);

    private void End(MeshClientException why)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0 && !_life.IsCancellationRequested) Ended?.Invoke(this, why);
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _shell.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _shell.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            throw Lost(ex);
        }
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        try { _shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException) { throw Lost(ex); }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_life.IsCancellationRequested) return;
        await _life.CancelAsync().ConfigureAwait(false);
        try { _shell.Dispose(); } catch (Exception) { }
        try { _client.Disconnect(); } catch (Exception) { }
        _client.Dispose();
        if (_reading is not null) { try { await _reading.ConfigureAwait(false); } catch (Exception) { } }
        await _mapping.DisposeAsync().ConfigureAwait(false);
        _life.Dispose();
    }
}
