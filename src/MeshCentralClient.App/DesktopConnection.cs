using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>
/// One remote desktop connection shared by every window that shows it. MeshCentral's server sends ONE picture per
/// device to all its viewers (meshdesktopmultiplex.js), so several displays at once means "all displays" and one
/// window per display, each showing its own part of that picture.
/// Una conexión de escritorio remoto compartida por todas las ventanas que la enseñan. El servidor de MeshCentral manda
/// UNA imagen por equipo a todos sus visores (meshdesktopmultiplex.js), así que varias pantallas a la vez es «todas las
/// pantallas» y una ventana por pantalla, cada una con su trozo de esa imagen.
/// </summary>
public sealed class DesktopConnection : IAsyncDisposable
{
    private readonly MeshServerSession _session;
    private readonly List<DesktopWindow> _windows = new();
    private bool _closed;

    public DesktopConnection(MeshServerSession session, DeviceRow device)
    {
        _session = session;
        Device = device;
    }

    public DeviceRow Device { get; }
    public DesktopSession? Session { get; private set; }

    /// <summary>Copy and paste of everything with a Windows device; null elsewhere. / Copiar y pegar de todo con un equipo Windows; null en los demás.</summary>
    public DesktopClipboard? Clipboard { get; private set; }
    public WriteableBitmap? Bitmap { get; private set; }
    public IReadOnlyList<DisplayRect> Layout { get; private set; } = Array.Empty<DisplayRect>();
    public DesktopDisplays? Displays { get; private set; }
    public long Drawn { get; private set; }
    public string Status { get; private set; } = L.T("Connecting");
    public bool IsSplit => _windows.Count > 1;
    public string? ServerVersion => _session.ServerInfo.Version;

    /// <summary>All raised on the UI thread. / Todos se lanzan en el hilo de la interfaz.</summary>
    public event Action? BitmapReplaced;
    public event Action? StatusChanged;
    public event Action? DisplaysChanged;
    public event Action? ClipboardReady;

    public static DesktopWindow Open(MeshServerSession session, DeviceRow device, Action<Window> track)
    {
        var c = new DesktopConnection(session, device) { _track = track };
        var w = new DesktopWindow(c, null);
        c._windows.Add(w);
        track(w);
        w.Show();
        _ = c.StartAsync();
        return w;
    }

    private Action<Window> _track = _ => { };

    private async Task StartAsync()
    {
        try { Session = await DesktopSession.OpenAsync(_session, Device.Id); }
        catch (MeshClientException ex) { Fail(ex); return; }
        if (Device.IsWindows) { Clipboard = new DesktopClipboard(_session, Device.Device); ClipboardReady?.Invoke(); }
        var ui = Application.Current.Dispatcher;
        Session.ScreenSizeChanged += (_, s) => ui.BeginInvoke(() => NewScreen(s.Width, s.Height));
        Session.TileReceived += (_, t) => Decode(t);
        Session.DisplaysChanged += (_, d) => ui.BeginInvoke(() => { Displays = d; DisplaysChanged?.Invoke(); });
        Session.LayoutChanged += (_, l) => ui.BeginInvoke(() => { Layout = l; DisplaysChanged?.Invoke(); });
        Session.Message += (_, m) => ui.BeginInvoke(() => SetStatus(m));
        Session.Tunnel.Closed += (_, e) => ui.BeginInvoke(() => { if (!_closed) Fail(e); });
        DisplaysChanged?.Invoke();
    }

    private void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    private void Fail(MeshClientException ex)
    {
        var err = UserError.From(ex, ServerVersion);
        SetStatus($"{err.Message}  {err.Footer}");
    }

    private void NewScreen(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        BitmapReplaced?.Invoke();
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (Bitmap is not null) SetStatus(L.F("DesktopInfo", Bitmap.PixelWidth, Bitmap.PixelHeight, Drawn));
    }

