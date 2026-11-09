using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>
/// The page of one device: general data (description and tags editable), network interfaces, the whole inventory as the
/// agent sends it (its shape changes with the OS, so it is shown as a tree, with a filter), notes and history.
/// La ficha de un equipo: datos generales (descripción y etiquetas editables), interfaces de red, el inventario entero
/// como lo manda el agente (su forma cambia con el sistema, así que va en árbol, con filtro), notas e historial.
/// </summary>
public sealed class DeviceWindow : Window
{
    private readonly MeshServerSession _session;
    private Device _device;
    private readonly string _groupName;
    private readonly Grid _general = new() { Margin = new Thickness(16) };
    private readonly TextBox _desc = new() { AcceptsReturn = false };
    private readonly TextBox _tags = new();
    private readonly TextBlock _generalNotice = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ListView _network = new();
    private readonly TreeView _inventory = new();
    private readonly TextBox _inventoryFilter = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBlock _notesNotice = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ListView _events = new();
    private SystemInfo? _info;

    public DeviceWindow(MeshServerSession session, DeviceRow row)
    {
        _session = session;
        _device = row.Device;
        _groupName = row.GroupName;
        Title = $"{L.T("DeviceDetails")} — {row.Name}";
        Width = 860; Height = 640; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Id(this, "DeviceWindow"); Id(_desc, "DescBox"); Id(_tags, "TagsBox"); Id(_generalNotice, "GeneralNotice"); Id(_network, "NetworkList");
        Id(_inventory, "InventoryTree"); Id(_inventoryFilter, "InventoryFilter"); Id(_notes, "NotesBox"); Id(_notesNotice, "NotesNotice"); Id(_events, "EventsList");

        var tabs = new TabControl { Margin = new Thickness(8) };
        Id(tabs, "DeviceTabs");
        tabs.Items.Add(Tab("TabGeneral", L.T("TabGeneral"), new ScrollViewer { Content = _general, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        tabs.Items.Add(Tab("TabNetwork", L.T("TabNetwork"), _network));
        tabs.Items.Add(Tab("TabInventory", L.T("TabInventory"), Docked(_inventoryFilter, _inventory)));
        var saveNotes = Button("SaveNotesButton", L.T("Save"), async () => await SaveNotesAsync());
        tabs.Items.Add(Tab("TabNotes", L.T("TabNotes"), Docked(new StackPanel { Children = { _notesNotice, saveNotes } }, _notes, Dock.Bottom)));
        tabs.Items.Add(Tab("TabHistory", L.T("TabHistory"), _events));

        var refresh = Button("RefreshDetails", L.T("Refresh"), async () => await LoadAsync());
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        refresh.Margin = new Thickness(0, 8, 16, 0);
        var root = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Top);
        root.Children.Add(refresh);
        root.Children.Add(tabs);
        Content = root;

        SetupLists();
        _inventoryFilter.TextChanged += (_, _) => FillInventory();
        System.Windows.Automation.AutomationProperties.SetName(_inventoryFilter, L.T("Search"));
        Loaded += async (_, _) => await LoadAsync();
    }

    private static void Id(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    private static TabItem Tab(string id, string header, UIElement content)
    {
        var t = new TabItem { Header = header, Content = content };
        Id(t, id);
        return t;
    }

    private static DockPanel Docked(UIElement edge, UIElement fill, Dock dock = Dock.Top)
    {
        var p = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(edge, dock);
        p.Children.Add(edge);
        p.Children.Add(fill);
        return p;
    }

    private static Button Button(string id, string text, Func<Task> run)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 100 };
        Id(b, id);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await run(); } finally { b.IsEnabled = true; } };
        return b;
    }

    private void SetupLists()
    {
        var net = new GridView();
        net.Columns.Add(Col(L.T("ColInterface"), "Name", 130));
        net.Columns.Add(Col("MAC", "Mac", 150));
        net.Columns.Add(Col(L.T("ColAddresses"), "Addresses", 300));
        net.Columns.Add(Col(L.T("ColGateway"), "Gateway", 120));
        net.Columns.Add(Col(L.T("ColSpeed"), "Speed", 90));
        _network.View = net;
        var ev = new GridView();
        ev.Columns.Add(Col(L.T("ColWhen"), "When", 150));
        ev.Columns.Add(Col(L.T("ColWho"), "Who", 120));
        ev.Columns.Add(Col(L.T("ColWhat"), "What", 520));
        _events.View = ev;
    }

    private static GridViewColumn Col(string header, string path, double width) => new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    private sealed record NicRow(string Name, string Mac, string Addresses, string Gateway, string Speed) { public override string ToString() => Name; }
    private sealed record EventRow(string When, string Who, string What) { public override string ToString() => What; }

    // ------------------------------------------------------------------ loading / carga

    private async Task LoadAsync()
    {
        // Everything at once; a part that fails says so in its place and does not stop the others.
        // Todo a la vez; una parte que falla lo dice en su sitio y no para a las demás.
        var device = Try(async () => (await _session.GetDevicesAsync()).FirstOrDefault(d => d.Id == _device.Id));
        var last = Try(() => _session.GetLastConnectAsync(_device.Id));
        var nics = Try(() => _session.GetNetworkInfoAsync(_device.Id));
        var info = Try(() => _session.GetSystemInfoAsync(_device.Id));
        var events = Try(() => _session.GetEventsAsync(_device.Id, 300));
        var notes = Notes();
        await Task.WhenAll(device, last, nics, info, events, notes);

        if (device.Result.Value is { } d) _device = d;
        FillGeneral(last.Result.Value, info.Result.Value);
        FillNetwork(nics.Result);
        _info = info.Result.Value;
        if (info.Result.Error is { } ie) ShowTreeMessage(UserError.From(ie, _session.ServerInfo.Version).Message);
        else FillInventory();
        FillEvents(events.Result);
        var (text, error) = notes.Result;
        _notes.Text = text ?? "";
        _notes.IsReadOnly = error is not null;
        _notesNotice.Text = error ?? "";
    }

    private static async Task<(T? Value, MeshClientException? Error)> Try<T>(Func<Task<T>> f)
    {
        try { return (await f(), null); }
        catch (MeshClientException ex) { return (default, ex); }
    }

    private async Task<(string? Text, string? Error)> Notes()
    {
        // Without access the server does not answer getNotes at all: a short wait, then "not available".
        // Sin acceso el servidor no contesta a getNotes: una espera corta y luego «no disponible».
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return (await _session.GetNotesAsync(_device.Id, cts.Token), null); }
        catch (OperationCanceledException) { return (null, L.T("NotesUnavailable")); }
        catch (MeshClientException ex) { return (null, UserError.From(ex).Message); }
    }

    private void FillGeneral((DateTimeOffset? Time, string? Address) last, SystemInfo? info)
    {
        _general.Children.Clear();
        _general.RowDefinitions.Clear();
        _general.ColumnDefinitions.Clear();
        _general.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        _general.ColumnDefinitions.Add(new ColumnDefinition());
        string Date(DateTimeOffset? t) => t?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
        var hw = info is null ? null : HardwareSummary.From(info.Hardware);
        TextBox Hw(string id, string? value) { var box = Text(value is { Length: > 0 } ? value : "—"); Id(box, id); return box; }
        var memory = Hw("DeviceMemory", hw is null ? null : HardwareText.Memory(hw));
        var disks = Hw("DeviceDisks", hw is null ? null : HardwareText.Disks(hw));
        if (hw is not null && hw.MissingLinuxParts(info!.Hardware) && _session.AbilitiesOn(_device).RunCommands) _ = AskLinuxAsync(hw, memory, disks);
        var rows = new List<(string Label, UIElement Value)>
        {
            (L.T("ColName"), Text(_device.Name)),
            (L.T("Group"), Text(_groupName)),
            (L.T("ColOs"), Text(_device.OsDescription ?? "—")),
            (L.T("State"), Text(L.T(_device.IsOnline ? "Online" : "Offline"))),
            (L.T("ColAddress"), Text(_device.Address ?? "—")),
            (L.T("LastConnection"), Text(last.Time is null ? "—" : $"{Date(last.Time)}  ·  {last.Address}")),
            (L.T("LastBoot"), Text(Date(_device.LastBoot))),
            (L.T("Agent"), Text(_device.AgentCore ?? "—")),
            (L.T("Model"), Hw("DeviceModel", hw?.Model)),
            (L.T("SerialNumber"), Hw("DeviceSerial", hw?.Serial)),
            ("CPU", Text(info?.Cpu ?? "—")),
            (L.T("Memory"), memory),
            (L.T("Disks"), disks),
            (L.T("Volumes"), Hw("DeviceVolumes", hw is null ? null : HardwareText.Volumes(hw))),
            (L.T("Graphics"), Hw("DeviceGraphics", hw is null ? null : string.Join("\n", hw.Graphics))),
            ("BIOS", Text(string.Join(" ", new[] { info?.Find("bios_vendor"), info?.Find("bios_version"), info?.Find("bios_date") }.Where(x => x is not null)) is { Length: > 0 } b ? b : "—")),
            (L.T("Board"), Text(string.Join(" ", new[] { info?.Find("board_vendor"), info?.Find("board_name") }.Where(x => x is not null)) is { Length: > 0 } m ? m : "—")),
            (L.T("Description"), _desc),
            (L.T("ColTags"), _tags),
        };
        _desc.Text = _device.Description ?? "";
        _tags.Text = string.Join(", ", _device.Tags);
        for (var i = 0; i < rows.Count; i++)
        {
            _general.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Opacity = 0.7, Margin = new Thickness(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, i);
            Grid.SetRow(rows[i].Value, i);
            Grid.SetColumn(rows[i].Value, 1);
            if (rows[i].Value is FrameworkElement fe && fe.Parent is Panel old) old.Children.Remove(fe);
            _general.Children.Add(label);
            _general.Children.Add(rows[i].Value);
        }
        var save = Button("SaveDeviceButton", L.T("Save"), async () => await SaveGeneralAsync());
        // Out of the previous panel first: WPF does not let an element have two parents.
        // Primero fuera del panel anterior: WPF no deja que un elemento tenga dos padres.
        if (_generalNotice.Parent is Panel p) p.Children.Remove(_generalNotice);
        var panel = new StackPanel();
        panel.Children.Add(save);
        panel.Children.Add(_generalNotice);
        _general.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(panel, rows.Count);
        Grid.SetColumn(panel, 1);
        _general.Children.Add(panel);
    }

    /// <summary>A Linux device without lshw or dmidecode: it is asked once the page is showing. / Un equipo Linux sin lshw ni dmidecode: se le pregunta con la página ya a la vista.</summary>
    private async Task AskLinuxAsync(HardwareSummary hw, TextBox memory, TextBox disks)
    {
        try
        {
            var answer = await _session.RunCommandsAsync(_device, HardwareSummary.LinuxProbe, MeshServerSession.ShellFor(_device, false), timeout: TimeSpan.FromSeconds(30));
            var full = hw.WithLinuxProbe(answer);
            if (HardwareText.Memory(full) is { Length: > 0 } m) memory.Text = m;
            if (HardwareText.Disks(full) is { Length: > 0 } d) disks.Text = d;
        }
        catch (MeshClientException) { /* it stays as the agent said / se queda como lo dijo el agente */ }
    }

    private static TextBox Text(string value) =>
        new() { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };

    private void FillNetwork((IReadOnlyList<NetworkInterface>? Value, MeshClientException? Error) r)
    {
        if (r.Error is { } e) { _network.ItemsSource = new[] { new NicRow(UserError.From(e).Message, "", "", "", "") }; return; }
        _network.ItemsSource = (r.Value ?? Array.Empty<NetworkInterface>())
            .OrderBy(n => n.Addresses.Count == 0).ThenBy(n => n.Name.StartsWith("lo", StringComparison.OrdinalIgnoreCase)).ThenBy(n => n.Name)
            .Select(n => new NicRow(n.Name, n.Mac ?? "", string.Join("  ", n.Addresses.Select(a => a.Netmask is null ? a.Address : $"{a.Address}/{a.Netmask}")),
                string.Join(" ", n.Addresses.Select(a => a.Gateway).Where(g => g is not null).Distinct()), n.Speed is long s ? Speed(s) : ""))
            .ToList();
    }

    private static string Speed(long bps) => bps >= 1_000_000_000 ? $"{bps / 1_000_000_000.0:0.#} Gb/s" : bps >= 1_000_000 ? $"{bps / 1_000_000.0:0} Mb/s" : $"{bps} b/s";

    private void ShowTreeMessage(string text)
    {
        _inventory.Items.Clear();
        _inventory.Items.Add(new TreeViewItem { Header = text });
    }

    private void FillInventory()
    {
        if (_info is null) { ShowTreeMessage(L.T("NoInventory")); return; }
        var filter = _inventoryFilter.Text.Trim();
        _inventory.Items.Clear();
        foreach (var item in Node(_info.Hardware, filter)) _inventory.Items.Add(item);
        if (_inventory.Items.Count == 0) ShowTreeMessage(L.T("NoMatches"));
    }

    /// <summary>One tree item per key; with a filter, only branches with a match, opened. / Un elemento por clave; con filtro, solo las ramas que casan, abiertas.</summary>
    private static IEnumerable<TreeViewItem> Node(JsonNode? node, string filter)
    {
        IEnumerable<(string Key, JsonNode? Value)> children = node switch
        {
            JsonObject o => o.Select(kv => (kv.Key, kv.Value)),
            JsonArray a => a.Select((v, i) => ($"[{i + 1}]", v)),
            _ => Array.Empty<(string, JsonNode?)>(),
        };
        foreach (var (key, value) in children)
        {
            if (value is JsonValue leaf)
            {
                var text = $"{key}: {leaf}";
                if (filter.Length == 0 || text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)) yield return new TreeViewItem { Header = text };
            }
            else
            {
                var inner = Node(value, filter.Length > 0 && key.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ? "" : filter).ToList();
                if (inner.Count == 0) continue;
                var item = new TreeViewItem { Header = key, IsExpanded = filter.Length > 0 };
                foreach (var i in inner) item.Items.Add(i);
                yield return item;
            }
        }
    }

    private void FillEvents((IReadOnlyList<DeviceEvent>? Value, MeshClientException? Error) r)
    {
        if (r.Error is { } e) { _events.ItemsSource = new[] { new EventRow("", "", UserError.From(e).Message) }; return; }
        _events.ItemsSource = (r.Value ?? Array.Empty<DeviceEvent>()).OrderByDescending(e => e.Time)
            .Select(e => new EventRow(e.Time?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "", e.User ?? "", e.Message)).ToList();
    }

    // ------------------------------------------------------------------ saving / guardar

    private async Task SaveGeneralAsync()
    {
        _generalNotice.Text = "";
        var tags = _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        try
        {
            await _session.ChangeDeviceAsync(_device.Id, description: _desc.Text.Trim(), tags: tags);
            await LoadAsync();
            _generalNotice.Text = L.T("Saved");
        }
        catch (MeshClientException ex) { var e = UserError.From(ex); _generalNotice.Text = $"{e.Message}  {e.Footer}"; }
    }

    private async Task SaveNotesAsync()
    {
        var text = _notes.Text;
        _notesNotice.Text = "";
        try
        {
            await _session.SetNotesAsync(_device.Id, text);
            // The server does not answer setNotes: read them back. / El servidor no contesta a setNotes: se releen.
            for (var i = 0; i < 15; i++)
            {
                var (back, _) = await Notes();
                if ((back ?? "") == text.Trim() || (back ?? "") == text) { _notesNotice.Text = L.T("Saved"); return; }
                await Task.Delay(200);
            }
            _notesNotice.Text = $"{L.T("NotesNotSaved")}  {Burburito.Footer("ficha/cambiar-ajustes/rechazo-remoto", L.Spanish)}";
        }
        catch (MeshClientException ex) { var e = UserError.From(ex); _notesNotice.Text = $"{e.Message}  {e.Footer}"; }
    }
}
