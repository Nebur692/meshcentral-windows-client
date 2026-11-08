using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>Who to add and with which rights. / A quién añadir y con qué permisos.</summary>
public sealed record RightsChoice(IReadOnlyList<string> UserNames, long Rights);

/// <summary>
/// The rights of an account on a device group, as checkboxes with the website's meaning: what is allowed, and the limits
/// that take something away from remote control.
/// Los permisos de una cuenta en un grupo de equipos, como casillas con el significado de la web: lo permitido y los
/// límites que quitan algo al control remoto.
/// </summary>
public static class RightsDialog
{
    private const long Full = 0xFFFFFFFF;

    internal static readonly (MeshRights Right, string Key)[] Allows =
    {
        (MeshRights.EditGroup, "RightEditGroup"), (MeshRights.ManageUsers, "RightManageUsers"), (MeshRights.ManageDevices, "RightManageDevices"),
        (MeshRights.RemoteControl, "RightRemoteControl"), (MeshRights.AgentConsole, "RightAgentConsole"), (MeshRights.ServerFiles, "RightServerFiles"),
        (MeshRights.Wake, "RightWake"), (MeshRights.SetNotes, "RightSetNotes"), (MeshRights.DeviceDetails, "RightDeviceDetails"),
        (MeshRights.ChatNotify, "RightChatNotify"), (MeshRights.Uninstall, "RightUninstall"), (MeshRights.RemoteCommand, "RightRemoteCommand"),
        (MeshRights.ResetOff, "RightResetOff"), (MeshRights.GuestSharing, "RightGuestSharing"), (MeshRights.Relay, "RightRelay"),
    };

    internal static readonly (MeshRights Right, string Key)[] Limits =
    {
        (MeshRights.RemoteViewOnly, "RightViewOnly"), (MeshRights.LimitedInput, "RightLimitedInput"), (MeshRights.NoDesktop, "RightNoDesktop"),
        (MeshRights.NoTerminal, "RightNoTerminal"), (MeshRights.NoFiles, "RightNoFiles"), (MeshRights.NoAmt, "RightNoAmt"),
        (MeshRights.LimitEvents, "RightLimitEvents"),
    };

    /// <summary>A short line for the list of users. / Una línea corta para la lista de usuarios.</summary>
    public static string Summary(long rights)
    {
        if (rights == Full) return L.T("RightFullShort");
        var parts = Allows.Concat(Limits).Where(a => (rights & (long)a.Right) != 0).Select(a => L.T(a.Key)).ToList();
        return parts.Count == 0 ? L.T("RightNone") : string.Join(", ", parts);
    }

    /// <param name="askNames">Show the box for user names (adding); otherwise only rights (changing). / Enseña la caja de nombres (añadir).</param>
    /// <param name="canGrantFull">Only a group administrator can make another. / Solo un administrador del grupo puede nombrar otro.</param>
    public static RightsChoice? Ask(Window owner, string title, bool askNames, long initial, bool canGrantFull)
    {
        var names = new TextBox { Margin = new Thickness(0, 4, 0, 12), MinWidth = 420 };
        AP.SetAutomationId(names, "UserNamesBox");
        var full = new CheckBox { Content = L.T("RightFull"), IsChecked = initial == Full, IsEnabled = canGrantFull || initial == Full, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        AP.SetAutomationId(full, "RightFull");
        CheckBox Box((MeshRights Right, string Key) a)
        {
            var c = new CheckBox { Content = L.T(a.Key), IsChecked = initial != Full && (initial & (long)a.Right) != 0, Margin = new Thickness(0, 2, 16, 2) };
            AP.SetAutomationId(c, a.Right.ToString());
            return c;
        }
        var allow = Allows.Select(a => (a.Right, Box: Box(a))).ToList();
        var limit = Limits.Select(a => (a.Right, Box: Box(a))).ToList();
        void Sync() { foreach (var (_, b) in allow.Concat(limit)) b.IsEnabled = full.IsChecked != true; }
        full.Checked += (_, _) => Sync();
        full.Unchecked += (_, _) => Sync();
        Sync();

        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(ok, "RightsOk");
        if (askNames)
        {
            ok.IsEnabled = false;
            names.TextChanged += (_, _) => ok.IsEnabled = names.Text.Trim().Length > 0;
        }

        static UniformGrid Grid(IEnumerable<CheckBox> boxes)
        {
            var g = new UniformGrid { Columns = 2 };
            foreach (var b in boxes) g.Children.Add(b);
            return g;
        }
        var body = new StackPanel { Margin = new Thickness(20), MaxWidth = 620 };
        if (askNames) { body.Children.Add(new TextBlock { Text = L.T("UserNamesLabel"), TextWrapping = TextWrapping.Wrap }); body.Children.Add(names); }
        body.Children.Add(full);
        body.Children.Add(new TextBlock { Text = L.T("RightsAllow"), Opacity = 0.75, Margin = new Thickness(0, 4, 0, 2) });
        body.Children.Add(Grid(allow.Select(a => a.Box)));
        body.Children.Add(new TextBlock { Text = L.T("RightsLimit"), Opacity = 0.75, Margin = new Thickness(0, 10, 0, 2) });
        body.Children.Add(Grid(limit.Select(a => a.Box)));
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });

        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        AP.SetAutomationId(w, "RightsDialog");
        RightsChoice? result = null;
        ok.Click += (_, _) =>
        {
            var rights = full.IsChecked == true ? Full : allow.Concat(limit).Where(a => a.Box.IsChecked == true).Aggregate(0L, (r, a) => r | (long)a.Right);
            result = new(names.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), rights);
            w.Close();
        };
        w.Loaded += (_, _) => { if (askNames) names.Focus(); else full.Focus(); };
        w.ShowDialog();
        return result;
    }
}
