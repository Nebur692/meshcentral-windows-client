using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The programs installed on a device, as the website's "Software" tab: name, version, publisher and date, with a search.
/// The agent reads them when asked, which can take a while. / Los programas instalados en un equipo, como la pestaña
/// «Software» de la web, con un buscador. El agente los lee al pedirlos, y puede tardar.
/// </summary>
public sealed class SoftwareWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly ObservableCollection<InstalledProgram> _rows = new();
    private readonly ListView _list = new() { Margin = new Thickness(16, 0, 16, 16) };
    private readonly TextBox _search = new() { MinWidth = 260 };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 4, 16, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _refresh = new() { Content = L.T("Refresh"), MinWidth = 100 };
    private List<InstalledProgram> _all = new();

    public SoftwareWindow(MeshServerSession session, DeviceRow row)
    {
        _session = session; _device = row.Device;
        Title = L.F("SoftwareTitle", row.Name);
        Width = 900; Height = 600; MinWidth = 560; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "SoftwareWindow"); AP.SetAutomationId(_list, "SoftwareList");
        AP.SetAutomationId(_search, "SoftwareSearch"); AP.SetAutomationId(_status, "SoftwareStatus");
        AP.SetName(_search, L.T("SoftwareSearch"));

        var grid = new GridView();
        void Col(string head, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(head), Width = width, DisplayMemberBinding = new Binding(path) });
        Col("SoftwareName", nameof(InstalledProgram.Name), 340);
        Col("SoftwareVersion", nameof(InstalledProgram.Version), 140);
        Col("SoftwarePublisher", nameof(InstalledProgram.Publisher), 220);
        Col("SoftwareDate", nameof(InstalledProgram.Date), 110);
        _list.View = grid;
        _list.ItemsSource = _rows;

        var bar = new DockPanel { Margin = new Thickness(16, 12, 16, 4), LastChildFill = false };
        bar.Children.Add(new TextBlock { Text = L.T("SoftwareSearch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(_search);
        DockPanel.SetDock(_refresh, Dock.Right);
        bar.Children.Add(_refresh);
        var top = new StackPanel { Children = { bar, _status } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;

        _search.TextChanged += (_, _) => Filter();
        _refresh.Click += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        _rows.Clear();
        foreach (var p in _all.Where(p => q.Length == 0 || new[] { p.Name, p.Publisher ?? "", p.Version ?? "" }.Any(x => x.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
            _rows.Add(p);
    }

    private async Task LoadAsync()
    {
        _refresh.IsEnabled = false;
        _status.Text = L.T("SoftwareLoading");
        try
        {
            _all = (await _session.GetInstalledProgramsAsync(_device)).ToList();
            Filter();
            _status.Text = _all.Count == 0 ? L.T("SoftwareNone") : L.F("SoftwareCount", _all.Count);
        }
        catch (MeshClientException ex)
        {
            var err = UserError.From(ex, _session.ServerInfo.Version);
            _status.Text = $"{err.Message}  {err.ActLine}  {err.Footer}";
        }
        finally { _refresh.IsEnabled = true; }
    }
}
