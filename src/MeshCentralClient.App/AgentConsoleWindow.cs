using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The agent's own console, as the website's "Console" tab: a command line to the agent (help, info, netinfo…) and what it
/// answers. Up and down bring back earlier commands. / La consola del propio agente, como la pestaña «Consola» de la web:
/// una línea de órdenes al agente y lo que contesta. Arriba y abajo recuperan las órdenes anteriores.
/// </summary>
public sealed class AgentConsoleWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly TextBox _output = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 12, 16, 8),
    };
    private readonly TextBox _input = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly Button _send = new() { Content = L.T("ConsoleSend"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
    private readonly List<string> _history = new();
    private int _at;

    public AgentConsoleWindow(MeshServerSession session, DeviceRow row)
    {
        _session = session; _device = row.Device;
        Title = L.F("ConsoleTitle", row.Name);
        Width = 860; Height = 560; MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "AgentConsoleWindow"); AP.SetAutomationId(_output, "ConsoleOutput");
        AP.SetAutomationId(_input, "ConsoleInput"); AP.SetAutomationId(_send, "ConsoleSend");
        AP.SetName(_input, L.T("ConsoleInput"));
        _output.Text = L.T("ConsoleHelp") + Environment.NewLine;

        var bar = new DockPanel { Margin = new Thickness(16, 0, 16, 16) };
        DockPanel.SetDock(_send, Dock.Right);
        bar.Children.Add(_send);
        bar.Children.Add(_input);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_output);
        Content = root;

        EventHandler<ConsoleText> said = (_, t) =>
        {
            if (t.DeviceId != _device.Id) return;
            Dispatcher.BeginInvoke(() => Append(t.Text));
        };
        _session.ConsoleOutput += said;
        Closed += (_, _) => _session.ConsoleOutput -= said;
        _send.Click += async (_, _) => await SendAsync();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || _history.Count == 0) return;
            _at = Math.Clamp(_at + (e.Key == Key.Up ? -1 : 1), 0, _history.Count);
            _input.Text = _at < _history.Count ? _history[_at] : "";
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        };
        Loaded += (_, _) => _input.Focus();
    }

    private void Append(string text)
    {
        _output.AppendText(text.EndsWith('\n') ? text : text + Environment.NewLine);
        _output.ScrollToEnd();
    }

    private async Task SendAsync()
    {
        var cmd = _input.Text.Trim();
        if (cmd.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != cmd) _history.Add(cmd);
        _at = _history.Count;
        _input.Clear();
        Append("> " + cmd);
        try { await _session.SendConsoleCommandAsync(_device, cmd); }
        catch (MeshClientException ex)
        {
            var err = UserError.From(ex, _session.ServerInfo.Version);
            Append($"{err.Message}  {err.ActLine}  {err.Footer}");
        }
    }
}
