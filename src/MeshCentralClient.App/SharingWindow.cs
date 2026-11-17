using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One link as a row. / Un enlace como fila.</summary>
public sealed class ShareRow(DeviceShare s)
{
    public DeviceShare Share => s;
    public string Guest => s.GuestName;
    public string Kinds => string.Join(", ", new[] { (ShareKinds.Desktop, "ShareDesktop"), (ShareKinds.Terminal, "ShareTerminal"), (ShareKinds.Files, "ShareFiles") }
        .Where(k => s.Kinds.HasFlag(k.Item1)).Select(k => L.T(k.Item2)))
        + (s.ViewOnly ? " · " + L.T("ShareViewOnly") : "") + (s.AsksConsent ? " · " + L.T("ShareAsks") : "");
    public string Until => s.Expires?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string Url => s.Url ?? L.T("ShareUrlHidden");
    public override string ToString() => $"{Guest} {Until}";
}

/// <summary>
/// Sharing a device with a guest without an account, as the website's "Share": a link to its desktop, terminal or
/// files for a time, asking or not the person at the device; and the links still valid, to copy or end them.
/// Compartir un equipo con un invitado sin cuenta, como «Compartir» de la web: un enlace a su escritorio, terminal o
/// ficheros por un tiempo, pidiendo o no permiso a quien está en el equipo; y los enlaces vigentes, para copiarlos o acabarlos.
/// </summary>
public sealed class SharingWindow : Window
{
    private static readonly (string Key, TimeSpan Time)[] Durations =
    [
        ("Share1Hour", TimeSpan.FromHours(1)), ("Share8Hours", TimeSpan.FromHours(8)), ("Share1Day", TimeSpan.FromDays(1)), ("Share1Week", TimeSpan.FromDays(7)),
    ];

    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly TextBox _guest = new() { MinWidth = 220 };
    private readonly CheckBox _desktop = new() { Content = L.T("ShareDesktop"), IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _terminal = new() { Content = L.T("ShareTerminal"), Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _files = new() { Content = L.T("ShareFiles") };
    private readonly ComboBox _duration = new() { MinWidth = 140 };
    private readonly CheckBox _consent = new() { Content = L.T("ShareAskConsent"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _create = new() { Content = L.T("ShareCreate"), MinWidth = 120, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _link = new() { IsReadOnly = true, Margin = new Thickness(0, 10, 8, 0) };
    private readonly ListView _list = new() { MinHeight = 120, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button _copy = new() { Content = L.T("CopyLink"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _remove = new() { Content = L.T("ShareRemove"), MinWidth = 100, IsEnabled = false };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    public SharingWindow(MeshServerSession session, DeviceRow row)
    {
        _session = session; _device = row.Device;
        Title = L.F("ShareTitle", row.Name);
        Width = 820; Height = 620; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "SharingWindow"), (_guest, "ShareGuest"), (_desktop, "ShareDesktop"), (_terminal, "ShareTerminal"), (_files, "ShareFiles"),
            (_duration, "ShareDuration"), (_consent, "ShareConsent"), (_create, "ShareCreate"), (_link, "ShareLink"), (_list, "ShareList"),
            (_copy, "ShareCopy"), (_remove, "ShareRemove"), (_status, "ShareStatus"),
        }) AP.SetAutomationId(e, id);
        AP.SetName(_guest, L.T("ShareGuest"));
        AP.SetName(_link, L.T("ShareLinkLabel"));
        foreach (var d in Durations) _duration.Items.Add(L.T(d.Key));
        _duration.SelectedIndex = 0;

        var grid = new GridView();
        void Col(string head, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(head), Width = width, DisplayMemberBinding = new Binding(path) });
        Col("ShareGuest", nameof(ShareRow.Guest), 150);
        Col("ShareWhat", nameof(ShareRow.Kinds), 220);
        Col("ShareUntil", nameof(ShareRow.Until), 140);
        Col("ShareLinkLabel", nameof(ShareRow.Url), 240);
        _list.View = grid;

        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 14, 0, 2) };
        static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) }; foreach (var i in items) p.Children.Add(i); return p; }
        var page = new StackPanel { Margin = new Thickness(20, 8, 20, 12) };
        page.Children.Add(Section("ShareNew"));
        page.Children.Add(new TextBlock { Text = L.T("ShareHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(Row(new TextBlock { Text = L.T("ShareGuest"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _guest,
            new TextBlock { Text = L.T("ShareFor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }, _duration));
        page.Children.Add(Row(_desktop, _terminal, _files));
        page.Children.Add(_consent);
        page.Children.Add(_create);
        var linkRow = new DockPanel();
        var copyNew = new Button { Content = L.T("CopyLink"), MinWidth = 80, Margin = new Thickness(0, 10, 0, 0), IsEnabled = false };
        AP.SetAutomationId(copyNew, "ShareCopyNew");
        DockPanel.SetDock(copyNew, Dock.Right);
        linkRow.Children.Add(copyNew);
        linkRow.Children.Add(_link);
        page.Children.Add(linkRow);
        page.Children.Add(Section("ShareCurrent"));
        page.Children.Add(_list);
        page.Children.Add(Row(_copy, _remove));
        page.Children.Add(_status);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        void Check() => _create.IsEnabled = _guest.Text.Trim().Length > 0 && (_desktop.IsChecked == true || _terminal.IsChecked == true || _files.IsChecked == true);
        _guest.TextChanged += (_, _) => Check();
        foreach (var b in new[] { _desktop, _terminal, _files }) { b.Checked += (_, _) => Check(); b.Unchecked += (_, _) => Check(); }
        Check();
        _list.SelectionChanged += (_, _) =>
        {
            _remove.IsEnabled = _list.SelectedItem is ShareRow;
            _copy.IsEnabled = _list.SelectedItem is ShareRow { Share.Url: { Length: > 0 } };
        };
        _create.Click += async (_, _) => await CreateAsync(copyNew);
        copyNew.Click += (_, _) => Copy(_link.Text);
        _copy.Click += (_, _) => { if (_list.SelectedItem is ShareRow { Share.Url: { } u }) Copy(u); };
        _remove.Click += async (_, _) => await RemoveAsync();
        Loaded += async (_, _) => await LoadAsync();
    }

    private void Copy(string text)
    {
        try { Clipboard.SetText(text); _status.Text = L.T("LinkCopied"); }
        catch (System.Runtime.InteropServices.COMException) { _status.Text = L.T("ClipboardBusy"); }
    }

    private async Task Run(Func<Task> op)
    {
        IsEnabled = false;
        try { await op(); }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally { IsEnabled = true; }
    }

    private Task LoadAsync() => Run(async () =>
    {
        var shares = await _session.GetDeviceSharesAsync(_device);
        _list.ItemsSource = shares.Select(s => new ShareRow(s)).ToList();
        _status.Text = shares.Count == 0 ? L.T("ShareNone") : L.F("ShareCount", shares.Count);
    });

    private Task CreateAsync(Button copyNew) => Run(async () =>
    {
        var kinds = (_desktop.IsChecked == true ? ShareKinds.Desktop : 0) | (_terminal.IsChecked == true ? ShareKinds.Terminal : 0) | (_files.IsChecked == true ? ShareKinds.Files : 0);
        var share = await _session.ShareDeviceAsync(_device, _guest.Text, kinds, Durations[Math.Max(0, _duration.SelectedIndex)].Time, _consent.IsChecked == true);
        _link.Text = share.Url ?? "";
        copyNew.IsEnabled = _link.Text.Length > 0;
        await LoadAsync();
        _status.Text = L.F("ShareMade", share.GuestName);
    });

    private Task RemoveAsync() => Run(async () =>
    {
        if (_list.SelectedItem is not ShareRow row) return;
        if (MessageBox.Show(this, L.F("ShareConfirmRemove", row.Guest), L.T("ShareRemove"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await _session.RemoveDeviceShareAsync(_device, row.Share);
        await LoadAsync();
        _status.Text = L.F("ShareRemoved", row.Guest);
    });
}
