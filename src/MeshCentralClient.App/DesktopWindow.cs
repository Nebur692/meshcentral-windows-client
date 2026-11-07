using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeshCentralClient.Core;
using MouseButton = MeshCentralClient.Core.MouseButton;

namespace MeshCentralClient.App;

/// <summary>
/// One view of a remote desktop: the whole picture (main window, with the toolbar) or one display of it (a window per
/// display). The shared bitmap is shown through a clipped canvas, so every view repaints live from the same picture;
/// mouse positions are taken in the view's own coordinates and moved by its offset. Ctrl+Alt+Del and the Windows key,
/// which Windows never lets a window catch, are buttons.
/// Una vista de un escritorio remoto: la imagen entera (la principal, con la barra) o una pantalla de ella (una ventana
/// por pantalla). El mapa de bits compartido se ve a través de un lienzo recortado, así que todas las vistas se repintan
/// en vivo desde la misma imagen; el ratón se toma en las coordenadas de la vista y se desplaza por su origen.
/// </summary>
public sealed class DesktopWindow : Window
{
    private static readonly HashSet<Key> Extended = new()
    {
        Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Insert, Key.Delete,
        Key.Divide, Key.NumLock, Key.RightCtrl, Key.RightAlt, Key.Pause,
    };

    private readonly DesktopConnection _c;
    private readonly bool _primary;
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Viewbox _box = new() { Stretch = Stretch.Uniform };
    private readonly Border _surface = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0) };
    // Notices of an action (typed text, clipboard empty): apart from the status, which the picture keeps rewriting.
    // Avisos de una acción (texto escrito, portapapeles vacío): aparte del estado, que la imagen va reescribiendo.
    private readonly TextBlock _notice = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontWeight = FontWeights.SemiBold };
    private readonly ComboBox _displays = new() { MinWidth = 130, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _quality = new() { MinWidth = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _split = new() { Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed };
    private readonly HashSet<int> _pressed = new();
    private DisplayRect? _viewport;
    private DateTime _lastMove;
    private bool _choosing;

    /// <param name="viewport">null = the whole picture (the main window). / null = la imagen entera (la principal).</param>
    public DesktopWindow(DesktopConnection connection, DisplayRect? viewport)
    {
        _c = connection;
        _primary = viewport is null;
        _viewport = viewport;
        Width = 1100; Height = 760; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Id(this, _primary ? "DesktopWindow" : "DesktopDisplayWindow"); Id(_surface, "DesktopSurface"); Id(_image, "DesktopImage");
        Id(_status, "DesktopStatus"); Id(_notice, "DesktopNotice"); Id(_displays, "DisplayBox"); Id(_quality, "QualityBox"); Id(_split, "SplitButton");
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(_image);
        _box.Child = _canvas;
        _surface.Child = _box;

        var root = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_notice);
        if (_primary)
        {
            tools.Children.Add(_split);
            tools.Children.Add(_displays);
            tools.Children.Add(_quality);
        }
        tools.Children.Add(Action("CtrlAltDelButton", "Ctrl+Alt+Del", async s => await s.SendCtrlAltDelAsync()));
        tools.Children.Add(Action("WinKeyButton", "⊞ Win", async s => { await s.SendKeyAsync(0x5B, true, true); await s.SendKeyAsync(0x5B, false, true); }));
        tools.Children.Add(Action("TypeClipboardButton", L.T("TypeClipboard"), TypeClipboardAsync));
        if (_primary) tools.Children.Add(Action("RefreshDesktop", L.T("Refresh"), async s => await s.RefreshAsync()));
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_surface);
        Content = root;

        _quality.ItemsSource = new[] { L.T("QualityHigh"), L.T("QualityMedium"), L.T("QualityLow") };
        _quality.SelectedIndex = 1;
        _quality.SelectionChanged += async (_, _) => await ApplyQualityAsync();
        _displays.SelectionChanged += async (_, _) =>
        {
            if (!_choosing && _c.Session is { } s && _displays.SelectedItem is DisplayItem d) await s.SelectDisplayAsync(d.Number);
        };
        _split.Click += async (_, _) =>
        {
            if (_c.IsSplit) _c.Join(this); else await _c.SplitAsync(this);
            Refresh();
        };

        // Input / Entrada
        _canvas.MouseMove += (_, e) => Mouse(e, null, null);
        _canvas.MouseDown += (_, e) => { _surface.Focus(); Keyboard.Focus(_surface); _canvas.CaptureMouse(); Mouse(e, Button(e.ChangedButton), true); e.Handled = true; };
        _canvas.MouseUp += (_, e) => { Mouse(e, Button(e.ChangedButton), false); _canvas.ReleaseMouseCapture(); e.Handled = true; };
        _canvas.MouseWheel += async (_, e) => { if (_c.Session is { } s && ToRemote(e.GetPosition(_canvas)) is { } p) await s.SendWheelAsync(p.X, p.Y, e.Delta); e.Handled = true; };
        _surface.PreviewKeyDown += async (_, e) => await KeyAsync(e, true);
        _surface.PreviewKeyUp += async (_, e) => await KeyAsync(e, false);
        // Leaving the window releases whatever was held, or the remote would keep a key down.
        // Al salir de la ventana se suelta lo pulsado, o el equipo seguiría con una tecla apretada.
        Deactivated += async (_, _) => await ReleaseAllAsync();

        _c.BitmapReplaced += Refresh;
        _c.StatusChanged += ShowStatus;
        _c.DisplaysChanged += Refresh;
        Loaded += (_, _) => { Refresh(); ShowStatus(); _surface.Focus(); };
        Closed += async (_, _) =>
        {
            _c.BitmapReplaced -= Refresh; _c.StatusChanged -= ShowStatus; _c.DisplaysChanged -= Refresh;
            await ReleaseAllAsync();
            if (_primary) _c.PrimaryClosed(this);
        };
    }

    private sealed record DisplayItem(int Number, string Text) { public override string ToString() => Text; }

    private static void Id(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    private Button Action(string id, string text, Func<DesktopSession, Task> run)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        Id(b, id);
        // Back to the picture for the next keys, but only if this window is active: focusing an inactive window brings
        // it to the front (seen in a test: typed text went to the viewer, not to the remote program).
        // De vuelta a la imagen para las siguientes teclas, pero solo si la ventana está activa: dar el foco a una ventana
        // inactiva la trae al frente (visto en una prueba: el texto iba al visor, no al programa remoto).
        b.Click += async (_, _) => { if (_c.Session is { } s) await run(s); if (IsActive) _surface.Focus(); };
        return b;
    }

    /// <summary>Shows one display of the picture, or all of it (null). / Enseña una pantalla de la imagen, o toda (null).</summary>
    public void ShowOnly(DisplayRect? viewport)
    {
        _viewport = viewport;
        Refresh();
    }

    private DisplayRect? View =>
        _viewport ?? (_c.Bitmap is { } b ? new DisplayRect(0, 0, 0, b.PixelWidth, b.PixelHeight) : null);

    private void Refresh()
    {
        Title = _viewport is null
            ? $"{L.T("Desktop")} — {_c.Device.Name}"
            : $"{L.F("DisplayN", _viewport.Id)} — {_c.Device.Name}";
        if (_c.Bitmap is { } bmp && View is { } v)
        {
            _image.Source = bmp;
            _image.Width = bmp.PixelWidth;
            _image.Height = bmp.PixelHeight;
            Canvas.SetLeft(_image, -v.X);
            Canvas.SetTop(_image, -v.Y);
            _canvas.Width = v.Width;
            _canvas.Height = v.Height;
        }
        if (!_primary) return;
        _choosing = true;
        var d = _c.Displays;
        var items = d?.Displays.Select(n => new DisplayItem(n, n == DesktopDisplays.All ? L.T("AllDisplays") : L.F("DisplayN", n))).ToList() ?? new();
        _displays.ItemsSource = items;
        _displays.SelectedItem = items.FirstOrDefault(i => i.Number == d?.Selected);
        _displays.Visibility = items.Count > 1 && !_c.IsSplit ? Visibility.Visible : Visibility.Collapsed;
        _choosing = false;
        // Several displays at once: offered when the device has more than one. / Varias a la vez: se ofrece si el equipo tiene más de una.
        var several = (d?.Displays.Count(n => n != DesktopDisplays.All) ?? 0) > 1;
        _split.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        _split.Content = L.T(_c.IsSplit ? "JoinDisplays" : "SplitDisplays");
    }

    private void ShowStatus() => _status.Text = _c.Status;

    private async Task ApplyQualityAsync()
    {
        if (_c.Session is not { } s) return;
        var (q, scale, ms) = _quality.SelectedIndex switch { 0 => (85, 1024, 50), 2 => (35, 512, 200), _ => (60, 1024, 100) };
        await s.SetQualityAsync(q, scale, ms);
    }

    // ------------------------------------------------------------------ input / entrada

    /// <summary>A point on this view → the pixel of the remote picture. / Un punto de esta vista → el píxel de la imagen remota.</summary>
    internal static (int X, int Y)? Map(Point p, DisplayRect view)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= view.Width || p.Y >= view.Height) return null;
        return (view.X + (int)p.X, view.Y + (int)p.Y);
    }

    private (int X, int Y)? ToRemote(Point p) => View is { } v ? Map(p, v) : null;

    private static MouseButton Button(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Right => MouseButton.Right,
        System.Windows.Input.MouseButton.Middle => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private async void Mouse(MouseEventArgs e, MouseButton? button, bool? down)
    {
        if (_c.Session is not { } s || ToRemote(e.GetPosition(_canvas)) is not { } p) return;
        if (button is null)
        {
            // Moves are thinned to ~60 a second. / Los movimientos se aligeran a unos 60 por segundo.
            if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 16) return;
            _lastMove = DateTime.UtcNow;
            await s.SendMouseMoveAsync(p.X, p.Y);
        }
        else if (e is MouseButtonEventArgs { ClickCount: 2 } && down == true && button == MouseButton.Left)
            await s.SendDoubleClickAsync(p.X, p.Y);
        else
            await s.SendMouseButtonAsync(button.Value, down!.Value, p.X, p.Y);
    }

    private static int Generic(int vk) => vk switch { 0xA0 or 0xA1 => 16, 0xA2 or 0xA3 => 17, 0xA4 or 0xA5 => 18, _ => vk };

    private async Task KeyAsync(KeyEventArgs e, bool down)
    {
        if (_c.Session is not { } s) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        // Alt+F4 stays local: it closes this window, as everywhere in Windows. / Alt+F4 se queda en local: cierra esta ventana.
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk > 255) return;
        e.Handled = true;
        if (down) _pressed.Add(vk); else _pressed.Remove(vk);
        // Left and right Shift/Ctrl/Alt travel as the generic key, as the website sends them. / Como los manda la web.
        await s.SendKeyAsync(Generic(vk), down, Extended.Contains(key));
    }

    private async Task ReleaseAllAsync()
    {
        if (_c.Session is not { } s) return;
        foreach (var vk in _pressed.ToList()) await s.SendKeyAsync(Generic(vk), false);
        _pressed.Clear();
    }

    /// <summary>
    /// Types the local clipboard on the device, character by character (Unicode, so the remote keyboard layout does not
    /// matter); line breaks become Enter. For when the remote clipboard is not shared, e.g. a login screen.
    /// Escribe el portapapeles local en el equipo, carácter a carácter (Unicode, así que no importa la distribución del
    /// teclado remoto); los saltos de línea son Intro. Para cuando no se comparte el portapapeles, p. ej. una pantalla de entrada.
    /// </summary>
    private async Task TypeClipboardAsync(DesktopSession s)
    {
        // The clipboard may be busy for a moment (another program holds it): retry briefly instead of giving up silently.
        // El portapapeles puede estar ocupado un momento (otro programa lo tiene): se reintenta un poco en vez de rendirse en silencio.
        var text = "";
        for (var i = 0; i < 10; i++)
        {
            try { text = Clipboard.GetText(); break; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(100); }
        }
        if (text.Length == 0) { _notice.Text = L.T("ClipboardEmpty"); return; }
        if (text.Length > 10_000) text = text[..10_000];
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { await s.SendKeyAsync(13, true); await s.SendKeyAsync(13, false); }
            else await s.SendCharAsync(c);
        }
        _notice.Text = L.F("Typed", text.Length);
    }
}
