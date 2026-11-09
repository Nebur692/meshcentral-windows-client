using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>Mouse buttons as the agent expects them (agent-desktop: LEFT 0x02, RIGHT 0x08, MIDDLE 0x20).
/// Botones del ratón como los espera el agente.</summary>
public enum MouseButton : byte { None = 0x00, Left = 0x02, Right = 0x08, Middle = 0x20 }

public sealed record DesktopTile(int X, int Y, ReadOnlyMemory<byte> Jpeg);

/// <summary>Where one remote display is inside the "all displays" image, in its pixels. / Dónde está una pantalla remota dentro de la imagen de «todas las pantallas».</summary>
public sealed record DisplayRect(int Id, int X, int Y, int Width, int Height);

public sealed record DesktopDisplays(IReadOnlyList<int> Displays, int Selected)
{
    /// <summary>65535 = all displays at once. / 65535 = todas las pantallas a la vez.</summary>
    public const int All = 65535;
}

/// <summary>
/// The remote desktop (protocol 2), as MeshCentral's own viewer (agent-desktop-0.0.2.js) speaks it: binary commands
/// [cmd u16][size u16][payload], padded to 8 bytes, with a "jumbo" header (cmd 27) for those over 64 KB, possibly cut
/// across several frames. Input goes back as the same kind of commands.
/// El escritorio remoto (protocolo 2), como lo habla el visor de MeshCentral: órdenes binarias
/// [orden u16][tamaño u16][datos], rellenas hasta 8 bytes, con cabecera «jumbo» (orden 27) para las de más de 64 KB,
/// que pueden llegar partidas en varios mensajes. La entrada vuelve con el mismo tipo de órdenes.
/// </summary>
public sealed class DesktopSession : IAsyncDisposable
{
    private readonly MeshTunnel _tunnel;
    private readonly DesktopFrameParser _parser;

    /// <summary>JPEG quality 1-100. / Calidad JPEG 1-100.</summary>
    public int Quality { get; private set; } = 60;
    /// <summary>1024 = 100 %, 512 = 50 %. / 1024 = 100 %, 512 = 50 %.</summary>
    public int Scaling { get; private set; } = 1024;
    /// <summary>Minimum milliseconds between frames. / Milisegundos mínimos entre fotogramas.</summary>
    public int FrameMs { get; private set; } = 100;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long TilesReceived => Interlocked.Read(ref _tiles);
    private long _tiles;

    /// <summary>The last screen size the agent gave, for whoever subscribes after it came. / El último tamaño que dio el agente, para quien se suscriba después.</summary>
    public (int Width, int Height)? ScreenSize { get; private set; }

    public event EventHandler<(int Width, int Height)>? ScreenSizeChanged;
    /// <summary>Raised from the receive thread; decode and draw it elsewhere. / Desde el hilo de recepción; se decodifica y pinta en otro sitio.</summary>
    public event EventHandler<DesktopTile>? TileReceived;
    public event EventHandler<DesktopDisplays>? DisplaysChanged;
    /// <summary>The position of every remote display (MNG cmd 82). / La posición de cada pantalla remota (orden 82).</summary>
    public event EventHandler<IReadOnlyList<DisplayRect>>? LayoutChanged;
    /// <summary>Messages of the agent ("waiting for the user to accept"…). / Mensajes del agente («esperando a que el usuario acepte»…).</summary>
    public event EventHandler<string>? Message;
    /// <summary>Asked for several times, the image has not come and the agent has not said why. / Pedida varias veces, la imagen no ha llegado y el agente no ha dicho por qué.</summary>
    public event EventHandler? NoImage;
    public event EventHandler<bool>? InputLockChanged;
    public event EventHandler<int>? CursorChanged;

    private DesktopSession(MeshTunnel tunnel)
    {
        _tunnel = tunnel;
        _parser = new DesktopFrameParser(Process);
        tunnel.DataReceived += (_, d) => OnFrame(d.Span);
        tunnel.ControlReceived += (_, c) =>
        {
            if (c.Str("type") == "console" && c.Str("msg") is { Length: > 0 } msg) { _agentSpoke = true; Message?.Invoke(this, msg); }
        };
        _ = AskAgainUntilTheFirstImage();
    }

    private readonly CancellationTokenSource _closing = new();
    private volatile bool _agentSpoke;

