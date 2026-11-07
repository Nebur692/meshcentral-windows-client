using System.Windows;
using System.Windows.Controls;

namespace MeshCentralClient.App;

/// <summary>Asks for one line of text. / Pide una línea de texto.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0), MinWidth = 320 };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "InputBox");
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "InputOk");
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(w, "InputDialog");
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); w.Close(); };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
