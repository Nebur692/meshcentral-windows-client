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
    private readonly string _kindName;
    private readonly Func<int, int, Task<ITerminalConnection>> _open;
    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 4, 12, 6), Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private ITerminalConnection? _terminal;
    private bool _closing;

    /// <summary>A MeshCentral terminal (the agent's shell). / Una terminal de MeshCentral (el shell del agente).</summary>
    public TerminalWindow(MeshServerSession session, DeviceRow device, TunnelProtocol kind)
        : this(session, device, KindName(kind), async (cols, rows) => await TerminalSession.OpenAsync(session, device.Id, kind, cols, rows))
    {
    }

    /// <summary>Any terminal: <paramref name="open"/> gets the screen size and connects (SSH, for one). / Cualquier terminal: <paramref name="open"/> recibe el tamaño y conecta (SSH, por ejemplo).</summary>
    public TerminalWindow(MeshServerSession session, DeviceRow device, string kindName, Func<int, int, Task<ITerminalConnection>> open)
    {
        _session = session;
        _device = device;
        _kindName = kindName;
        _open = open;
        Title = $"{kindName} — {device.Name}";
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
            _terminal = await _open(cols, rows);
        }
        catch (MeshClientException ex)
        {
            ShowFailure(UserError.From(ex, _session.ServerInfo.Version));
            return;
        }
        if (_closing) { await _terminal.DisposeAsync(); return; }
        _terminal.Ended += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            // The shell ended right after an order (exit, logout, Ctrl+D): what was asked for, so the window goes. Ending on its own is news.
            // El shell acabó justo tras una orden (exit, logout, Ctrl+D): lo que se pidió, así que la ventana se va. Acabar por su cuenta es noticia.
            if (DateTime.UtcNow - _lastOrder < TimeSpan.FromSeconds(3)) Close();
            else ShowFailure(UserError.From(e, _session.ServerInfo.Version));
        });
        _terminal.Output += (_, d) => Post(new JsonObject { ["t"] = "data", ["b64"] = Convert.ToBase64String(d.Span) });
        _status.Text = (_terminal.IsRecorded ? L.T("TerminalRecorded") : L.T("TerminalHelp")) + (_terminal is SshTerminal ssh ? "  ·  " + ssh.HostKey.Fingerprint : "");
        Post(new JsonObject { ["t"] = "focus" });
    }

    private DateTime _lastOrder = DateTime.MinValue;

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
                var typed = m["d"]?.ToString() ?? "";
                if (typed.AsSpan().IndexOfAny('\r', '\n', '\u0004') >= 0) _lastOrder = DateTime.UtcNow;
                _ = Guard(t.WriteAsync(typed));
                break;
            case "size" when _terminal is { } t:
                _ = Guard(t.ResizeAsync((int?)m["cols"] ?? 80, (int?)m["rows"] ?? 25));
                break;
            case "title":
                Title = $"{_kindName} — {_device.Name} — {m["title"]}";
                break;
            case "copy":
                _ = CopyAsync(m["text"]?.ToString() ?? "");
                break;
            case "paste-request":
                string text = "";
                try { text = Clipboard.GetText(); } catch (Exception) { }
                if (text.Length > 0) Post(new JsonObject { ["t"] = "paste", ["text"] = text });
                break;
        }
    }

    /// <summary>To the clipboard, which another program may hold for a moment; and it says how it went, as copying has no other sign.
    /// Al portapapeles, que otro programa puede tener un momento; y dice cómo fue, porque copiar no da otra señal.</summary>
    private async Task CopyAsync(string text)
    {
        var told = L.T("ClipBusy");
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                told = L.F("TerminalCopied", text.Length);
                break;
            }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(100); }
        }
        var before = _status.Text;
        var shown = _status.Text = told;
        await Task.Delay(TimeSpan.FromSeconds(3));
        // Unless something else was said meanwhile. / Salvo que entretanto se haya dicho otra cosa.
        if (_status.Text == shown) _status.Text = before == shown ? (_terminal?.IsRecorded == true ? L.T("TerminalRecorded") : L.T("TerminalHelp")) : before;
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
