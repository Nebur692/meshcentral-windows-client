using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One device of a run and what it gave back. / Un equipo de una ejecución y lo que devolvió.</summary>
public sealed class CommandRun(Device device) : INotifyPropertyChanged
{
    private string _state = "";
    private string _output = "";

    public Device Device { get; } = device;
    public string Name => Device.Name;
    public string State { get => _state; set { _state = value; Changed(); } }
    public string Output { get => _output; set { _output = value; Changed(); } }
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Runs the same commands on one or several devices at once and shows each output. Each device gets its batch alone:
/// the agent ignores, without answering, a batch sent while it is running another.
/// Ejecuta las mismas órdenes en uno o varios equipos a la vez y enseña la salida de cada uno. Cada equipo recibe su
/// tanda sola: el agente ignora, sin contestar, una tanda que llega mientras ejecuta otra.
/// </summary>
public sealed class CommandsWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly ObservableCollection<CommandRun> _runs = new();
    private readonly TextBox _commands = new()
    {
        AcceptsReturn = true, AcceptsTab = true, MinHeight = 110, MaxHeight = 220, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly RadioButton _cmd = new() { Content = L.T("UseCmd"), IsChecked = true, Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _ps = new() { Content = L.T("UsePowerShell"), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _asUser = new() { Content = L.T("RunAsUser"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _run = new() { Content = L.T("Run"), MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 10) };
    private readonly ListView _list = new() { Width = 300 };
    private readonly TextBox _output = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 0, 0),
    };
    private bool _closing;

    public CommandsWindow(MeshServerSession session, IReadOnlyList<Device> devices, IReadOnlyList<string> skipped)
    {
        _session = session;
        foreach (var d in devices) _runs.Add(new CommandRun(d));
        Title = $"{L.T("RunCommandsItem").TrimEnd('…')} — {(devices.Count == 1 ? devices[0].Name : devices.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))}";
        Width = 960; Height = 640; MinWidth = 620; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "CommandsWindow"); AP.SetAutomationId(_commands, "CommandsBox"); AP.SetAutomationId(_cmd, "UseCmd");
        AP.SetAutomationId(_ps, "UsePowerShell"); AP.SetAutomationId(_asUser, "RunAsUser"); AP.SetAutomationId(_run, "RunButton");
        AP.SetAutomationId(_list, "RunList"); AP.SetAutomationId(_output, "RunOutput");

        var top = new StackPanel { Margin = new Thickness(16, 12, 16, 0) };
        top.Children.Add(new TextBlock { Text = L.F("CommandsFor", string.Join(", ", devices.Select(d => d.Name))), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        if (skipped.Count > 0) top.Children.Add(new TextBlock { Text = L.F("SkippedDevices", string.Join(", ", skipped)), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        // The choice only matters for Windows devices; the rest always use sh. / La elección solo cuenta en Windows; los demás usan siempre sh.
        if (devices.Any(d => d.IsWindows))
            top.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0),
                Children = { new TextBlock { Text = L.T("WindowsShell"), VerticalAlignment = VerticalAlignment.Center }, _cmd, _ps },
            });
        top.Children.Add(_asUser);
        top.Children.Add(new TextBlock { Text = L.T("CommandsHint"), Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        top.Children.Add(_commands);
        top.Children.Add(_run);
        _commands.TextChanged += (_, _) => _run.IsEnabled = _commands.Text.Trim().Length > 0 && !_runs.Any(r => r.State == L.T("Running"));
        _run.IsEnabled = false;
        _run.Click += async (_, _) => await RunAsync();

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColDevice"), Width = 170, DisplayMemberBinding = new Binding(nameof(CommandRun.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColState"), Width = 110, DisplayMemberBinding = new Binding(nameof(CommandRun.State)) });
        _list.View = grid;
        _list.ItemsSource = _runs;
        _list.SelectionChanged += (_, _) => ShowSelected();

        var results = new DockPanel { Margin = new Thickness(16, 0, 16, 16) };
        DockPanel.SetDock(_list, Dock.Left);
        results.Children.Add(_list);
        results.Children.Add(_output);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(results);
        Content = root;
        _list.SelectedIndex = 0;
        Loaded += (_, _) => _commands.Focus();
        Closed += (_, _) => _closing = true;
    }

    private void ShowSelected()
    {
        if (_list.SelectedItem is CommandRun r) _output.Text = r.Output;
    }

    private async Task RunAsync()
    {
        var commands = _commands.Text;
        var powerShell = _ps.IsChecked == true;
        var asUser = _asUser.IsChecked == true;
        _run.IsEnabled = false;
        foreach (var r in _runs) { r.State = L.T("Running"); r.Output = ""; }
        ShowSelected();
        await Task.WhenAll(_runs.Select(async r =>
        {
            try
            {
                var text = await _session.RunCommandsAsync(r.Device, commands, MeshServerSession.ShellFor(r.Device, powerShell), asUser);
                r.Output = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                r.State = L.T("Finished");
            }
            catch (MeshClientException ex)
            {
                var err = UserError.From(ex, _session.ServerInfo.Version);
                r.Output = $"{err.Message}{Environment.NewLine}{err.ActLine}{Environment.NewLine}{err.Footer}";
                r.State = L.T("Failed");
            }
            if (!_closing && ReferenceEquals(_list.SelectedItem, r)) ShowSelected();
        }));
        if (_closing) return;
        _run.IsEnabled = _commands.Text.Trim().Length > 0;
    }
}
