using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>Names for the ids the server sends (accounts, devices, device groups). / Nombres para los ids que manda el servidor.</summary>
public sealed class Names(MeshServerSession session, Func<string, string?> deviceName, IReadOnlyDictionary<string, string>? users = null)
{
    public IReadOnlyDictionary<string, string> Users { get; set; } = users ?? new Dictionary<string, string>();

    public string User(string? id) => id is null ? "" : Users.GetValueOrDefault(id) ?? (id == session.User.Id ? session.User.Name : id.Split('/')[^1]);
    public string Device(string? id) => id is null ? "" : deviceName(id) ?? id.Split('/')[^1];
    public string Group(string? id) => id is null ? "" : session.GroupById(id)?.Name ?? id.Split('/')[^1];
}

/// <summary>How each kind of report value reads, as the website shows it. / Cómo se lee cada tipo de valor de un informe, como en la web.</summary>
public static class ReportFormat
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["time"] = "RepTime", ["userid"] = "RepUser", ["nodeid"] = "RepDevice", ["meshid"] = "RepDevGroup", ["guestname"] = "RepGuest", ["protocol"] = "RepProtocol",
        ["length"] = "RepLength", ["bytesin"] = "RepBytesIn", ["bytesout"] = "RepBytesOut", ["ip"] = "RepIp", ["browser"] = "RepBrowser", ["os"] = "RepOs",
        ["twofactor"] = "RepTwoFactor", ["msg"] = "RepMsg", ["record"] = "RepRecord", ["recordcount"] = "RepCount",
    };

    public static string Title(ReportColumn c) => Titles.TryGetValue(c.Id, out var key) ? L.T(key) : c.Title;

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N1} KB", b / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", b / 1048576.0),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N2} GB", b / 1073741824.0),
    };

    public static string Seconds(long s) => s < 3600 ? $"{s / 60}:{s % 60:D2}" : $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}";

    public static string Protocol(long p) => p switch
    {
        1 or 6 or 7 or 8 or 9 => L.T("ProtoTerminal"), 2 => L.T("ProtoDesktop"), 5 => L.T("ProtoFiles"),
        >= 100 and < 200 => L.T("ProtoAmt"), _ => L.F("ProtoOther", p),
    };

    public static string Login(long msgId) => msgId switch
    {
        107 => L.T("LoginOk"), 108 => L.T("LoginBadCode"), 109 => L.T("LoginLocked"), 110 => L.T("LoginBadPassword"), _ => msgId.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>One value as text. / Un valor como texto.</summary>
    public static string Value(ReportColumn c, JsonNode? v, Names names)
    {
        if (v is null) return "";
        long? n = v is JsonValue jv && jv.TryGetValue<double>(out var d) ? (long)d : null;
        return (c.Format, c.Id) switch
        {
            ("datetime", _) when n is long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            ("time", _) when n is long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            ("user", _) => names.User(v.ToString()),
            ("node", _) => names.Device(v.ToString()),
            ("mesh", _) => names.Group(v.ToString()),
            ("bytes", _) when n is long b => Bytes(b),
            ("seconds", _) when n is long s => Seconds(s),
            ("msg", _) when n is long m => Login(m),
            (_, "protocol") when n is long p => Protocol(p),
            _ => v.ToString(),
        };
    }

    /// <summary>A group's header: an account, a device (with its group), or a day. / La cabecera de un grupo.</summary>
    public static string GroupName(Report r, string key, Names names) => r.GroupFormat switch
    {
        "user" => names.User(key),
        // "node/…/…" alone, or followed by "/mesh/…/…" (meshuser.js: nodeid + '/' + meshid). / Solo el equipo, o seguido de su grupo.
        "nodemesh" => key.IndexOf("/mesh/", StringComparison.Ordinal) is var i and > 0
            ? $"{names.Device(key[..i])} ({names.Group(key[(i + 1)..])})" : names.Device(key),
        _ => r.Groups.Count == 1 && key == "0" ? "" : key,
    };

    /// <summary>A CSV field (RFC 4180). / Un campo de CSV.</summary>
    public static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Writes rows as a CSV file the person chooses. / Escribe filas en un CSV que elige la persona.</summary>
internal static class CsvExport
{
    public static string? Save(Window owner, string fileName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog(owner) != true) return null;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header.Select(ReportFormat.Csv)));
        foreach (var r in rows) sb.AppendLine(string.Join(',', r.Select(ReportFormat.Csv)));
        // With a BOM, so Excel reads accents right. / Con BOM, para que Excel lea bien los acentos.
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        return dialog.FileName;
    }
}

