using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One recording as a row. / Una grabación como fila.</summary>
public sealed class RecordingRow(Recording recording, Names names)
{
    private readonly Recording r = recording;
    public Recording Recording => r;
    public string When => r.Start?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string Device => r.DeviceName ?? names.Device(r.DeviceId);
    public string Kind => L.T(r.Kind switch
    {
        RecordedKind.Desktop => "RecDesktop", RecordedKind.Terminal or RecordedKind.UserShell => "RecTerminal",
        RecordedKind.AdminPowerShell or RecordedKind.UserPowerShell => "RecPowerShell", RecordedKind.Files => "RecFiles",
        RecordedKind.Chat => "RecChat", RecordedKind.AmtWsman or RecordedKind.AmtRedirection => "RecAmt", _ => "RecOther",
    });
    public string Who => string.Join(", ", r.UserIds.Select(names.User));
    public string Length => r.Length.TotalMinutes >= 1 ? $"{(int)r.Length.TotalMinutes} min {r.Length.Seconds} s" : $"{r.Length.Seconds} s";
    public string Size => r.Present ? Sizes.Short(r.Bytes) : L.T("RecGone");
    public string Group => r.GroupName ?? "";
    public bool Matches(string q) => q.Length == 0 || new[] { Device, Who, Group, r.FileName }.Any(x => x.Contains(q, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Short sizes ("12 KB"). / Tamaños cortos.</summary>
internal static class Sizes
{
    public static string Short(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.#} GB",
    };
}

/// <summary>
/// The server's recorded sessions: when, which device, what kind, who and how big. A recording is downloaded where the
/// person chooses, which the server only allows to a web session (single sign-on); otherwise the website's own list
/// opens in the browser. The same as the Android app's screen.
/// Las sesiones grabadas: cuándo, qué equipo, qué tipo, quién y cuánto ocupa. Se baja donde elige la persona, cosa que
/// el servidor solo deja a una sesión web; si no, se abre en el navegador la lista de la propia web. Igual que en Android.
/// </summary>
public sealed class RecordingsWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly Names _names;
    private readonly ObservableCollection<RecordingRow> _rows = new();
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBox _search = new() { MinWidth = 260 };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _download = new() { MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _play = new() { Content = L.T("RecPlay"), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _refresh = new() { Content = L.T("Refresh"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
    private List<RecordingRow> _all = new();

    public RecordingsWindow(MeshServerSession session, Names names)
    {
        _session = session; _names = names;
        Title = L.T("RecordingsItem");
        Width = 980; Height = 600; MinWidth = 640; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "RecordingsWindow"); AP.SetAutomationId(_list, "RecordingsList"); AP.SetAutomationId(_search, "RecordingsSearch");
        AP.SetAutomationId(_download, "RecordingsDownload"); AP.SetAutomationId(_play, "RecordingsPlay"); AP.SetAutomationId(_status, "RecordingsStatus");
        AP.SetName(_search, L.T("RecSearch"));
        _download.Content = L.T(session.HasWebSession ? "RecDownload" : "RecOpenWeb");
        _play.Visibility = session.HasWebSession ? Visibility.Visible : Visibility.Collapsed;

        var grid = new GridView();
        void Col(string head, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(head), Width = width, DisplayMemberBinding = new Binding(path) });
        Col("RecWhen", nameof(RecordingRow.When), 150);
        Col("ColDevice", nameof(RecordingRow.Device), 170);
        Col("RecKind", nameof(RecordingRow.Kind), 120);
        Col("RecWho", nameof(RecordingRow.Who), 140);
        Col("RecLength", nameof(RecordingRow.Length), 90);
        Col("RecSize", nameof(RecordingRow.Size), 100);
        Col("RecGroup", nameof(RecordingRow.Group), 150);
        _list.View = grid;
        _list.ItemsSource = _rows;

        var bar = new DockPanel { Margin = new Thickness(16, 12, 16, 8), LastChildFill = false };
        bar.Children.Add(new TextBlock { Text = L.T("RecSearch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(_search);
        foreach (var b in new[] { _refresh, _play, _download }) { DockPanel.SetDock(b, Dock.Right); bar.Children.Add(b); }
        var top = new StackPanel();
        top.Children.Add(bar);
        var hint = new TextBlock { Text = L.T(session.HasWebSession ? "RecHintDownload" : "RecHintWeb"), Margin = new Thickness(16, 0, 16, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        top.Children.Add(hint);
        _status.Margin = new Thickness(16, 4, 16, 8);
        top.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _list.Margin = new Thickness(16, 0, 16, 16);
        root.Children.Add(_list);
        Content = root;

        _search.TextChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) =>
        {
            var present = _list.SelectedItem is RecordingRow { Recording.Present: true } r;
            _download.IsEnabled = present || !_session.HasWebSession;
            _play.IsEnabled = present && _list.SelectedItem is RecordingRow { Recording.Kind: RecordedKind.Desktop or RecordedKind.Terminal or RecordedKind.UserShell };
        };
        _list.MouseDoubleClick += async (_, _) => await DownloadAsync();
        _download.IsEnabled = !session.HasWebSession;
        _download.Click += async (_, _) => await DownloadAsync();
        _play.Click += (_, _) => Play();
        _refresh.Click += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        _rows.Clear();
        foreach (var r in _all.Where(r => r.Matches(q))) _rows.Add(r);
    }

    private async Task LoadAsync()
    {
        _refresh.IsEnabled = false;
        _status.Text = L.T("RecLoading");
        try
        {
            _all = (await _session.GetRecordingsAsync()).Select(r => new RecordingRow(r, _names)).ToList();
            Filter();
            _status.Text = _all.Count == 0 ? L.T("RecNone") : L.F("RecCount", _all.Count);
        }
        catch (MeshClientException ex)
        {
            var err = UserError.From(ex, _session.ServerInfo.Version);
            _status.Text = $"{err.Message}  {err.ActLine}  {err.Footer}";
        }
        finally { _refresh.IsEnabled = true; }
    }

    private static void OpenInBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void Play()
    {
        if (_list.SelectedItem is not RecordingRow r) return;
        OpenInBrowser(new Uri(_session.Server.BaseUri, "player.htm?stream=" + Uri.EscapeDataString(r.Recording.FileName)));
    }

    private async Task DownloadAsync()
    {
        // Without a web session the server gives nothing: its own list opens in the browser. / Sin sesión web, su lista en el navegador.
        if (!_session.HasWebSession) { OpenInBrowser(_session.RecordingsPageUri); return; }
        if (_list.SelectedItem is not RecordingRow { Recording.Present: true } row) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = row.Recording.FileName, Title = L.T("RecDownload") };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        var ok = false;
        _download.IsEnabled = false;
        try
        {
            await using (var file = File.Create(path))
                await _session.DownloadRecordingAsync(row.Recording, file, new Progress<long>(n => _status.Text = L.F("RecDownloading", row.Recording.FileName, Sizes.Short(n))));
            ok = true;
            _status.Text = L.F("RecSaved", path);
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorWindow.Show(this, UserError.From(new MeshClientException(MeshFailure.Rejected, "grabaciones/descargar/permisos-fichero", "The file could not be written.", ex), _session.ServerInfo.Version));
        }
        finally
        {
            // Half a file is worse than none. / Medio fichero es peor que ninguno.
            if (!ok) try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _download.IsEnabled = true;
        }
    }
}
