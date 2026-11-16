using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// Suggestions for a box of comma-separated user names: as the person types a name, an email or a user name, the matching
/// accounts show below and taking one writes its sign-in name (UserCompletion). The accounts are read once, when the box
/// is first used; an account that cannot list users (no right to manage them) types the names by hand, as before.
/// Sugerencias para una caja de nombres con comas: al escribir nombre, correo o usuario salen debajo las cuentas que
/// encajan, y al tomar una se escribe su usuario de entrar. Las cuentas se leen una vez; sin permiso para listarlas, a mano.
/// </summary>
public static class NameSuggest
{
    private sealed class Row(UserInfo u)
    {
        public UserInfo User { get; } = u;
        public override string ToString() => UserCompletion.Label(User);
    }

    public static void Attach(TextBox box, Func<Task<IReadOnlyList<UserInfo>>> load)
    {
        var list = new ListBox { MaxHeight = 260, MinWidth = 320 };
        AP.SetAutomationId(list, "UserSuggestions");
        var border = new Border { Child = list, BorderThickness = new Thickness(1), BorderBrush = SystemColors.ActiveBorderBrush, Padding = new Thickness(2) };
        var popup = new Popup { Placement = PlacementMode.Bottom, PlacementTarget = box, StaysOpen = false, Child = border };
        box.Loaded += (_, _) => { if (Window.GetWindow(box) is { } w) border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Window.Background)) { Source = w }); };

        Task<IReadOnlyList<UserInfo>>? users = null;
        var taking = false;

        void Take(UserInfo u)
        {
            taking = true;
            box.Text = UserCompletion.Take(box.Text, u);
            box.CaretIndex = box.Text.Length;
            taking = false;
            popup.IsOpen = false;
            box.Focus();
        }

        box.TextChanged += async (_, _) =>
        {
            if (taking || !box.IsKeyboardFocused) return;
            users ??= Load(load);
            var all = await users;
            var matches = UserCompletion.Matches(all, box.Text);
            list.ItemsSource = matches.Select(u => new Row(u)).ToList();
            list.SelectedIndex = matches.Count > 0 ? 0 : -1;
            popup.IsOpen = matches.Count > 0 && box.IsKeyboardFocused;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!popup.IsOpen || list.Items.Count == 0) return;
            switch (e.Key)
            {
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); list.ScrollIntoView(list.SelectedItem); e.Handled = true; break;
                case Key.Tab or Key.Enter when list.SelectedItem is Row r: Take(r.User); e.Handled = true; break;
                case Key.Escape: popup.IsOpen = false; e.Handled = true; break;
            }
        };
        list.PreviewMouseLeftButtonUp += (_, _) => { if (list.SelectedItem is Row r) Take(r.User); };
        box.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not DependencyObject d || !list.IsAncestorOf(d)) popup.IsOpen = false; };
    }

    private static async Task<IReadOnlyList<UserInfo>> Load(Func<Task<IReadOnlyList<UserInfo>>> load)
    {
        try { return await load(); }
        catch (MeshClientException) { return []; }
    }
}
