using System.Windows;
using System.Windows.Controls;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>What to show to whoever uses the devices. / Lo que se enseña a quien usa los equipos.</summary>
public sealed record OutgoingMessage(string Title, string Text, bool AsWindow);

/// <summary>
/// Asks for a message for the selected devices: a notification that goes away by itself, or a window that stays until
/// it is closed.
/// Pide un mensaje para los equipos elegidos: una notificación que se va sola, o una ventana que se queda hasta que la
/// cierren.
/// </summary>
public static class MessageDialog
{
    public static OutgoingMessage? Ask(Window owner, IReadOnlyList<string> devices)
    {
        var title = new TextBox { Text = "MeshCentral", Margin = new Thickness(0, 4, 0, 10), MinWidth = 380 };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220, Margin = new Thickness(0, 4, 0, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var toast = new RadioButton { Content = L.T("AsNotification"), IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
        var box = new RadioButton { Content = L.T("AsMessageBox") };
        var send = new Button { Content = L.T("Send"), IsDefault = false, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(title, "MessageTitleBox"); AP.SetAutomationId(text, "MessageTextBox");
        AP.SetAutomationId(toast, "AsNotification"); AP.SetAutomationId(box, "AsMessageBox"); AP.SetAutomationId(send, "MessageSend");
        // Both are needed: the agent shows nothing without a title or a text. / Hacen falta los dos: sin título o texto el agente no enseña nada.
        void Check() => send.IsEnabled = title.Text.Trim().Length > 0 && text.Text.Trim().Length > 0;
        title.TextChanged += (_, _) => Check();
        text.TextChanged += (_, _) => Check();

        var w = new Window
        {
            Title = L.T("SendMessageItem").TrimEnd('…'), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.F("CommandsFor", string.Join(", ", devices)), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 12) },
                    new TextBlock { Text = L.T("MessageTitle") }, title,
                    new TextBlock { Text = L.T("MessageText") }, text,
                    toast, box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { send, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "MessageDialog");
        OutgoingMessage? result = null;
        send.Click += (_, _) => { result = new(title.Text.Trim(), text.Text.Trim(), box.IsChecked == true); w.Close(); };
        w.Loaded += (_, _) => text.Focus();
        w.ShowDialog();
        return result;
    }
}