    /// <summary>
    /// A desktop opened right after the last one closed sometimes gets no image at all: the server is still closing the
    /// agent's previous tunnel (meshdesktopmultiplex.js). Measured on GitHub (DesktopReopenTests, 2 runs of 7): nothing
    /// came in 15 s, and asking for the screen again (refresh) brought it. So while no tile has arrived, it is asked for
    /// again every few seconds, a few times, and never once an image is in or the agent has said why there is none
    /// (a device without a screen: asking again would only load an agent that already answered).
    /// Un escritorio abierto justo después de cerrar el anterior a veces no recibe ninguna imagen: el servidor aún está
    /// cerrando el túnel anterior del agente. Medido en GitHub (DesktopReopenTests, 2 pasadas de 7): nada en 15 s, y
    /// volver a pedir la pantalla (refrescar) la trajo. Así que mientras no ha llegado ninguna tesela se vuelve a pedir
    /// cada pocos segundos, unas pocas veces, y nunca una vez que hay imagen o el agente ha dicho por qué no la hay
    /// (un equipo sin pantalla: volver a pedir solo cargaría a un agente que ya contestó).
    /// </summary>
    private async Task AskAgainUntilTheFirstImage()
    {
        if (await NoImageWatch.GaveUpAsync(() => Interlocked.Read(ref _tiles) > 0 || _agentSpoke, RefreshAsync, NoImageRetries, NoImageWait, _closing.Token).ConfigureAwait(false))
            NoImage?.Invoke(this, EventArgs.Empty);
    }

    private static readonly TimeSpan NoImageWait = TimeSpan.FromSeconds(3);
    private const int NoImageRetries = 5;

    public MeshTunnel Tunnel => _tunnel;

    public static async Task<DesktopSession> OpenAsync(MeshServerSession session, string nodeId, CancellationToken ct = default) =>
        new(await MeshTunnel.OpenAsync(session, nodeId, TunnelProtocol.Desktop, null, ct).ConfigureAwait(false));

    // ------------------------------------------------------------------ incoming / entrante

    private void OnFrame(ReadOnlySpan<byte> view) => _parser.Feed(view);