/// <summary>The server's event log, or one account's, with new events arriving live. / El registro de sucesos, o el de una cuenta, en vivo.</summary>
public sealed class EventsWindow : Window
{
    public sealed class EventRow(ServerEvent e, Names n)
    {
        public ServerEvent Event { get; } = e;
        public string Time => Event.Time?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
        public string User => Event.UserName ?? n.User(Event.UserId);
        public string Where => Event.NodeId is not null ? n.Device(Event.NodeId) : n.Group(Event.MeshId);
        public string Message => Event.Message;
        public override string ToString() => Message;
    }

    private readonly MeshServerSession _session;
    private readonly UserInfo? _user;
    private readonly Names _names;
    private readonly List<EventRow> _all = [];
    private readonly ListView _list = new();
    private readonly TextBox _filter = new() { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _export = new() { Content = L.T("ExportCsv"), MinWidth = 110 };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 10), Opacity = 0.85 };

    public EventsWindow(MeshServerSession session, Names names, UserInfo? user = null)
    {
        _session = session; _names = names; _user = user;
        Title = user is null ? L.F("EventsTitle", string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name) : L.F("UserEventsTitle", user.Name);
        Width = Math.Min(1000, SystemParameters.WorkArea.Width); Height = Math.Min(640, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[] { (this, "EventsWindow"), (_list, "EventList"), (_filter, "EventFilter"), (_export, "ExportEventsButton"), (_status, "EventsStatus") })
            AP.SetAutomationId(e, id);
        AP.SetName(_filter, L.T("FilterEvents"));
        var grid = new GridView();
        foreach (var (key, path, width) in new[] { ("ColTime", nameof(EventRow.Time), 140.0), ("ColUser", nameof(EventRow.User), 120.0), ("ColWhere", nameof(EventRow.Where), 170.0), ("ColMessage", nameof(EventRow.Message), 520.0) })
            grid.Columns.Add(new GridViewColumn { Header = L.T(key), Width = width, DisplayMemberBinding = new Binding(path) });
        _list.View = grid;
        var top = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
        DockPanel.SetDock(_export, Dock.Right);
        top.Children.Add(_export);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = L.T("FilterEvents"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _filter } });
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(_status);
        _list.Margin = new Thickness(12, 0, 12, 0);
        root.Children.Add(_list);
        Content = root;
        _filter.TextChanged += (_, _) => ShowRows();
        _export.Click += (_, _) => Export();
        // New events at the top, live; for one account, only that account's. / Los nuevos arriba, en vivo; de una cuenta, solo los suyos.
        EventHandler<ServerEvent> live = (_, e) =>
        {
            if (_user is not null && e.UserId != _user.Id) return;
            Dispatcher.BeginInvoke(() => { _all.Insert(0, new EventRow(e, _names)); ShowRows(); });
        };
        session.EventReceived += live;
        Closed += (_, _) => session.EventReceived -= live;
        Loaded += async (_, _) =>
        {
            try
            {
                var events = _user is null ? await _session.GetServerEventsAsync(1000) : await _session.GetUserEventsAsync(_user, 1000);
                _all.AddRange(events.Select(e => new EventRow(e, _names)));
                ShowRows();
            }
            catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        };
    }

    private IEnumerable<EventRow> Visible()
    {
        var f = _filter.Text.Trim();
        return f.Length == 0 ? _all : _all.Where(r => $"{r.Time} {r.User} {r.Where} {r.Message}".Contains(f, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ShowRows()
    {
        _list.Items.Clear();
        foreach (var r in Visible()) _list.Items.Add(r);
        _status.Text = L.F("EventsCount", _list.Items.Count);
    }

    private void Export()
    {
        try
        {
            if (CsvExport.Save(this, "events.csv", [L.T("ColTime"), L.T("ColUser"), L.T("ColWhere"), L.T("ColMessage")],
                    Visible().Select(r => (IReadOnlyList<string>)[r.Time, r.User, r.Where, r.Message])) is { } path)
                _status.Text = L.F("Exported", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorWindow.Show(this, UserError.Unexpected(ex)); }
    }
}

/// <summary>The website's reports, between two dates, with CSV export. / Los informes de la web, entre dos fechas, con exportación a CSV.</summary>
public sealed class ReportsWindow : Window
{
    private static readonly (ReportKind Kind, string Key)[] Kinds =
        { (ReportKind.RemoteSessions, "ReportSessions"), (ReportKind.Traffic, "ReportTraffic"), (ReportKind.Logins, "ReportLogins"), (ReportKind.Database, "ReportDatabase") };
    private static readonly (ReportGroupBy By, string Key)[] Groupings = { (ReportGroupBy.User, "GroupByUser"), (ReportGroupBy.Device, "GroupByDevice"), (ReportGroupBy.Day, "GroupByDay") };

    private readonly MeshServerSession _session;
    private readonly Names _names;
    private readonly ComboBox _kind = new() { MinWidth = 200, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _by = new() { MinWidth = 120, Margin = new Thickness(0, 0, 12, 0) };
    private readonly DatePicker _from = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly DatePicker _to = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _group = new() { MinWidth = 180, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _traffic = new() { Content = L.T("ShowTraffic"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _run = new() { Content = L.T("RunReport"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _export = new() { Content = L.T("ExportCsv"), MinWidth = 110, IsEnabled = false };
    private readonly TextBlock _note = new() { Text = L.T("ReportLoginsNote"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) };
    private readonly ListView _list = new() { Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 10), Opacity = 0.85 };
    private List<string> _header = [];
    private List<List<string>> _rows = [];

    public ReportsWindow(MeshServerSession session, Names names)
    {
        _session = session; _names = names;
        Title = L.F("ReportsTitle", string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name);
        Width = Math.Min(1040, SystemParameters.WorkArea.Width); Height = Math.Min(660, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "ReportsWindow"), (_kind, "ReportKind"), (_by, "ReportGroupBy"), (_from, "ReportFrom"), (_to, "ReportTo"), (_group, "ReportDeviceGroup"),
            (_traffic, "ShowTrafficBox"), (_run, "RunReportButton"), (_export, "ExportReportButton"), (_list, "ReportList"), (_status, "ReportStatus"), (_note, "ReportNote"),
        }) AP.SetAutomationId(e, id);
        foreach (var k in Kinds.Where(k => k.Kind != ReportKind.Database || session.User.IsFullAdministrator)) _kind.Items.Add(L.T(k.Key));
        _kind.SelectedIndex = 0;
        _from.SelectedDate = DateTime.Today.AddDays(-7);
        _to.SelectedDate = DateTime.Today;
        _group.Items.Add(new ComboBoxItem { Content = L.T("AllDeviceGroups") });
        foreach (var g in session.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)) _group.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Id });
        _group.SelectedIndex = 0;

        static StackPanel Labeled(string key, Control c) => new() { Children = { new TextBlock { Text = L.T(key), Opacity = 0.8 }, c } };
        var bar = new WrapPanel { Margin = new Thickness(12, 12, 12, 8) };
        bar.Children.Add(Labeled("ReportKind", _kind));
        bar.Children.Add(Labeled("ReportGroupBy", _by));
        bar.Children.Add(Labeled("ReportFrom", _from));
        bar.Children.Add(Labeled("ReportTo", _to));
        bar.Children.Add(Labeled("ReportDeviceGroup", _group));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 14, 0, 0), Children = { _traffic, _run, _export } };
        bar.Children.Add(buttons);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(_note, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(_note); root.Children.Add(_status); root.Children.Add(_list);
        Content = root;

        _kind.SelectionChanged += (_, _) => Gate();
        _run.Click += async (_, _) => await Run();
        _export.Click += (_, _) => Export();
        Gate();
    }

    private ReportKind Kind => Kinds.Where(k => k.Kind != ReportKind.Database || _session.User.IsFullAdministrator).ElementAt(Math.Max(0, _kind.SelectedIndex)).Kind;

    /// <summary>Only what the server accepts for each report. / Solo lo que el servidor acepta en cada informe.</summary>
    private void Gate()
    {
        var keep = _by.SelectedIndex >= 0 ? Groupings[Math.Min(_by.SelectedIndex, Groupings.Length - 1)].By : ReportGroupBy.User;
        _by.Items.Clear();
        var allowed = Kind switch
        {
            ReportKind.RemoteSessions => Groupings,
            ReportKind.Logins => Groupings.Where(g => g.By != ReportGroupBy.Device).ToArray(),
            _ => [],
        };
        foreach (var g in allowed) _by.Items.Add(new ComboBoxItem { Content = L.T(g.Key), Tag = g.By });
        _by.SelectedItem = _by.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ReportGroupBy)i.Tag == keep) ?? _by.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _by.IsEnabled = allowed.Length > 0;
        _traffic.Visibility = Kind == ReportKind.RemoteSessions ? Visibility.Visible : Visibility.Collapsed;
        _group.IsEnabled = Kind is ReportKind.RemoteSessions or ReportKind.Traffic;
        _note.Visibility = Kind == ReportKind.Logins ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task Run()
    {
        IsEnabled = false;
        try
        {
            var by = (_by.SelectedItem as ComboBoxItem)?.Tag is ReportGroupBy b ? b : ReportGroupBy.User;
            var from = new DateTimeOffset((_from.SelectedDate ?? DateTime.Today.AddDays(-7)).Date);
            var to = new DateTimeOffset((_to.SelectedDate ?? DateTime.Today).Date.AddDays(1)); // the whole last day / el último día entero
            var group = _group.IsEnabled ? (_group.SelectedItem as ComboBoxItem)?.Tag as string : null;
            var report = await _session.GetReportAsync(Kind, by, from, to, group, Kind == ReportKind.RemoteSessions && _traffic.IsChecked == true);
            ShowReport(report);
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally { IsEnabled = true; }
    }

    /// <summary>A flat table: the group first (when there are several), then the server's columns. / Una tabla plana: primero el grupo y luego las columnas del servidor.</summary>
    private void ShowReport(Report r)
    {
        var grouped = r.Groups.Count > 1 || r.Groups.Any(g => g.Key != "0");
        _header = (grouped ? [L.T("ColGroup")] : new List<string>()).Concat(r.Columns.Select(ReportFormat.Title)).ToList();
        _rows = r.Groups.SelectMany(g => g.Rows.Select(row =>
            (grouped ? [ReportFormat.GroupName(r, g.Key, _names)] : new List<string>()).Concat(r.Columns.Select(c => ReportFormat.Value(c, row.GetValueOrDefault(c.Id), _names))).ToList())).ToList();
        var grid = new GridView();
        for (var i = 0; i < _header.Count; i++)
            grid.Columns.Add(new GridViewColumn { Header = _header[i], DisplayMemberBinding = new Binding($"[{i}]"), Width = double.NaN });
        _list.View = grid;
        _list.ItemsSource = _rows;
        _export.IsEnabled = _rows.Count > 0;
        _status.Text = _rows.Count == 0 ? L.T("ReportEmpty") : L.F("ReportRows", _rows.Count);
    }

    private void Export()
    {
        try
        {
            if (CsvExport.Save(this, "report.csv", _header, _rows) is { } path) _status.Text = L.F("Exported", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorWindow.Show(this, UserError.Unexpected(ex)); }
    }
}

/// <summary>The server's own state, live, and its error log, for its administrators. / El estado del propio servidor en vivo y su registro de errores.</summary>
public sealed class ServerWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly TextBlock _memory = new() { Text = L.T("WaitingStats") };
    private readonly TextBlock _cpu = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _counters = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _agentErrors = new() { TextWrapping = TextWrapping.Wrap, Text = L.T("NoAgentErrors") };
    private readonly TextBox _errors = new() { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), MinHeight = 160, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _reload = new() { Content = L.T("ReloadErrors"), MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };

    public ServerWindow(MeshServerSession session)
    {
        _session = session;
        Title = L.F("ServerTitle", string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name);
        Width = Math.Min(760, SystemParameters.WorkArea.Width); Height = Math.Min(700, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[] { (this, "ServerWindow"), (_memory, "MemoryText"), (_cpu, "CpuText"), (_counters, "CountersText"), (_agentErrors, "AgentErrorsText"), (_errors, "ErrorLogBox"), (_reload, "ReloadErrorsButton") })
            AP.SetAutomationId(e, id);
        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 18, 0, 4) };
        var page = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        page.Children.Add(Section("MemorySection")); page.Children.Add(_memory); page.Children.Add(_cpu);
        page.Children.Add(Section("CountersSection")); page.Children.Add(_counters);
        page.Children.Add(Section("AgentErrorsSection")); page.Children.Add(_agentErrors);
        if (session.CanSeeErrorLog) { page.Children.Add(Section("ErrorLogSection")); page.Children.Add(_errors); page.Children.Add(_reload); }
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        EventHandler<ServerStats> live = (_, s) => Dispatcher.BeginInvoke(() => ShowStats(s));
        session.ServerStatsReceived += live;
        _reload.Click += async (_, _) => await LoadErrors();
        Loaded += async (_, _) =>
        {
            try { await _session.WatchServerStatsAsync(TimeSpan.FromSeconds(5)); }
            catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
            if (session.CanSeeErrorLog) await LoadErrors();
        };
        // The server keeps sending until told to stop. / El servidor sigue mandando hasta que se le dice que pare.
        Closed += async (_, _) =>
        {
            session.ServerStatsReceived -= live;
            try { if (session.IsOpen) await session.WatchServerStatsAsync(null); } catch (MeshClientException) { }
        };
    }

    private void ShowStats(ServerStats s)
    {
        var used = s.TotalMemory - (s.AvailableMemory ?? s.FreeMemory);
        _memory.Text = L.F("MemoryLine", used / 1073741824.0, s.TotalMemory / 1073741824.0, s.TotalMemory > 0 ? (double)used / s.TotalMemory : 0);
        _cpu.Text = L.F("CpuLine", string.Join("  ", s.CpuAverage.Select(c => c.ToString("N2", CultureInfo.CurrentCulture))));
        _counters.Text = string.Join("\n", s.State.Select(p => $"{CounterName(p.Key)}: {p.Value:N0}"));
        _agentErrors.Text = s.AgentErrors.Count == 0 ? L.T("NoAgentErrors") : string.Join("\n", s.AgentErrors.Select(p => $"{p.Key}: {p.Value:N0}"));
    }

    /// <summary>The server's counter names in this app's language; a new counter keeps the server's own name.
    /// Los contadores del servidor en el idioma de la app; uno nuevo se queda con el nombre del servidor.</summary>
    internal static string CounterName(string serverKey)
    {
        var key = "Ctr" + serverKey;
        return L.Has(key) ? L.T(key) : serverKey;
    }

    private async Task LoadErrors()
    {
        try
        {
            var text = await _session.GetServerErrorsAsync();
            _errors.Text = string.IsNullOrWhiteSpace(text) ? L.T("ErrorLogEmpty") : text;
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
    }
}

/// <summary>Writing a notification, and showing one that arrives. / Escribir un aviso, y enseñar uno que llega.</summary>
public static class Notifications
{
    /// <summary>Text and who gets it (null = everyone connected). / Texto y a quién (null = todas las personas conectadas).</summary>
    public static (string Text, bool OnlyUser)? Ask(Window owner, UserInfo? selected)
    {
        var text = new TextBox { MaxLength = 512, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MinWidth = 380, Margin = new Thickness(0, 4, 0, 10) };
        var everyone = new RadioButton { Content = L.T("NotifyEveryone"), IsChecked = selected is null, Margin = new Thickness(0, 2, 0, 2) };
        var one = new RadioButton { Content = selected is null ? "" : L.F("NotifyUser", selected.Name), IsChecked = selected is not null, Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        foreach (var (e, id) in new (FrameworkElement, string)[] { (text, "NotificationTextBox"), (everyone, "NotifyEveryone"), (one, "NotifyOne"), (ok, "NotificationOk") }) AP.SetAutomationId(e, id);
        text.TextChanged += (_, _) => ok.IsEnabled = text.Text.Trim().Length > 0;
        var w = new Window
        {
            Title = L.T("SendNotification").TrimEnd('…'), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.T("NotificationText") }, text, everyone, one,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "NotificationForm");
        (string, bool)? result = null;
        ok.Click += (_, _) => { result = (text.Text.Trim(), one.IsChecked == true); w.Close(); };
        w.Loaded += (_, _) => text.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>A notification that arrived: a small window that does not take the keyboard from what the person is doing.
    /// Un aviso que ha llegado: una ventana pequeña que no quita el teclado a lo que esté haciendo la persona.</summary>
    public static void Show(Window owner, ServerNotification n)
    {
        var close = new Button { Content = L.T("Close"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var text = new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        AP.SetAutomationId(text, "NotificationText");
        var w = new Window
        {
            Title = string.IsNullOrEmpty(n.Title) ? L.T("NotificationTitle") : L.F("NotificationFrom", n.Title), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, Topmost = true, ShowInTaskbar = false, MaxWidth = 460,
            Content = new StackPanel { Margin = new Thickness(18), Children = { text, close } },
        };
        AP.SetAutomationId(w, "NotificationWindow");
        close.Click += (_, _) => w.Close();
        // Bottom right of the screen, above the taskbar. / Abajo a la derecha, encima de la barra de tareas.
        w.Loaded += (_, _) => { w.Left = SystemParameters.WorkArea.Right - w.ActualWidth - 16; w.Top = SystemParameters.WorkArea.Bottom - w.ActualHeight - 16; };
        w.Show();
    }
}
