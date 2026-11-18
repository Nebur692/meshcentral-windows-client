using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The device's icon, own ports (empty = the usual one) and place on the map, as the website's device page. The place
/// only shows on servers that keep it. / El icono, los puertos propios (vacío = el normal) y el sitio en el mapa.
/// </summary>
public static class DeviceSettingsDialog
{
    private static readonly (DeviceIcon Icon, string Key)[] Icons =
    [
        (DeviceIcon.Desktop, "IconDesktop"), (DeviceIcon.Laptop, "IconLaptop"), (DeviceIcon.Phone, "IconPhone"), (DeviceIcon.Server, "IconServer"),
        (DeviceIcon.Storage, "IconStorage"), (DeviceIcon.Network, "IconNetwork"), (DeviceIcon.Board, "IconBoard"), (DeviceIcon.VirtualMachine, "IconVm"),
    ];

    public static DeviceSettings? Ask(Window owner, string deviceName, DeviceSettings now, bool withPlace)
    {
        var icon = new ComboBox { MinWidth = 220 };
        foreach (var (_, key) in Icons) icon.Items.Add(L.T(key));
        icon.SelectedIndex = Math.Max(0, Array.FindIndex(Icons, i => i.Icon == now.Icon));
        TextBox Port(int? value, string id, int usual)
        {
            var b = new TextBox { Width = 90, Text = value?.ToString(CultureInfo.InvariantCulture) ?? "" };
            AP.SetAutomationId(b, id);
            AP.SetHelpText(b, L.F("PortUsual", usual));
            return b;
        }
        var rdp = Port(now.RdpPort, "PortRdp", 3389); var ssh = Port(now.SshPort, "PortSsh", 22); var vnc = Port(now.VncPort, "PortVnc", 5900);
        var http = Port(now.HttpPort, "PortHttp", 80); var https = Port(now.HttpsPort, "PortHttps", 443);
        var lat = new TextBox { Width = 140, Text = now.Location?.Latitude.ToString("0.######", CultureInfo.InvariantCulture) ?? "" };
        var lon = new TextBox { Width = 140, Text = now.Location?.Longitude.ToString("0.######", CultureInfo.InvariantCulture) ?? "" };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(icon, "DeviceIcon"); AP.SetAutomationId(lat, "PlaceLatitude"); AP.SetAutomationId(lon, "PlaceLongitude");
        AP.SetAutomationId(ok, "DeviceSettingsOk"); AP.SetAutomationId(error, "DeviceSettingsError");
        AP.SetName(lat, L.T("PlaceLatitude")); AP.SetName(lon, L.T("PlaceLongitude"));

        static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 2) };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rowsOf = new (string Name, TextBox Box, int Usual)[] { ("RDP", rdp, 3389), ("SSH", ssh, 22), ("VNC", vnc, 5900), ("HTTP", http, 80), ("HTTPS", https, 443) };
        for (var i = 0; i < rowsOf.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var (name, box, usual) = rowsOf[i];
            AP.SetName(box, name);
            var n = new TextBlock { Text = name, Width = 70, VerticalAlignment = VerticalAlignment.Center };
            var u = new TextBlock { Text = L.F("PortUsual", usual), Opacity = 0.65, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            box.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(n, i); Grid.SetRow(box, i); Grid.SetRow(u, i);
            Grid.SetColumn(box, 1); Grid.SetColumn(u, 2);
            grid.Children.Add(n); grid.Children.Add(box); grid.Children.Add(u);
        }

        var page = new StackPanel { Margin = new Thickness(20) };
        page.Children.Add(Label(L.T("DeviceIconLabel")));
        page.Children.Add(icon);
        page.Children.Add(Label(L.T("OwnPorts")));
        page.Children.Add(new TextBlock { Text = L.T("OwnPortsHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        page.Children.Add(grid);
        if (withPlace)
        {
            page.Children.Add(Label(L.T("PlaceLabel")));
            page.Children.Add(new TextBlock { Text = L.T("PlaceHelp"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            page.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0),
                Children = { new TextBlock { Text = L.T("PlaceLatitude"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, lat,
                             new TextBlock { Text = L.T("PlaceLongitude"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) }, lon },
            });
        }
        page.Children.Add(error);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        var w = new Window
        {
            Title = L.F("DeviceSettingsTitle", deviceName), Content = page, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        AP.SetAutomationId(w, "DeviceSettingsDialog");
        DeviceSettings? result = null;
        ok.Click += (_, _) =>
        {
            static bool ReadPort(TextBox b, out int? p)
            {
                p = null;
                var t = b.Text.Trim();
                if (t.Length == 0) return true;
                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v is < 1 or > 65535) return false;
                p = v;
                return true;
            }
            static bool ReadNumber(TextBox b, double min, double max, out double? v)
            {
                v = null;
                var t = b.Text.Trim().Replace(',', '.');
                if (t.Length == 0) return true;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < min || d > max) return false;
                v = d;
                return true;
            }
            if (!ReadPort(rdp, out var pRdp) || !ReadPort(ssh, out var pSsh) || !ReadPort(vnc, out var pVnc) || !ReadPort(http, out var pHttp) || !ReadPort(https, out var pHttps))
            { error.Text = L.T("OwnPortsBad"); return; }
            DeviceLocation? place = now.Location;
            if (withPlace)
            {
                if (!ReadNumber(lat, -90, 90, out var la) || !ReadNumber(lon, -180, 180, out var lo) || (la is null) != (lo is null))
                { error.Text = L.T("PlaceBad"); return; }
                place = la is { } a && lo is { } b ? new DeviceLocation(a, b) : null;
            }
            // The usual port is no own port. / El puerto normal no es un puerto propio.
            static int? Own(int? p, int usual) => p == usual ? null : p;
            result = new DeviceSettings(Icons[Math.Max(0, icon.SelectedIndex)].Icon, Own(pRdp, 3389), Own(pSsh, 22), Own(pVnc, 5900), Own(pHttp, 80), Own(pHttps, 443), place);
            w.DialogResult = true;
        };
        return w.ShowDialog() == true ? result : null;
    }
}
