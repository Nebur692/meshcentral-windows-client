using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MeshCentralClient.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MeshCentralClient.App;

/// <summary>
/// A terminal (shell, cmd or PowerShell) on one device: xterm.js in WebView2, bytes over a MeshCentral relay tunnel.
/// Una terminal (shell, cmd o PowerShell) en un equipo: xterm.js en WebView2 y los bytes por un túnel de relé de MeshCentral.
/// </summary>
public sealed class TerminalWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly DeviceRow _device;
    private readonly TunnelProtocol _kind;
    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 4, 12, 6), Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private TerminalSession? _terminal;
    private bool _closing;

    public TerminalWindow(MeshServerSession session, DeviceRow device, TunnelProtocol kind)
    {
        _session = session;
        _device = device;
        _kind = kind;
        Title = $"{KindName(kind)} — {device.Name}";
        Width = 960;
        Height = 600;
        MinWidth = 400;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "TerminalWindow");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_view, "TerminalView");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "TerminalStatus");
        var dock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(_status);
        dock.Children.Add(_view);
        Content = dock;
        _status.Text = L.T("Connecting");
        Loaded += async (_, _) => await StartAsync();
        Closed += async (_, _) => { _closing = true; ThemeService.Changed -= OnTheme; if (_terminal is not null) await _terminal.DisposeAsync(); };
        ThemeService.Changed += OnTheme;
    }

    public static string KindName(TunnelProtocol kind) => kind switch
    {
        TunnelProtocol.PowerShell => "PowerShell",
        TunnelProtocol.UserShell => L.T("UserShell"),
        _ => L.T("Terminal"),
    };

    private async Task StartAsync()
    {
        try
        {
            await WebAssets.InitAsync(_view);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            ShowFailure(UserError.MissingWebView(ex));
            return;
        }
        var ready = new TaskCompletionSource<(int Cols, int Rows)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _view.CoreWebView2.WebMessageReceived += (_, e) => OnPageMessage(e, ready);
        _view.CoreWebView2.Navigate($"https://{WebAssets.Host}/terminal/terminal.html?dark={(ThemeService.IsDark ? 1 : 0)}");
        var (cols, rows) = await ready.Task;

        try
        {
            _terminal = await TerminalSession.OpenAsync(_session, _device.Id, _kind, cols, rows);
        }
        catch (MeshClientException ex)
        {
            ShowFailure(UserError.From(ex, _session.ServerInfo.Version));
            return;
        }
        if (_closing) { await _terminal.DisposeAsync(); return; }
        _terminal.Tunnel.DataReceived += (_, d) => Post(new JsonObject { ["t"] = "data", ["b64"] = Convert.ToBase64String(d.Span) });
        _terminal.Tunnel.TextReceived += (_, t) => Post(new JsonObject { ["t"] = "text", ["text"] = t });
        _terminal.Tunnel.Closed += (_, e) => Dispatcher.BeginInvoke(() => ShowFailure(UserError.From(e, _session.ServerInfo.Version)));
        _status.Text = _terminal.Tunnel.IsRecorded ? L.T("TerminalRecorded") : L.T("TerminalHelp");
        Post(new JsonObject { ["t"] = "focus" });
    }

    private void OnPageMessage(CoreWebView2WebMessageReceivedEventArgs e, TaskCompletionSource<(int, int)> ready)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; } catch (JsonException) { return; }
        if (m is null) return;
        switch (m["t"]?.ToString())
        {
            case "ready":
                ready.TrySetResult(((int?)m["cols"] ?? 80, (int?)m["rows"] ?? 25));
                break;
            case "input" when _terminal is { } t:
                _ = Guard(t.WriteAsync(m["d"]?.ToString() ?? ""));
                break;
            case "size" when _terminal is { } t:
                _ = Guard(t.ResizeAsync((int?)m["cols"] ?? 80, (int?)m["rows"] ?? 25));
                break;
            case "title":
                Title = $"{KindName(_kind)} — {_device.Name} — {m["title"]}";
                break;
            case "copy":
                try { Clipboard.SetText(m["text"]?.ToString() ?? ""); } catch (Exception) { }
                break;
            case "paste-request":
                string text = "";
                try { text = Clipboard.GetText(); } catch (Exception) { }
                if (text.Length > 0) Post(new JsonObject { ["t"] = "paste", ["text"] = text });
                break;
        }
    }

    // A failed keystroke after the tunnel closed is already reported by Closed. / Una tecla que falla tras cerrarse el túnel ya la avisa Closed.
    private static async Task Guard(Task t) { try { await t; } catch (MeshClientException) { } }

    private void Post(JsonObject message)
    {
        var json = message.ToJsonString();
        Dispatcher.BeginInvoke(() => { if (!_closing && _view.CoreWebView2 is { } c) c.PostWebMessageAsJson(json); });
    }

    private void ShowFailure(UserError error)
    {
        if (_closing) return;
        _status.Text = $"{error.Message}  {error.ActLine}  {error.Footer}";
        Post(new JsonObject { ["t"] = "closed", ["text"] = error.Message });
    }

    private void OnTheme(object? sender, EventArgs e) => Post(new JsonObject { ["t"] = "theme", ["dark"] = ThemeService.IsDark });
}