    /// <summary>Decoded on a worker thread; only the copy into the bitmap runs on the UI thread.
    /// Se decodifica en otro hilo; solo la copia al mapa de bits va en el de la interfaz.</summary>
    private void Decode(DesktopTile tile)
    {
        BitmapSource frame;
        try
        {
            var decoder = new JpegBitmapDecoder(new MemoryStream(tile.Jpeg.ToArray()), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            frame.Freeze();
        }
        catch (Exception) { return; } // a broken tile is skipped; the next refresh repaints it / una tesela rota se salta
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (Bitmap is null || _closed) return;
            var w = Math.Min(frame.PixelWidth, Bitmap.PixelWidth - tile.X);
            var h = Math.Min(frame.PixelHeight, Bitmap.PixelHeight - tile.Y);
            if (w <= 0 || h <= 0) return;
            Bitmap.WritePixels(new Int32Rect(tile.X, tile.Y, w, h), pixels, frame.PixelWidth * 4, 0);
            Drawn++;
            if (Drawn % 25 == 1) UpdateInfo();
        });
    }

    // ------------------------------------------------------------------ several displays / varias pantallas

    /// <summary>
    /// One window per remote display, each maximised on one of the local monitors (the extra ones on the last).
    /// Una ventana por pantalla remota, cada una maximizada en uno de los monitores locales (las que sobran, en el último).
    /// </summary>
    public async Task SplitAsync(DesktopWindow primary)
    {
        if (Session is null || IsSplit) return;
        if (Displays is null || Displays.Selected != DesktopDisplays.All)
        {
            await Session.SelectDisplayAsync(DesktopDisplays.All);
            // The picture becomes the stitched one; the layout tells where each display is in it.
            // La imagen pasa a ser la cosida; la posición dice dónde está cada pantalla en ella.
            for (var i = 0; i < 50 && !LayoutFitsBitmap(); i++) await Task.Delay(100);
        }
        if (Layout.Count < 2 || !LayoutFitsBitmap()) { SetStatus(L.T("SplitUnavailable")); return; }

        var monitors = LocalMonitors.WorkAreas();
        for (var i = 0; i < Layout.Count; i++)
        {
            var w = i == 0 ? primary : new DesktopWindow(this, Layout[i]);
            if (i == 0) primary.ShowOnly(Layout[0]);
            else { _windows.Add(w); _track(w); w.Closed += (_, _) => _windows.Remove(w); }
            PlaceOn(w, monitors[Math.Min(i, monitors.Count - 1)]);
            if (i > 0) w.Show();
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Back to one window with every display. / De vuelta a una ventana con todas las pantallas.</summary>
    public void Join(DesktopWindow primary)
    {
        foreach (var w in _windows.Where(w => w != primary).ToList()) w.Close();
        primary.ShowOnly(null);
        primary.WindowState = WindowState.Normal;
        StatusChanged?.Invoke();
    }

    private bool LayoutFitsBitmap() =>
        Bitmap is not null && Layout.Count > 0 && Layout.All(d => d.X + d.Width <= Bitmap.PixelWidth && d.Y + d.Height <= Bitmap.PixelHeight);

    private static void PlaceOn(Window w, Rect area)
    {
        // Position on that monitor first; maximising then fills the monitor the window is on.
        // Primero se coloca en ese monitor; al maximizar ocupa el monitor donde está la ventana.
        w.WindowState = WindowState.Normal;
        w.Left = area.Left + 40;
        w.Top = area.Top + 40;
        w.Width = Math.Max(480, area.Width - 80);
        w.Height = Math.Max(320, area.Height - 80);
        if (w.IsLoaded) w.WindowState = WindowState.Maximized;
        else w.Loaded += (_, _) => w.WindowState = WindowState.Maximized;
    }

    /// <summary>Closing the main window closes them all and the connection. / Cerrar la principal las cierra todas y la conexión.</summary>
    public async void PrimaryClosed(DesktopWindow primary)
    {
        _closed = true;
        foreach (var w in _windows.Where(w => w != primary).ToList()) w.Close();
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        if (Clipboard is not null) await Clipboard.DisposeAsync();
        if (Session is not null) await Session.DisposeAsync();
    }
}

/// <summary>The work areas of the local monitors, in WPF units, primary first. / Las áreas de trabajo de los monitores locales.</summary>
internal static class LocalMonitors
{
    public static List<Rect> WorkAreas()
    {
        var list = new List<(bool Primary, Rect Area)>();
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add(((mi.dwFlags & 1) != 0, new Rect(mi.rcWork.Left / dpi.DpiScaleX, mi.rcWork.Top / dpi.DpiScaleY,
                    (mi.rcWork.Right - mi.rcWork.Left) / dpi.DpiScaleX, (mi.rcWork.Bottom - mi.rcWork.Top) / dpi.DpiScaleY)));
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) return new() { SystemParameters.WorkArea };
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Area.Left).Select(m => m.Area).ToList();
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
