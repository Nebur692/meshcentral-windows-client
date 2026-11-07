using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeshCentralClient.Core;
using MouseButton = MeshCentralClient.Core.MouseButton;

namespace MeshCentralClient.App;

/// <summary>
/// The remote desktop of one device: JPEG tiles decoded off the UI thread and drawn into one bitmap, scaled to the window;
/// mouse and keyboard go back in the device's coordinates. Ctrl+Alt+Del and the Windows key, which Windows never lets a
/// window catch, are buttons.
/// El escritorio remoto de un equipo: teselas JPEG decodificadas fuera del hilo de la interfaz y pintadas en un solo mapa
/// de bits, escalado a la ventana; ratón y teclado vuelven en las coordenadas del equipo. Ctrl+Alt+Supr y la tecla de
/// Windows, que Windows nunca deja capturar a una ventana, son botones.
/// </summary>
public sealed class DesktopWindow : Window
{
    private static readonly HashSet<Key> Extended = new()
    {
        Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Insert, Key.Delete,
        Key.Divide, Key.NumLock, Key.RightCtrl, Key.RightAlt, Key.Pause,
    };

    private readonly MeshServerSession _session;
    private readonly DeviceRow _device;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Focusable = true, SnapsToDevicePixels = true };
    private readonly Border _surface = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0) };
    private readonly ComboBox _displays = new() { MinWidth = 130, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _quality = new() { MinWidth = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly List<Control> _actions = new();
    private readonly HashSet<int> _pressed = new();
    private DesktopSession? _desk;
    private WriteableBitmap? _bitmap;
    private long _drawn;
    private bool _closing;
    private DateTime _lastMove;

    public DesktopWindow(MeshServerSession session, DeviceRow device)
    {
        _session = session;
        _device = device;
        Title = $"{L.T("Desktop")} — {device.Name}";
        Width = 1100; Height = 760; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Id(this, "DesktopWindow"); Id(_surface, "DesktopSurface"); Id(_image, "DesktopImage"); Id(_status, "DesktopStatus"); Id(_displays, "DisplayBox"); Id(_quality, "QualityBox");
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_displays);
        tools.Children.Add(_quality);
        tools.Children.Add(Action("CtrlAltDelButton", "Ctrl+Alt+Del", async () => { if (_desk is not null) await _desk.SendCtrlAltDelAsync(); }));
        tools.Children.Add(Action("WinKeyButton", "⊞ Win", async () => { if (_desk is null) return; await _desk.SendKeyAsync(0x5B, true, true); await _desk.SendKeyAsync(0x5B, false, true); }));
        tools.Children.Add(Action("TypeClipboardButton", L.T("TypeClipboard"), TypeClipboardAsync));
        tools.Children.Add(Action("RefreshDesktop", L.T("Refresh"), async () => { if (_desk is not null) await _desk.RefreshAsync(); }));
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(_status);

        _quality.ItemsSource = new[] { L.T("QualityHigh"), L.T("QualityMedium"), L.T("QualityLow") };
        _quality.SelectedIndex = 1;
        _quality.SelectionChanged += async (_, _) => await ApplyQualityAsync();
        _displays.SelectionChanged += async (_, _) =>
        {
            if (_desk is not null && _displays.SelectedItem is DisplayItem d) await _desk.SelectDisplayAsync(d.Number);
        };

        _surface.Child = _image;
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_surface);
        Content = root;

        // Input / Entrada
        _image.MouseMove += (_, e) => Mouse(e, null, null);
        _image.MouseDown += (_, e) => { _surface.Focus(); Keyboard.Focus(_surface); _image.CaptureMouse(); Mouse(e, Button(e.ChangedButton), true); e.Handled = true; };
        _image.MouseUp += (_, e) => { Mouse(e, Button(e.ChangedButton), false); _image.ReleaseMouseCapture(); e.Handled = true; };
        _image.MouseWheel += async (_, e) => { if (_desk is not null && ToRemote(e.GetPosition(_image)) is { } p) await _desk.SendWheelAsync(p.X, p.Y, e.Delta); e.Handled = true; };
        _surface.PreviewKeyDown += async (_, e) => await KeyAsync(e, true);
        _surface.PreviewKeyUp += async (_, e) => await KeyAsync(e, false);
        // Leaving the window releases whatever was held, or the remote would keep a key down.
        // Al salir de la ventana se suelta lo pulsado, o el equipo seguiría con una tecla apretada.
        Deactivated += async (_, _) => await ReleaseAllAsync();

        Loaded += async (_, _) => await StartAsync();
        Closed += async (_, _) => { _closing = true; if (_desk is not null) { await ReleaseAllAsync(); await _desk.DisposeAsync(); } };
    }

    private sealed record DisplayItem(int Number, string Text) { public override string ToString() => Text; }

    private static void Id(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    private Button Action(string id, string text, Func<Task> run)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
        Id(b, id);
        b.Click += async (_, _) => { await run(); _surface.Focus(); };
        _actions.Add(b);
        return b;
    }

    private async Task StartAsync()
    {
        _status.Text = L.T("Connecting");
        try
        {
            _desk = await DesktopSession.OpenAsync(_session, _device.Id);
        }
        catch (MeshClientException ex) { Fail(ex); return; }
        _desk.ScreenSizeChanged += (_, s) => Dispatcher.BeginInvoke(() => NewScreen(s.Width, s.Height));
        _desk.TileReceived += (_, t) => Decode(t);
        _desk.DisplaysChanged += (_, d) => Dispatcher.BeginInvoke(() => ShowDisplays(d));
        _desk.Message += (_, m) => Dispatcher.BeginInvoke(() => _status.Text = m);
        _desk.Tunnel.Closed += (_, e) => Dispatcher.BeginInvoke(() => { if (!_closing) Fail(e); });
        foreach (var c in _actions) c.IsEnabled = true;
        await ApplyQualityAsync();
        _surface.Focus();
    }

    private async Task ApplyQualityAsync()
    {
        if (_desk is null) return;
        var (q, scale, ms) = _quality.SelectedIndex switch { 0 => (85, 1024, 50), 2 => (35, 512, 200), _ => (60, 1024, 100) };
        await _desk.SetQualityAsync(q, scale, ms);
    }

    private void NewScreen(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        UpdateStatus();
    }

    private void ShowDisplays(DesktopDisplays d)
    {
        var items = d.Displays.Select(n => new DisplayItem(n, n == DesktopDisplays.All ? L.T("AllDisplays") : L.F("DisplayN", n))).ToList();
        _displays.ItemsSource = items;
        _displays.SelectedItem = items.FirstOrDefault(i => i.Number == d.Selected);
        _displays.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        Dispatcher.BeginInvoke(() =>
        {
            if (_bitmap is null || _closing) return;
            var w = Math.Min(frame.PixelWidth, _bitmap.PixelWidth - tile.X);
            var h = Math.Min(frame.PixelHeight, _bitmap.PixelHeight - tile.Y);
            if (w <= 0 || h <= 0) return;
            _bitmap.WritePixels(new Int32Rect(tile.X, tile.Y, w, h), pixels, frame.PixelWidth * 4, 0);
            _drawn++;
            if (_drawn % 25 == 1) UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        if (_desk is null || _bitmap is null) return;
        _status.Text = L.F("DesktopInfo", _bitmap.PixelWidth, _bitmap.PixelHeight, _drawn);
    }

    // ------------------------------------------------------------------ input / entrada

    private (int X, int Y)? ToRemote(Point p)
    {
        if (_bitmap is null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return null;
        var x = (int)(p.X * _bitmap.PixelWidth / _image.ActualWidth);
        var y = (int)(p.Y * _bitmap.PixelHeight / _image.ActualHeight);
        if (x < 0 || y < 0 || x >= _bitmap.PixelWidth || y >= _bitmap.PixelHeight) return null;
        return (x, y);
    }

    private static MouseButton Button(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Right => MouseButton.Right,
        System.Windows.Input.MouseButton.Middle => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private async void Mouse(MouseEventArgs e, MouseButton? button, bool? down)
    {
        if (_desk is null || ToRemote(e.GetPosition(_image)) is not { } p) return;
        if (button is null)
        {
            // Moves are thinned to ~60 a second. / Los movimientos se aligeran a unos 60 por segundo.
            if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 16) return;
            _lastMove = DateTime.UtcNow;
            await _desk.SendMouseMoveAsync(p.X, p.Y);
        }
        else if (e is MouseButtonEventArgs { ClickCount: 2 } && down == true && button == MouseButton.Left)
            await _desk.SendDoubleClickAsync(p.X, p.Y);
        else
            await _desk.SendMouseButtonAsync(button.Value, down!.Value, p.X, p.Y);
    }

    private async Task KeyAsync(KeyEventArgs e, bool down)
    {
        if (_desk is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        // Alt+F4 stays local: it closes this window, as everywhere in Windows. / Alt+F4 se queda en local: cierra esta ventana.
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk > 255) return;
        e.Handled = true;
        if (down) _pressed.Add(vk); else _pressed.Remove(vk);
        // Left and right Shift/Ctrl/Alt travel as the generic key, as the website sends them. / Como los manda la web.
        var send = vk switch { 0xA0 or 0xA1 => 16, 0xA2 or 0xA3 => 17, 0xA4 or 0xA5 => 18, _ => vk };
        await _desk.SendKeyAsync(send, down, Extended.Contains(key));
    }

    /// <summary>
    /// Types the local clipboard on the device, character by character (Unicode, so the remote keyboard layout does not
    /// matter); line breaks become Enter. For when the remote clipboard is not shared, e.g. a login screen.
    /// Escribe el portapapeles local en el equipo, carácter a carácter (Unicode, así que no importa la distribución del
    /// teclado remoto); los saltos de línea son Intro. Para cuando no se comparte el portapapeles, p. ej. una pantalla de entrada.
    /// </summary>
    private async Task TypeClipboardAsync()
    {
        if (_desk is null) return;
        // The clipboard may be busy for a moment (another program holds it): retry briefly instead of giving up silently.
        // El portapapeles puede estar ocupado un momento (otro programa lo tiene): se reintenta un poco en vez de rendirse en silencio.
        var text = "";
        for (var i = 0; i < 10; i++)
        {
            try { text = Clipboard.GetText(); break; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(100); }
        }
        if (text.Length == 0) { _status.Text = L.T("ClipboardEmpty"); return; }
        if (text.Length > 10_000) text = text[..10_000];
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { await _desk.SendKeyAsync(13, true); await _desk.SendKeyAsync(13, false); }
            else await _desk.SendCharAsync(c);
        }
    }

    private async Task ReleaseAllAsync()
    {
        if (_desk is null) return;
        foreach (var vk in _pressed.ToList()) await _desk.SendKeyAsync(vk switch { 0xA0 or 0xA1 => 16, 0xA2 or 0xA3 => 17, 0xA4 or 0xA5 => 18, _ => vk }, false);
        _pressed.Clear();
    }

    private void Fail(MeshClientException ex)
    {
        foreach (var c in _actions) c.IsEnabled = false;
        var err = UserError.From(ex, _session.ServerInfo.Version);
        _status.Text = $"{err.Message}  {err.Footer}";
    }
}