    private void Process(int cmd, byte[] v)
    {
        try
        {
            switch (cmd)
            {
                case 3 when v.Length > 8: // tile / tesela
                    Interlocked.Increment(ref _tiles);
                    TileReceived?.Invoke(this, new DesktopTile(BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(6)), v.AsMemory(8)));
                    break;
                case 7 when v.Length >= 8: // screen size / tamaño de pantalla
                    int w = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(4)), h = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(6));
                    var changed = w != Width || h != Height;
                    Width = w; Height = h;
                    // What the website does on a new size: quality, unpause, input lock query, and no key left pressed.
                    // Lo que hace la web con un tamaño nuevo: calidad, quitar la pausa, preguntar el bloqueo, y ninguna tecla pulsada.
                    _ = SafeSend(QualityCommand(), Bytes(0, 8, 0, 5, 0), Bytes(0, 87, 0, 5, 2),
                        KeyCommand(16, false, false), KeyCommand(17, false, false), KeyCommand(18, false, false), KeyCommand(91, false, false), KeyCommand(92, false, false),
                        Bytes(0, 14, 0, 4));
                    ScreenSize = (w, h);
                    if (changed) ScreenSizeChanged?.Invoke(this, (w, h));
                    break;
                case 11 when v.Length >= 6: // displays / pantallas
                    var count = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(4));
                    var list = new List<int>();
                    for (var i = 0; i < count && 8 + i * 2 <= v.Length; i++) list.Add(BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(6 + i * 2)));
                    var sel = v.Length >= 8 + count * 2 ? BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(6 + count * 2)) : 0;
                    DisplaysChanged?.Invoke(this, new DesktopDisplays(list, sel));
                    break;
                case 17 or 65 when v.Length > 4: // agent message, alert / mensaje, alerta
                    var text = Encoding.UTF8.GetString(v, 4, v.Length - 4);
                    if (!text.StartsWith('.')) { _agentSpoke = true; Message?.Invoke(this, text); }
                    break;
                case 82:
                    if (ParseLayout(v) is { Count: > 0 } layout) LayoutChanged?.Invoke(this, layout);
                    break;
                case 87 when v.Length == 5:
                    InputLockChanged?.Invoke(this, v[4] != 0);
                    break;
                case 88 when v.Length == 5:
                    CursorChanged?.Invoke(this, v[4]);
                    break;
            }
        }
        catch (Exception) { /* a subscriber must not kill the stream / un suscriptor no puede tumbar el flujo */ }
    }

    /// <summary>
    /// cmd 82: [82][size] then 10 bytes per display: id, x, y, width, height (u16). Windows places monitors left of or
    /// above the main one at negative coordinates, so x and y are read as signed and moved so the top-left monitor
    /// starts at 0,0 — where it is in the stitched "all displays" image.
    /// orden 82: 10 bytes por pantalla: id, x, y, ancho, alto (u16). Windows pone los monitores a la izquierda o encima
    /// del principal en coordenadas negativas, así que x e y se leen con signo y se desplazan para que el de arriba a la
    /// izquierda empiece en 0,0, que es donde está en la imagen cosida de «todas las pantallas».
    /// </summary>
    internal static IReadOnlyList<DisplayRect>? ParseLayout(ReadOnlySpan<byte> v)
    {
        if (v.Length < 14 || (v.Length - 4) % 10 != 0) return null;
        var raw = new List<DisplayRect>();
        for (var p = 4; p + 10 <= v.Length; p += 10)
        {
            raw.Add(new DisplayRect(
                BinaryPrimitives.ReadUInt16BigEndian(v[p..]),
                BinaryPrimitives.ReadInt16BigEndian(v[(p + 2)..]),
                BinaryPrimitives.ReadInt16BigEndian(v[(p + 4)..]),
                BinaryPrimitives.ReadUInt16BigEndian(v[(p + 6)..]),
                BinaryPrimitives.ReadUInt16BigEndian(v[(p + 8)..])));
        }
        int minX = raw.Min(d => d.X), minY = raw.Min(d => d.Y);
        return raw.Where(d => d.Width > 0 && d.Height > 0)
                  .Select(d => d with { X = d.X - minX, Y = d.Y - minY })
                  .OrderBy(d => d.Y).ThenBy(d => d.X).ToList();
    }

    // ------------------------------------------------------------------ outgoing / saliente

    private static byte[] Bytes(params byte[] b) => b;

    private byte[] QualityCommand() =>
        Bytes(0, 5, 0, 10, 1 /* JPEG */, (byte)Quality, (byte)(Scaling >> 8), (byte)Scaling, (byte)(FrameMs >> 8), (byte)FrameMs);

    internal static byte[] KeyCommand(int virtualKey, bool down, bool extended)
    {
        // agent-desktop: up = action - 1 (down 0, up 1); extended keys use 4 (down) and 3 (up).
        byte action = extended ? (byte)(down ? 4 : 3) : (byte)(down ? 0 : 1);
        return Bytes(0, 1, 0, 6, action, (byte)virtualKey);
    }

    internal static byte[] MouseCommand(MouseButton button, bool? down, int x, int y)
    {
        // down: the button; up: the button × 2; move: 0. / Bajar: el botón; subir: el botón × 2; mover: 0.
        var state = down switch { true => (byte)button, false => (byte)((int)button * 2 & 0xFF), null => (byte)0 };
        return Bytes(0, 2, 0, 10, 0, state, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y);
    }

    internal static byte[] WheelCommand(int x, int y, int delta)
    {
        var d = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
        // The website writes negative deltas as 255 - |d|, i.e. one's complement. / La web escribe los negativos como 255 - |d|: complemento a uno.
        byte hi, lo;
        if (d < 0) { var a = -d; hi = (byte)(255 - (a >> 8)); lo = (byte)(255 - (a & 0xFF)); } else { hi = (byte)(d >> 8); lo = (byte)d; }
        return Bytes(0, 2, 0, 12, 0, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, hi, lo);
    }

    private async Task SafeSend(params byte[][] commands)
    {
        foreach (var c in commands)
        {
            try { await _tunnel.SendAsync(c).ConfigureAwait(false); }
            catch (MeshClientException) { return; } // Closed is reported by the tunnel / el cierre lo avisa el túnel
        }
    }

    public Task SendKeyAsync(int virtualKey, bool down, bool extended = false) => SafeSend(KeyCommand(virtualKey, down, extended));

    /// <summary>Types a character whatever the remote keyboard layout. / Escribe un carácter sea cual sea la distribución del teclado remoto.</summary>
    public Task SendCharAsync(char c) =>
        SafeSend(Bytes(0, 85, 0, 7, 0, (byte)(c >> 8), (byte)c), Bytes(0, 85, 0, 7, 1, (byte)(c >> 8), (byte)c));

    public Task SendMouseMoveAsync(int x, int y) => SafeSend(MouseCommand(MouseButton.None, null, x, y));

    public Task SendMouseButtonAsync(MouseButton button, bool down, int x, int y) => SafeSend(MouseCommand(button, down, x, y));

    public Task SendDoubleClickAsync(int x, int y) => SafeSend(Bytes(0, 2, 0, 10, 0, 0x88, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y));

    /// <summary>120 per wheel notch, positive = up. / 120 por paso de rueda, positivo = arriba.</summary>
    public Task SendWheelAsync(int x, int y, int delta) => SafeSend(WheelCommand(x, y, delta));

    public Task SendCtrlAltDelAsync() => SafeSend(Bytes(0, 10, 0, 4));

    public Task RefreshAsync() => SafeSend(Bytes(0, 6, 0, 4));

    public Task SetPausedAsync(bool paused) => SafeSend(Bytes(0, 8, 0, 5, (byte)(paused ? 1 : 0)));

    public Task SelectDisplayAsync(int display) => SafeSend(Bytes(0, 12, 0, 6, (byte)(display >> 8), (byte)display));

    /// <summary>Blocks the remote keyboard and mouse (Windows). / Bloquea el teclado y el ratón remotos (Windows).</summary>
    public Task SetInputLockAsync(bool locked) => SafeSend(Bytes(0, 87, 0, 5, (byte)(locked ? 1 : 0)));

    /// <summary>quality 1-100, scaling 128-1024 (1024 = 100 %), frameMs ≥ 20. / calidad 1-100, escala 128-1024, frameMs ≥ 20.</summary>
    public Task SetQualityAsync(int quality, int scaling, int frameMs)
    {
        Quality = Math.Clamp(quality, 1, 100);
        Scaling = Math.Clamp(scaling, 128, 1024);
        FrameMs = Math.Clamp(frameMs, 20, 5000);
        return SafeSend(QualityCommand(), Bytes(0, 6, 0, 4));
    }

    public ValueTask DisposeAsync()
    {
        _closing.Cancel();
        return _tunnel.DisposeAsync();
    }
}

