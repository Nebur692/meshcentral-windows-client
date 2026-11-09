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
    private readonly Button _clipTo = new() { Visibility = Visibility.Collapsed };
    private readonly Button _clipFrom = new() { Visibility = Visibility.Collapsed };
    private readonly Button _closeViewer = new() { Visibility = Visibility.Collapsed };
    // What copy and paste is doing, apart from the other notices. / Lo que hace el copiar y pegar, aparte de los demás avisos.
    private readonly TextBlock _clipNotice = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Opacity = 0.85, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _notice = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontWeight = FontWeights.SemiBold };
    private readonly ComboBox _displays = new() { MinWidth = 120, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _quality = new() { MinWidth = 110, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _split = new() { Visibility = Visibility.Collapsed };
    private readonly Button _fullButton = new();
    private bool _menuOpen, _inputLocked;
    private readonly ProgressBar _transfer = new() { Width = 140, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _transferCancel = new() { Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly DockPanel _dock = new();
    private readonly Border _floating = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(0, 0, 10, 10), MaxWidth = 1100, Visibility = Visibility.Collapsed,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 } };
    private readonly Border _handle = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Width = 96, Height = 6, CornerRadius = new CornerRadius(0, 0, 4, 4), Background = Brushes.Gray, Opacity = 0.8, Visibility = Visibility.Collapsed };
    // At normal priority: at its default one a picture that keeps changing never lets it run (measured: the bar never hid).
    // A prioridad normal: con la suya una imagen que no para de cambiar no lo deja correr nunca (medido: la barra no se escondía).
    private readonly System.Windows.Threading.DispatcherTimer _hideBar = new(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(3) };
    private StackPanel _bar = null!;
    private bool _full, _transferring;
    private (WindowState State, WindowStyle Style, ResizeMode Resize) _beforeFull;
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

        // The buttons wrap when the window is narrow, and the status and notices have their own line: nothing is cut off.
        // Los botones saltan de fila si la ventana es estrecha, y estado y avisos tienen su línea: no se corta nada.
        var bar = _bar = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        // In groups, from what acts on the device to what acts on this window. / Por grupos, de lo que actúa sobre el equipo a lo que actúa sobre esta ventana.
        tools.Children.Add(MenuButton("DeviceMenuButton", Glyphs.Device, L.T("DeviceMenuButton"), () => (Application.Current.MainWindow as MainWindow)?.DeviceMenuFor(_c.Device.Device) ?? Array.Empty<object>()));
        tools.Children.Add(MenuButton("KeysMenuButton", Glyphs.Keyboard, L.T("KeysMenuButton"), KeysMenu));
        tools.Children.Add(Action("CtrlAltDelButton", null, "Ctrl+Alt+Del", "Ctrl+Alt+Del", async s => await s.SendCtrlAltDelAsync()));
        tools.Children.Add(Action("WinKeyButton", Glyphs.Windows, null, "Win", async s => { await s.SendKeyAsync(0x5B, true, true); await s.SendKeyAsync(0x5B, false, true); }));
        tools.Children.Add(Gap());
        tools.Children.Add(Action("TypeClipboardButton", Glyphs.Type, null, L.T("TypeClipboard"), TypeClipboardAsync));
        Flat(_clipTo, Glyphs.Up, null, L.T("ClipToDevice") + ". " + L.T("ClipToDeviceHelp"));
        Flat(_clipFrom, Glyphs.Down, null, L.T("ClipFromDevice") + ". " + L.T("ClipFromDeviceHelp"));
        tools.Children.Add(_clipTo);
        tools.Children.Add(_clipFrom);
        tools.Children.Add(Gap());
        if (_primary)
        {
            Flat(_split, Glyphs.Monitors, null, L.T("SplitDisplays"));
            tools.Children.Add(_split);
            tools.Children.Add(_displays);
            _quality.ToolTip = L.T("QualityHelp");
            tools.Children.Add(_quality);
            tools.Children.Add(Action("RefreshDesktop", Glyphs.Refresh, null, L.T("Refresh"), async s => await s.RefreshAsync()));
        }
        Id(_fullButton, "FullScreenButton");
        Flat(_fullButton, Glyphs.FullScreen, null, L.T("FullScreen") + ". " + L.T("FullScreenHelp"));
        _fullButton.Click += (_, _) => ToggleFullScreen();
        tools.Children.Add(_fullButton);
        Id(_closeViewer, "CloseViewerButton");
        Flat(_closeViewer, Glyphs.Close, null, L.T("CloseViewer"));
        _closeViewer.Click += (_, _) => Close();
        tools.Children.Add(_closeViewer);
        bar.Children.Add(tools);
        var info = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_notice, Dock.Right); DockPanel.SetDock(_clipNotice, Dock.Right); DockPanel.SetDock(_transfer, Dock.Right); DockPanel.SetDock(_transferCancel, Dock.Right);
        info.Children.Add(_notice);
        info.Children.Add(_transferCancel);
        info.Children.Add(_transfer);
        info.Children.Add(_clipNotice);
        info.Children.Add(_status);
        bar.Children.Add(info);
        DockPanel.SetDock(bar, Dock.Top);
        _dock.Children.Add(bar);
        _dock.Children.Add(_surface);
        var layers = new Grid();
        layers.Children.Add(_dock);
        layers.Children.Add(_handle);
        layers.Children.Add(_floating);
        Content = layers;
        // In full screen the bar floats over the picture and hides; the top edge brings it back, as in Remote Desktop.
        // En pantalla completa la barra flota sobre la imagen y se esconde; el borde de arriba la devuelve, como en Escritorio remoto.
        PreviewMouseMove += (_, e) => { if (_full && e.GetPosition(this).Y <= _handle.Height) ShowBar(); };
        _hideBar.Tick += (_, _) => HideBarIfIdle();
        Id(_transfer, "ClipProgress"); Id(_transferCancel, "ClipCancelButton");
        _transferCancel.Content = L.T("ClipCancel");
        _transferCancel.Click += (_, _) => { _c.Clipboard?.CancelTransfer(); if (IsActive) _surface.Focus(); };

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
        // Ctrl+Alt+Pause stays local: full screen, the same keys as Remote Desktop. For the whole window, wherever the focus is, as
        // the floating bar can hold it. / Ctrl+Alt+Pausa se queda en local: pantalla completa, las mismas teclas que Escritorio remoto.
        // Para toda la ventana, esté donde esté el foco, porque la barra flotante puede tenerlo.
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (!DesktopKeys.IsFullScreen(key, Keyboard.Modifiers)) return;
            e.Handled = true;
            if (!e.IsRepeat) { _ = ReleaseAllAsync(); ToggleFullScreen(); }
        };
        PreviewKeyUp += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key is System.Windows.Input.Key.Pause or System.Windows.Input.Key.Cancel && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) e.Handled = true;
        };
        _surface.PreviewKeyDown += (_, e) => OnKey(e, true);
        _surface.PreviewKeyUp += (_, e) => OnKey(e, false);
        // Leaving the window releases whatever was held, or the remote would keep a key down; and brings what the device copied.
        // Al salir de la ventana se suelta lo pulsado, o el equipo seguiría con una tecla apretada; y se trae lo que copió el equipo.
        Deactivated += async (_, _) => { await ReleaseAllAsync(); _c.Clipboard?.Left(); };
        // Entering takes this PC's clipboard to the device. / Al entrar se lleva al equipo el portapapeles de este PC.
        Activated += (_, _) => _c.Clipboard?.Entered();
        Id(_clipNotice, "ClipboardNotice"); Id(_clipTo, "ClipToDeviceButton"); Id(_clipFrom, "ClipFromDeviceButton");
        _clipTo.Click += (_, _) => { _c.Clipboard?.PushNow(); if (IsActive) _surface.Focus(); };
        _clipFrom.Click += (_, _) => { _c.Clipboard?.Left(); if (IsActive) _surface.Focus(); };
        void ClipboardReady()
        {
            if (_c.Clipboard is not { } clip) return;
            clip.Notice += t => { _clipNotice.Text = t; _clipNotice.ToolTip = t; if (_full) ShowBar(); };
            clip.Transfer += ShowTransfer;
            clip.OnlyText += () => _clipTo.Visibility = _clipFrom.Visibility = Visibility.Collapsed;
            _clipTo.Visibility = _clipFrom.Visibility = clip.CanAll ? Visibility.Visible : Visibility.Collapsed;
            if (IsActive) clip.Entered();
        }
        _c.ClipboardReady += ClipboardReady;
        ClipboardReady();

        _c.BitmapReplaced += Refresh;
        _c.StatusChanged += ShowStatus;
        _c.DisplaysChanged += Refresh;
        Loaded += (_, _) => { Refresh(); ShowStatus(); _surface.Focus(); };
        Closed += async (_, _) =>
        {
            _c.BitmapReplaced -= Refresh; _c.StatusChanged -= ShowStatus; _c.DisplaysChanged -= Refresh;
            _hideBar.Stop();
            await ReleaseAllAsync();
            if (_primary) _c.PrimaryClosed(this);
        };
    }

    private void ShowTransfer(ClipTransfer? t)
    {
        _transferring = t is not null;
        _transfer.Visibility = t is null ? Visibility.Collapsed : Visibility.Visible;
        _transferCancel.Visibility = t is { CanCancel: true } ? Visibility.Visible : Visibility.Collapsed;
        if (t is null) { if (_full) ShowBar(); return; }
        _transfer.IsIndeterminate = t.Fraction is null;
        _transfer.Value = t.Fraction ?? 0;
        _clipNotice.Text = t.Fraction is { } f ? $"{t.Text} — {f:P0}" : t.Text;
        _clipNotice.ToolTip = _clipNotice.Text;
        if (_full) ShowBar();
    }

    /// <summary>The whole screen and back; on the screen this window is on. / Toda la pantalla y vuelta; en la pantalla donde esté esta ventana.</summary>
    private void ToggleFullScreen()
    {
        _full = !_full;
        if (_full)
        {
            _beforeFull = (WindowState, WindowStyle, ResizeMode);
            _dock.Children.Remove(_bar);
            _floating.Background = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? SystemColors.WindowBrush;
            _floating.Child = _bar;
            // A window already maximized would keep the taskbar: Windows only covers it when it maximizes without a frame.
            // Una ventana ya maximizada conservaría la barra de tareas: Windows solo la tapa al maximizar sin marco.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _handle.Visibility = Visibility.Visible;
            ShowBar();
        }
        else
        {
            _hideBar.Stop();
            _floating.Child = null;
            _floating.Visibility = _handle.Visibility = Visibility.Collapsed;
            _dock.Children.Insert(0, _bar);
            WindowState = WindowState.Normal;
            WindowStyle = _beforeFull.Style;
            ResizeMode = _beforeFull.Resize;
            WindowState = _beforeFull.State;
        }
        Flat(_fullButton, _full ? Glyphs.BackToWindow : Glyphs.FullScreen, null, L.T(_full ? "FullScreenExit" : "FullScreen") + ". " + L.T("FullScreenHelp"));
        _closeViewer.Visibility = _full ? Visibility.Visible : Visibility.Collapsed;
        ShowStatus();
        // Changing the frame can leave the window without keyboard focus, and then not even these keys arrive (measured). This asks
        // for it after the change has settled. / Cambiar el marco puede dejar la ventana sin foco de teclado, y entonces ni estas teclas
        // llegan (medido). Se pide cuando el cambio ya se ha asentado.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { Activate(); _surface.Focus(); Keyboard.Focus(_surface); });
    }

    private void ShowBar()
    {
        _floating.Visibility = Visibility.Visible;
        _hideBar.Stop();
        _hideBar.Start();
    }

    private void HideBarIfIdle()
    {
        // Never from under the mouse, an open list or a transfer going on. / Nunca bajo el ratón, con una lista abierta o con una transferencia en marcha.
        if (_floating.IsMouseOver || _quality.IsDropDownOpen || _displays.IsDropDownOpen || _transferring || _menuOpen) return;
        _hideBar.Stop();
        _floating.Visibility = Visibility.Collapsed;
        if (IsActive) _surface.Focus();
    }

    private sealed record DisplayItem(int Number, string Text) { public override string ToString() => Text; }

    private static void Id(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    /// <summary>The icons of the bar, from the font Windows brings. / Los iconos de la barra, de la fuente que trae Windows.</summary>
    private static class Glyphs
    {
        public const string Device = "\uE7F4", Keyboard = "\uE765", Windows = "\uE782", Type = "\uE8D2", Up = "\uE74A", Down = "\uE74B", Monitors = "\uE7F4",
            Refresh = "\uE72C", FullScreen = "\uE740", BackToWindow = "\uE73F", Close = "\uE711", ChevronDown = "\uE70D";
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>A flat button of the bar: its icon, its text if it needs one, and what it does said in a tooltip and to screen readers.
    /// Un botón plano de la barra: su icono, su texto si lo necesita, y lo que hace dicho en una ayuda y a los lectores de pantalla.</summary>
    private static void Flat(Button b, string? glyph, string? text, string help)
    {
        var face = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) face.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        if (text is not null) face.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(glyph is null ? 0 : 7, 0, 0, 0) });
        b.Content = face;
        b.ToolTip = help;
        System.Windows.Automation.AutomationProperties.SetName(b, help);
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.Padding = new Thickness(9, 6, 9, 6);
        b.Margin = new Thickness(1, 0, 1, 0);
    }

    private static Border Gap() => new() { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), Background = Brushes.Gray, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A button that opens a list made at that moment. / Un botón que abre una lista hecha en ese momento.</summary>
    private Button MenuButton(string id, string glyph, string text, Func<IReadOnlyList<object>> items)
    {
        var b = new Button();
        Id(b, id);
        Flat(b, glyph, text, text);
        if (b.Content is StackPanel face) face.Children.Add(new TextBlock { Text = Glyphs.ChevronDown, FontFamily = IconFont, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) });
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var item in items()) menu.Items.Add(item);
            _menuOpen = true;
            menu.Closed += (_, _) => { _menuOpen = false; if (_full) ShowBar(); if (IsActive) _surface.Focus(); };
            menu.IsOpen = true;
        };
        return b;
    }

    private MenuItem KeyItem(string text, params (int Vk, bool Extended)[] keys)
    {
        var item = new MenuItem { Header = text };
        item.Click += async (_, _) =>
        {
            if (_c.Session is not { } s) return;
            foreach (var (vk, ext) in keys) await s.SendKeyAsync(vk, true, ext);
            foreach (var (vk, ext) in keys.Reverse()) await s.SendKeyAsync(vk, false, ext);
            if (IsActive) _surface.Focus();
        };
        return item;
    }

    private IReadOnlyList<object> KeysMenu()
    {
        const int Win = 0x5B, Ctrl = 0x11, Shift = 0x10, Alt = 0x12;
        var lockScreen = new MenuItem { Header = L.T("LockScreen") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(lockScreen, "LockScreenItem");
        lockScreen.Click += async (_, _) =>
        {
            // Windows keeps Win+L from injected keys, so it is asked for as the person there. / Windows no acepta Win+L de teclas inyectadas, así que se pide como quien está allí.
            try { await _c.Server.RunCommandsAsync(_c.Device.Device, "rundll32.exe user32.dll,LockWorkStation", CommandShell.Cmd, asUser: true); }
            catch (MeshClientException ex) { _notice.Text = UserError.From(ex, _c.ServerVersion).Message; }
        };
        var lockInput = new MenuItem { Header = L.T("LockInput"), IsCheckable = true, IsChecked = _inputLocked };
        System.Windows.Automation.AutomationProperties.SetAutomationId(lockInput, "LockInputItem");
        lockInput.Click += async (_, _) =>
        {
            _inputLocked = lockInput.IsChecked;
            if (_c.Session is { } s) await s.SetInputLockAsync(_inputLocked);
        };
        return new object[]
        {
            KeyItem(L.T("KeyRun"), (Win, true), (0x52, false)), KeyItem(L.T("KeyExplorer"), (Win, true), (0x45, false)), KeyItem(L.T("KeyShowDesktop"), (Win, true), (0x44, false)),
            KeyItem(L.T("KeyTaskManager"), (Ctrl, false), (Shift, false), (0x1B, false)), KeyItem(L.T("KeyAltTab"), (Alt, false), (0x09, false)), KeyItem(L.T("KeyAltF4"), (Alt, false), (0x73, false)),
            KeyItem(L.T("KeyPrintScreen"), (0x2C, true)), new Separator(), lockScreen, lockInput,
        };
    }

    private Button Action(string id, string? glyph, string? text, string help, Func<DesktopSession, Task> run)
    {
        var b = new Button();
        Flat(b, glyph, text, help);
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
        Flat(_split, Glyphs.Monitors, null, L.T(_c.IsSplit ? "JoinDisplays" : "SplitDisplays"));
    }

    // With the frame gone, the bar is also what says which device this is. / Sin marco, la barra es también lo que dice qué equipo es.
    private void ShowStatus() => _status.Text = _full ? $"{_c.Device.Name}  ·  {_c.Status}" : _c.Status;

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

    // Keys go out one after another through this queue, so a Ctrl+V that waits for the clipboard keeps its place.
    // Las teclas salen una tras otra por esta cola, así que un Ctrl+V que espera al portapapeles conserva su sitio.
    private readonly InOrder _keys = new();
    private Task _lastKey = Task.CompletedTask;

    private void Queue(Func<Task> send) => _lastKey = _keys.Run(async () =>
    {
        try { await send(); } catch (MeshClientException) { } // a closed tunnel is told by the status / un túnel cerrado lo dice el estado
    });

    private void OnKey(KeyEventArgs e, bool down)
    {
        if (_c.Session is not { } s) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        // Alt+F4 stays local: it closes this window, as everywhere in Windows. / Alt+F4 se queda en local: cierra esta ventana.
        if (key == System.Windows.Input.Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk > 255) return;
        e.Handled = true;
        if (down) _pressed.Add(vk); else _pressed.Remove(vk);
        var mods = Keyboard.Modifiers;
        var clip = _c.Clipboard;
        // Left and right Shift/Ctrl/Alt travel as the generic key, as the website sends them. / Como los manda la web.
        var extended = Extended.Contains(key);
        DesktopKeys.Send(Queue, key, mods, down, e.IsRepeat, () => s.SendKeyAsync(Generic(vk), down, extended),
            clip is null ? null : clip.BeforePasteAsync,
            clip is null ? null : async () => { await Task.Delay(500); await Dispatcher.InvokeAsync(clip.Left); });
    }

    private Task ReleaseAllAsync()
    {
        if (_c.Session is not { } s) return Task.CompletedTask;
        foreach (var vk in _pressed.ToList()) Queue(() => s.SendKeyAsync(Generic(vk), false));
        _pressed.Clear();
        return _lastKey;
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
