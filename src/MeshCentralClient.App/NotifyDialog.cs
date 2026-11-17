using System.Windows;
using System.Windows.Controls;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// Which notices this account gets when a device (or any device of a group) connects or disconnects: by email and by the
/// account's messaging (ntfy, Telegram…), as the website's "Notifications". The server sends them; the app need not run.
/// Qué avisos recibe la cuenta cuando un equipo (o cualquiera de un grupo) se conecta o desconecta: por correo y por
/// mensajería, como «Notificaciones» de la web. Los manda el servidor: la app no tiene que estar abierta.
/// </summary>
public static class NotifyDialog
{
    public static DeviceNotify? Ask(Window owner, string title, DeviceNotify current)
    {
        CheckBox Box(string key, string id, DeviceNotify bit)
        {
            var b = new CheckBox { Content = L.T(key), IsChecked = current.HasFlag(bit), Margin = new Thickness(0, 6, 0, 0), Tag = bit };
            AP.SetAutomationId(b, id);
            return b;
        }
        var boxes = new[]
        {
            Box("NotifyEmailConnect", "NotifyEmailConnect", DeviceNotify.EmailConnect),
            Box("NotifyEmailDisconnect", "NotifyEmailDisconnect", DeviceNotify.EmailDisconnect),
            Box("NotifyMessageConnect", "NotifyMessageConnect", DeviceNotify.MessageConnect),
            Box("NotifyMessageDisconnect", "NotifyMessageDisconnect", DeviceNotify.MessageDisconnect),
        };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(ok, "NotifyOk");
        var page = new StackPanel { Margin = new Thickness(20) };
        page.Children.Add(new TextBlock { Text = L.T("NotifyHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, MaxWidth = 420 });
        foreach (var b in boxes) page.Children.Add(b);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        var w = new Window
        {
            Title = title, Content = page, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        AP.SetAutomationId(w, "NotifyDialog");
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return null;
        return boxes.Where(b => b.IsChecked == true).Aggregate(DeviceNotify.None, (a, b) => a | (DeviceNotify)b.Tag);
    }
}
