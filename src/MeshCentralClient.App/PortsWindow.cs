using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeshCentralClient.Core;
using Microsoft.Win32;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One running mapping, as the list shows it. / Una redirección en marcha, como la enseña la lista.</summary>
public sealed class MappingRow : INotifyPropertyChanged
{
    public MappingRow(PortMapping map, string kind, bool remembered)
    {
        Map = map;
        Kind = kind;
        Remembered = remembered;
    }

    /// <summary>Reads the counters again; the window does it once a second, not on every packet. / Vuelve a leer los contadores; la ventana lo hace una vez por segundo, no en cada paquete.</summary>
    public void Refresh() => Changed("");

    public PortMapping Map { get; }

    /// <summary>"" for a plain mapping, "RDP" for one that opened Remote Desktop. / "" para una normal, "RDP" si abrió Escritorio remoto.</summary>
    public string Kind { get; }

    public bool Remembered { get; }
    public string Device => Map.Target.DeviceName;
    public string Destination => Map.Target.Destination;
    public string Local => $"127.0.0.1:{Map.LocalPort}";
    public string Connections => L.F("MappingConnections", Map.ActiveConnections, Map.TotalConnections);
    public string Traffic => $"↑ {ReportFormat.Bytes(Map.BytesUp)}  ↓ {ReportFormat.Bytes(Map.BytesDown)}";
    public string State => Map.LastError is { } e ? UserError.From(e).Message : Remembered ? L.T("MappingRemembered") : Kind;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The port mappings of one signed-in session: started from the device menu or the Port mappings window, the remembered
/// ones again at sign-in, and all of them stopped at sign-out.
/// Las redirecciones de una sesión: se empiezan desde el menú del equipo o la ventana de redirecciones, las recordadas
/// otra vez al entrar, y todas se paran al salir.
/// </summary>
public sealed class RelayManager : IAsyncDisposable
{
    private readonly MeshServerSession _session;
    private readonly string _server;

    public RelayManager(MeshServerSession session)
    {
        _session = session;
        _server = session.Server.BaseUri.ToString();
    }

    public ObservableCollection<MappingRow> Rows { get; } = new();

    /// <summary>Remembered mappings that could not start again, with why. / Las recordadas que no pudieron volver a empezar, con el porqué.</summary>
    public List<(SavedMapping Saved, MeshClientException Why)> NotRestored { get; } = new();

    public MappingRow Start(PortTarget target, bool remember, string kind = "")
    {
        var map = PortMapping.Start(_session, target);
        if (remember) RelayStore.Shared.Remember(_server, target, map.LocalPort);
        var row = new MappingRow(map, kind, remember);
        Rows.Add(row);
        return row;
    }

    public async Task StopAsync(MappingRow row)
    {
        Rows.Remove(row);
        if (row.Remembered) RelayStore.Shared.Forget(_server, row.Map.Target);
        await row.Map.DisposeAsync();
    }

    /// <summary>Starts the remembered mappings of this server; the ones whose device is gone or whose port is busy are kept aside.
    /// Empieza las recordadas de este servidor; las de un equipo que ya no está o con el puerto ocupado se apartan.</summary>
    public void RestoreRemembered(IEnumerable<Device> devices)
    {
        var known = devices.ToDictionary(d => d.Id);
        foreach (var saved in RelayStore.Shared.MappingsOf(_server))
        {
            if (!known.TryGetValue(saved.NodeId, out var device) || !_session.CanRelayThrough(device))
            {
                NotRestored.Add((saved, new MeshClientException(MeshFailure.Rejected, "redireccion/redirigir-puerto/sin-permiso",
                    $"The device \"{saved.DeviceName}\" is not reachable now, or this account cannot relay through it.")));
                continue;
            }
            try { Rows.Add(new MappingRow(PortMapping.Start(_session, saved.Target with { DeviceName = device.Name }), "", true)); }
            catch (MeshClientException ex) { NotRestored.Add((saved, ex)); }
        }
    }

