using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeshCentralClient.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The devices on a map, as the website's: OpenStreetMap with a dot per device that has a place (set by hand, GPS, Wi-Fi
/// or guessed from its address), green when online; its "Open" opens the device. It follows the live list.
/// Los equipos en un mapa, como en la web: OpenStreetMap con un punto por equipo que tenga sitio; verde si está en línea;
/// su «Abrir» abre el equipo. Sigue la lista en vivo.
/// </summary>
public sealed class MapWindow : Window
{
    private readonly WebView2 _view = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 6, 12, 6), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly Func<IReadOnlyList<Device>> _devices;
    private readonly Action<string> _open;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string _sent = "";

    public MapWindow(Func<IReadOnlyList<Device>> devices, Action<string> open)
    {
        _devices = devices; _open = open;
        Title = L.T("MapTitle");
        Width = 1000; Height = 680; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "MapWindow"); AP.SetAutomationId(_view, "MapView"); AP.SetAutomationId(_status, "MapStatus");
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_view);
        Content = root;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await SendAsync();
    }

    private async Task StartAsync()
    {
        try { await WebAssets.InitAsync(_view); }
        catch (WebView2RuntimeNotFoundException ex) { ErrorWindow.Show(this, UserError.MissingWebView(ex)); Close(); return; }
        _view.CoreWebView2.WebMessageReceived += (_, e) => { if (e.TryGetWebMessageAsString() is { Length: > 0 } id) _open(id); };
        _view.CoreWebView2.NavigationCompleted += async (_, _) => { _sent = ""; await SendAsync(); _timer.Start(); };
        _view.CoreWebView2.Navigate($"https://{WebAssets.Host}/map/map.html");
    }

    /// <summary>The devices with a place, sent again only when something changed. / Los equipos con sitio, solo si cambió algo.</summary>
    private async Task SendAsync()
    {
        if (_view.CoreWebView2 is null) return;
        var all = _devices();
        var placed = all.Where(d => d.MapPlace is not null).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var list = new JsonArray(placed.Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name, ["lat"] = d.MapPlace!.Latitude, ["lon"] = d.MapPlace.Longitude,
            ["online"] = d.IsOnline, ["kind"] = (int)d.MapPlace.Kind,
        }).ToArray());
        var labels = new JsonObject
        {
            ["online"] = L.T("MapOnline"), ["offline"] = L.T("MapOffline"), ["open"] = L.T("MapOpen"), ["none"] = L.T("MapNone"),
            ["kinds"] = new JsonObject { ["1"] = L.T("MapKindIp"), ["2"] = L.T("MapKindWifi"), ["3"] = L.T("MapKindGps"), ["4"] = L.T("MapKindUser") },
        };
        var json = new JsonObject { ["devices"] = list, ["labels"] = labels }.ToJsonString();
        _status.Text = L.F("MapCount", placed.Count, all.Count);
        if (json == _sent) return;
        _sent = json;
        await _view.CoreWebView2.ExecuteScriptAsync($"setDevices({json})");
    }
}