/// <summary>Waits, asks again, a few times; true if it all came to nothing. / Espera y vuelve a pedir unas veces; true si no sirvió de nada.</summary>
internal static class NoImageWatch
{
    internal static async Task<bool> GaveUpAsync(Func<bool> settled, Func<Task> askAgain, int retries, TimeSpan wait, CancellationToken ct)
    {
        // One more wait than asks: what the last ask brings also counts. / Una espera más que peticiones: también cuenta lo que traiga la última.
        for (var i = 0; ; i++)
        {
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
            if (settled()) return false;
            if (i == retries) return true;
            await askAgain().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Cuts the agent's desktop stream into commands: padding to 8 bytes, the jumbo header (cmd 27) and commands cut across
/// frames, as agent-redir-ws does. Separate so it can be tested byte for byte.
/// Corta el flujo de escritorio del agente en órdenes: relleno hasta 8 bytes, la cabecera jumbo (orden 27) y órdenes
/// partidas entre mensajes, como hace agent-redir-ws. Aparte para poder probarlo byte a byte.
/// </summary>
internal sealed class DesktopFrameParser(Action<int, byte[]> onCommand)
{
    private readonly object _lock = new();
    private byte[]? _acc;
    private int _accCmd, _accSize, _accLen;

    public void Feed(ReadOnlySpan<byte> view)
    {
        lock (_lock)
        {
            if (_acc is not null)
            {
                var n = Math.Min(view.Length, _accSize - _accLen);
                view[..n].CopyTo(_acc.AsSpan(_accLen));
                _accLen += n;
                if (_accLen < _accSize) return;
                var whole = _acc;
                _acc = null;
                onCommand(_accCmd, whole);
                return;
            }
            if (view.Length < 4) return;
            int c = BinaryPrimitives.ReadUInt16BigEndian(view), size = BinaryPrimitives.ReadUInt16BigEndian(view[2..]);
            if (c == 27 && size == 8 && view.Length >= 10)
            {
                size = (view[5] << 16) | (view[6] << 8) | view[7];
                c = BinaryPrimitives.ReadUInt16BigEndian(view[8..]);
                view = view[8..];
            }
            if (size < 4) return;
            if (size > view.Length)
            {
                _acc = new byte[size];
                view.CopyTo(_acc);
                _accCmd = c;
                _accSize = size;
                _accLen = view.Length;
                return;
            }
            onCommand(c, view[..size].ToArray());
        }
    }
}