    /// <summary>
    /// Windows Remote Desktop (mstsc, part of Windows) to the device through a private port; the port closes when
    /// Remote Desktop does. / Escritorio remoto de Windows (mstsc, de Windows) al equipo por un puerto privado; el
    /// puerto se cierra al cerrar Escritorio remoto.
    /// </summary>
    public MappingRow OpenRemoteDesktop(Device device, string? host = null, int port = 3389)
    {
        var row = Start(new PortTarget(device.Id, device.Name, port, host), remember: false, kind: "RDP");
        Process? p;
        try { p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), $"/v:127.0.0.1:{row.Map.LocalPort}") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = StopAsync(row);
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/abrir-rdp/dependencia-caida", "Windows Remote Desktop (mstsc.exe) could not be started on this PC.", ex);
        }
        if (p is not null)
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(async () => { if (Rows.Contains(row)) await StopAsync(row); });
        }
        return row;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in Rows.ToList()) await r.Map.DisposeAsync();
        Rows.Clear();
    }
}

/// <summary>Port mappings: MeshCentral Router inside the app. / Redirecciones de puertos: MeshCentral Router dentro de la app.</summary>
public sealed class PortsWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly RelayManager _relays;
    private readonly Func<IReadOnlyList<Device>> _devices;
    private readonly ListView _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _remove, _browser, _rdp, _copy;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public PortsWindow(MeshServerSession session, RelayManager relays, Func<IReadOnlyList<Device>> devices)
    {
        _session = session;
        _relays = relays;
        _devices = devices;
        Title = L.T("PortsTitle");
        Width = 900; Height = 440; MinWidth = 620; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AP.SetAutomationId(this, "PortsWindow");
        AP.SetAutomationId(_list, "MappingList");
        AP.SetAutomationId(_status, "PortsStatus");

        var grid = new GridView();
        void Col(string key, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(key), Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) });
        Col("ColDevice", nameof(MappingRow.Device), 150);
        Col("ColDestination", nameof(MappingRow.Destination), 170);
        Col("ColLocal", nameof(MappingRow.Local), 130);
        Col("ColConnections", nameof(MappingRow.Connections), 110);
        Col("ColTraffic", nameof(MappingRow.Traffic), 150);
        Col("ColState", nameof(MappingRow.State), 220);
        _list.View = grid;
        _list.ItemsSource = relays.Rows;
        _list.SelectionMode = SelectionMode.Single;

        Button B(string id, string key, RoutedEventHandler click)
        {
            var b = new Button { Content = L.T(key), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            AP.SetAutomationId(b, id);
            b.Click += click;
            return b;
        }
        var add = B("AddMappingButton", "AddMapping", (_, _) => Add());
        _remove = B("RemoveMappingButton", "RemoveMapping", async (_, _) => { if (Selected is { } r) await _relays.StopAsync(r); });
        _browser = B("OpenMappingBrowserButton", "OpenMappingBrowser", (_, _) => OpenBrowser());
        _rdp = B("OpenMappingRdpButton", "OpenMappingRdp", (_, _) => OpenRdpOnSelected());
        _copy = B("CopyMappingButton", "CopyMapping", (_, _) => { if (Selected is { } r) { try { Clipboard.SetText(r.Local); } catch (Exception) { } } });
        _list.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), Children = { add, _remove, _browser, _rdp, _copy } };
        var help = new TextBlock { Text = L.T("PortsHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) };
        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(help, Dock.Top); DockPanel.SetDock(buttons, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(help); dock.Children.Add(buttons); dock.Children.Add(_status); dock.Children.Add(_list);
        Content = dock;

        if (relays.NotRestored.Count > 0)
            _status.Text = string.Join("\n", relays.NotRestored.Select(n => L.F("MappingNotRestored", n.Saved.Target.Destination, UserError.From(n.Why).Message)));
        // The traffic columns move while bytes pass; one refresh a second is enough to read them. / Una vez por segundo basta para leer el tráfico.
        _tick.Tick += (_, _) => { foreach (var r in _relays.Rows) r.Refresh(); };
        Loaded += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    private MappingRow? Selected => _list.SelectedItem as MappingRow;

    private void UpdateButtons()
    {
        var r = Selected;
        _remove.IsEnabled = _copy.IsEnabled = r is not null;
        _browser.IsEnabled = r is not null;
        _rdp.IsEnabled = r is not null && r.Map.Target.RemotePort == 3389;
    }

    private void Add()
    {
        var devices = _devices().Where(_session.CanRelayThrough).ToList();
        if (devices.Count == 0) { _status.Text = L.T("NoRelayDevices"); return; }
        if (PortForm.Ask(this, devices, null) is not { } asked) return;
        try
        {
            var row = _relays.Start(asked.Target, asked.Remember);
            _list.SelectedItem = row;
            _status.Text = L.F("MappingStarted", row.Local, row.Destination);
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
    }

    private void OpenBrowser()
    {
        if (Selected is not { } r) return;
        var scheme = r.Map.Target.RemotePort is 443 or 8443 or 4443 ? "https" : "http";
        try { Process.Start(new ProcessStartInfo($"{scheme}://127.0.0.1:{r.Map.LocalPort}/") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = ex.Message; }
    }

    private void OpenRdpOnSelected()
    {
        if (Selected is not { } r) return;
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), $"/v:{r.Local}") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = ex.Message; }
    }
}

/// <summary>What to map, and whether to remember it. / Qué redirigir, y si recordarlo.</summary>
public static class PortForm
{
    public sealed record Answer(PortTarget Target, bool Remember);

    private sealed record Service(string Name, int Port)
    {
        public override string ToString() => Port == 0 ? Name : $"{Name} ({Port})";
    }

    private sealed class DeviceItem(Device d)
    {
        public Device Device { get; } = d;
        public override string ToString() => Device.Name;
    }

    public static Answer? Ask(Window owner, IReadOnlyList<Device> devices, Device? preselected)
    {
        var deviceBox = new ComboBox { MinWidth = 300 };
        foreach (var d in devices) deviceBox.Items.Add(new DeviceItem(d));
        deviceBox.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Id == preselected?.Id));
        var service = new ComboBox { MinWidth = 300 };
        foreach (var s in new[] { new Service(L.T("ServiceOther"), 0), new("RDP", 3389), new("SSH", 22), new("HTTP", 80), new("HTTPS", 443), new("VNC", 5900), new("SMB", 445) }) service.Items.Add(s);
        service.SelectedIndex = 0;
        var host = new TextBox { MinWidth = 300 };
        var remote = new TextBox { MinWidth = 120 };
        var local = new TextBox { MinWidth = 120 };
        var remember = new CheckBox { Content = L.T("RememberMapping"), Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(deviceBox, "MappingDeviceBox"); AP.SetAutomationId(service, "MappingServiceBox"); AP.SetAutomationId(host, "MappingHostBox");
        AP.SetAutomationId(remote, "MappingRemotePortBox"); AP.SetAutomationId(local, "MappingLocalPortBox"); AP.SetAutomationId(remember, "MappingRemember"); AP.SetAutomationId(ok, "MappingOk");
        service.SelectionChanged += (_, _) => { if (service.SelectedItem is Service { Port: > 0 } s) remote.Text = s.Port.ToString(); };

        static int? Port(string text, bool emptyIsZero) =>
            text.Trim().Length == 0 ? (emptyIsZero ? 0 : null) : int.TryParse(text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : null;
        void Validate() => ok.IsEnabled = deviceBox.SelectedItem is DeviceItem && Port(remote.Text, false) is not null && Port(local.Text, true) is not null;
        remote.TextChanged += (_, _) => Validate(); local.TextChanged += (_, _) => Validate(); deviceBox.SelectionChanged += (_, _) => Validate();

        static TextBlock Label(string key) => new() { Text = L.T(key), Margin = new Thickness(0, 10, 0, 2) };
        var w = new Window
        {
            Title = L.T("AddMappingTitle"), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    Label("ColDevice"), deviceBox,
                    Label("MappingService"), service,
                    Label("MappingHost"), host,
                    new TextBlock { Text = L.T("MappingHostHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                    Label("MappingRemotePort"), remote,
                    Label("MappingLocalPort"), local,
                    new TextBlock { Text = L.T("MappingLocalHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                    remember,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "MappingForm");
        Answer? result = null;
        ok.Click += (_, _) =>
        {
            var d = ((DeviceItem)deviceBox.SelectedItem).Device;
            result = new Answer(new PortTarget(d.Id, d.Name, Port(remote.Text, false)!.Value, host.Text.Trim() is { Length: > 0 } h ? h : null, Port(local.Text, true)!.Value), remember.IsChecked == true);
            w.Close();
        };
        w.Loaded += (_, _) => remote.Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>How to sign in to SSH through a device. / Cómo entrar por SSH a través de un equipo.</summary>
public static class SshForm
{
    public sealed record Answer(string? Host, int Port, SshLogin Login);

    public static Answer? Ask(Window owner, string deviceName, SshProfile? last)
    {
        var host = new TextBox { MinWidth = 300, Text = last?.Host ?? "" };
        var port = new TextBox { MinWidth = 120, Text = (last?.Port ?? 22).ToString() };
        var user = new TextBox { MinWidth = 300, Text = last?.User ?? "" };
        var password = new PasswordBox { MinWidth = 300 };
        var key = new TextBox { MinWidth = 220, Text = last?.KeyPath ?? "" };
        var browse = new Button { Content = L.T("Browse"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        var phrase = new PasswordBox { MinWidth = 300 };
        var ok = new Button { Content = L.T("ConnectSsh"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(host, "SshHostBox"); AP.SetAutomationId(port, "SshPortBox"); AP.SetAutomationId(user, "SshUserBox"); AP.SetAutomationId(password, "SshPasswordBox");
        AP.SetAutomationId(key, "SshKeyBox"); AP.SetAutomationId(phrase, "SshPassphraseBox"); AP.SetAutomationId(ok, "SshOk");
        int? Port() => int.TryParse(port.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : null;
        void Validate() => ok.IsEnabled = user.Text.Trim().Length > 0 && Port() is not null && (password.Password.Length > 0 || key.Text.Trim().Length > 0);
        user.TextChanged += (_, _) => Validate(); port.TextChanged += (_, _) => Validate(); key.TextChanged += (_, _) => Validate(); password.PasswordChanged += (_, _) => Validate();
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = L.T("SshKey"), CheckFileExists = true, InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
            if (dlg.ShowDialog(owner) == true) key.Text = dlg.FileName;
        };
        static TextBlock Label(string key) => new() { Text = L.T(key), Margin = new Thickness(0, 10, 0, 2) };
        var w = new Window
        {
            Title = L.F("SshTitle", deviceName), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    Label("MappingHost"), host,
                    new TextBlock { Text = L.T("SshHostHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                    Label("MappingRemotePort"), port,
                    Label("SshUser"), user,
                    Label("Password"), password,
                    Label("SshKey"), new DockPanel { Children = { browse, key } },
                    Label("SshPassphrase"), phrase,
                    new TextBlock { Text = L.T("SshKeyHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        DockPanel.SetDock(browse, Dock.Right);
        AP.SetAutomationId(w, "SshForm");
        Answer? result = null;
        ok.Click += (_, _) =>
        {
            result = new Answer(host.Text.Trim() is { Length: > 0 } h ? h : null, Port()!.Value,
                new SshLogin(user.Text.Trim(), password.Password.Length > 0 ? password.Password : null, key.Text.Trim() is { Length: > 0 } k ? k : null, phrase.Password.Length > 0 ? phrase.Password : null));
            w.Close();
        };
        w.Loaded += (_, _) => { if (user.Text.Length > 0) password.Focus(); else user.Focus(); Validate(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>
    /// The SSH server's identity checked against the trusted ones, asking the person when it is new or changed. Called by
    /// the SSH client on its own thread, so it waits for the answer on the window's thread.
    /// La identidad del servidor SSH contra las de confianza, preguntando a la persona si es nueva o cambió. La llama el
    /// cliente SSH en su hilo, así que espera la respuesta en el hilo de la ventana.
    /// </summary>
    public static Func<SshHostKey, bool> Trust(Window owner, string server, Device device, string? host, int port) => key =>
    {
        var store = RelayStore.Shared;
        var verdict = store.Check(server, device.Id, host, port, key);
        if (verdict == HostKeyVerdict.Known) return true;
        var where = $"{(string.IsNullOrWhiteSpace(host) ? device.Name : host)}:{port}";
        var yes = owner.Dispatcher.Invoke(() => MessageBox.Show(owner,
            verdict == HostKeyVerdict.New ? L.F("SshNewHostKey", where, key.Algorithm, key.Fingerprint) : L.F("SshChangedHostKey", where, key.Algorithm, key.Fingerprint),
            L.T("SshIdentityTitle"), MessageBoxButton.YesNo, verdict == HostKeyVerdict.New ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (yes) store.Trust(server, device.Id, host, port, key);
        return yes;
    };
}
