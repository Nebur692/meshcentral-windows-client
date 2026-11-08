using System.Windows;
using System.Windows.Controls;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>Asks where to move the selected devices, among the groups where they can go.
/// Pregunta adónde mover los equipos elegidos, entre los grupos donde pueden ir.</summary>
public static class MoveDialog
{
    private sealed class Item(DeviceGroup g)
    {
        public DeviceGroup Group { get; } = g;
        public override string ToString() => Group.Name;
    }

    public static DeviceGroup? Ask(Window owner, IReadOnlyList<string> devices, IReadOnlyList<DeviceGroup> targets)
    {
        var list = new ListBox { MinWidth = 360, MinHeight = 140, MaxHeight = 320, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var g in targets) list.Items.Add(new Item(g));
        var ok = new Button { Content = L.T("Move"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(list, "MoveTargetList"); AP.SetAutomationId(ok, "MoveOk");
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;
        var w = new Window
        {
            Title = L.T("MoveTitle"), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.F("MoveLabel", string.Join(", ", devices)), TextWrapping = TextWrapping.Wrap },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "MoveDialog");
        DeviceGroup? result = null;
        ok.Click += (_, _) => { result = (list.SelectedItem as Item)?.Group; w.Close(); };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Item i) { result = i.Group; w.Close(); } };
        w.Loaded += (_, _) => { list.Focus(); if (list.Items.Count == 1) list.SelectedIndex = 0; };
        w.ShowDialog();
        return result;
    }
}
