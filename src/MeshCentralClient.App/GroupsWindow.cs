using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The device groups of the account: create, edit and delete them, give and take rights to other accounts, and add new
/// devices (invitation link, Windows agent download, Linux command). It follows the server live: changes made on the
/// website or by someone else appear here at once. Each button is enabled only when the account may do it.
/// Los grupos de equipos de la cuenta: crearlos, editarlos y borrarlos, dar y quitar permisos a otras cuentas, y añadir
/// equipos nuevos (enlace de invitación, descarga del agente de Windows, orden de Linux). Sigue al servidor en vivo: lo
/// cambiado en la web o por otra persona aparece aquí enseguida. Cada botón se activa solo si la cuenta puede hacerlo.
/// </summary>
public sealed class GroupsWindow : Window
{
    private sealed class GroupItem(DeviceGroup g)
    {
        public DeviceGroup Group { get; } = g;
        public override string ToString() => Group.Name;
    }

    public sealed class MemberItem(GroupMember m)
    {
        public GroupMember Member { get; } = m;
        public string Name => Member.IsUserGroup ? L.F("UserGroupSuffix", Member.Name) : Member.Name;
        public string Rights => RightsDialog.Summary(Member.Rights);
        public override string ToString() => Name;
    }

    private readonly MeshServerSession _session;
    private readonly Func<string, int> _deviceCount;
    private readonly ListBox _groups = new() { MinWidth = 220 };
    private readonly TextBlock _empty = new() { Text = L.T("NoGroupSelected"), Opacity = 0.65, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _detail = new() { Margin = new Thickness(20, 12, 20, 12), Visibility = Visibility.Collapsed };
    private readonly TextBlock _name = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _desc = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _facts = new() { Opacity = 0.65, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _new = new() { Content = L.T("NewGroup"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _edit = new() { Content = L.T("EditGroup"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _delete = new() { Content = L.T("DeleteGroup"), MinWidth = 100 };
    private readonly ListView _members = new() { MinHeight = 90, MaxHeight = 220, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _addUsers = new() { Content = L.T("AddUsers"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _addUserGroup = new() { Content = L.T("AddUserGroup"), MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _changeRights = new() { Content = L.T("ChangeRights"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _removeUser = new() { Content = L.T("RemoveUser"), MinWidth = 90 };
    private readonly StackPanel _addPanel = new();
    private readonly TextBlock _cannotAdd = new() { Text = L.T("CannotAddDevices"), Opacity = 0.65 };
    private readonly ComboBox _mode = new() { MinWidth = 260 };
    private readonly ComboBox _expiry = new() { MinWidth = 120, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button _createInvite = new() { Content = L.T("CreateInvite"), MinWidth = 110 };
    private readonly TextBox _invite = new() { IsReadOnly = true, Margin = new Thickness(0, 6, 8, 0) };
    private readonly Button _copyInvite = new() { Content = L.T("CopyLink"), MinWidth = 80, Margin = new Thickness(0, 6, 0, 0), IsEnabled = false };
    private readonly ComboBox _platform = new() { MinWidth = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _download = new() { Content = L.T("DownloadAgent"), MinWidth = 110 };
    private readonly TextBox _linux = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 8, 0), MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _copyLinux = new() { Content = L.T("CopyLink"), MinWidth = 80, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 10), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 };
    private string? _selectId;
    private string? _inviteFor;

    private static readonly (int Hours, string Key)[] Expiries = { (1, "Expire1h"), (8, "Expire8h"), (24, "Expire1d"), (168, "Expire1w"), (720, "Expire30d") };
    private static readonly (AgentPlatform Platform, string Key, string File)[] Platforms =
        { (AgentPlatform.Windows64, "Windows64", "meshagent64"), (AgentPlatform.WindowsArm64, "WindowsArm64", "meshagentarm64"), (AgentPlatform.Windows32, "Windows32", "meshagent32") };
    private static readonly (AgentInstallMode Mode, string Key)[] Modes =
        { (AgentInstallMode.BackgroundAndInteractive, "InstallBoth"), (AgentInstallMode.BackgroundOnly, "InstallBackground"), (AgentInstallMode.InteractiveOnly, "InstallInteractive") };

    /// <param name="deviceCount">How many devices of the list are in a group, for the delete warning. / Cuántos equipos de la lista hay en un grupo.</param>
    public GroupsWindow(MeshServerSession session, Func<string, int> deviceCount)
    {
        _session = session;
        _deviceCount = deviceCount;
        Title = L.F("GroupsTitle", string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name);
        // Never taller or wider than the screen's work area: on 1024×768 the title bar went off the top (CI screenshot 08-11).
        // Nunca más alta ni ancha que la zona útil de la pantalla: en 1024×768 la barra de título se salía por arriba.
        Width = Math.Min(980, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(720, Width); MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "GroupsWindow"), (_groups, "GroupList"), (_new, "NewGroupButton"), (_edit, "EditGroupButton"), (_delete, "DeleteGroupButton"),
            (_name, "GroupNameText"), (_desc, "GroupDescText"), (_members, "MemberList"), (_addUsers, "AddUsersButton"), (_addUserGroup, "AddUserGroupButton"), (_changeRights, "ChangeRightsButton"),
            (_removeUser, "RemoveUserButton"), (_mode, "InstallMode"), (_expiry, "InviteExpiry"), (_createInvite, "CreateInviteButton"), (_invite, "InviteLinkBox"),
            (_copyInvite, "CopyInviteButton"), (_platform, "AgentPlatform"), (_download, "DownloadAgentButton"), (_linux, "LinuxCommandBox"),
            (_copyLinux, "CopyLinuxButton"), (_status, "GroupsStatus"),
        }) AP.SetAutomationId(e, id);
        AP.SetName(_invite, L.T("InviteLabel"));
        AP.SetName(_linux, L.T("LinuxLabel"));

        foreach (var x in Expiries) _expiry.Items.Add(L.T(x.Key));
        _expiry.SelectedIndex = 2;
        foreach (var x in Platforms) _platform.Items.Add(L.T(x.Key));
        _platform.SelectedIndex = 0;
        foreach (var x in Modes) _mode.Items.Add(L.T(x.Key));
        _mode.SelectedIndex = 0;

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColUser"), Width = 220, DisplayMemberBinding = new Binding(nameof(MemberItem.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColRights"), Width = 440, DisplayMemberBinding = new Binding(nameof(MemberItem.Rights)) });
        _members.View = grid;

        // Left: the groups. / Izquierda: los grupos.
        var left = new DockPanel { Margin = new Thickness(16, 12, 0, 0) };
        DockPanel.SetDock(_new, Dock.Bottom);
        left.Children.Add(_new);
        left.Children.Add(_groups);

        // Right: the chosen group. / Derecha: el grupo elegido.
        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 20, 0, 2) };
        static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) }; foreach (var i in items) p.Children.Add(i); return p; }
        static DockPanel Stretch(UIElement main, UIElement right) { var d = new DockPanel(); DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); d.Children.Add(main); return d; }
        _detail.Children.Add(_name);
        _detail.Children.Add(_desc);
        _detail.Children.Add(_facts);
        _detail.Children.Add(Row(_edit, _delete));
        _detail.Children.Add(Section("UsersSection"));
        _detail.Children.Add(_members);
        _detail.Children.Add(Row(_addUsers, _addUserGroup, _changeRights, _removeUser));
        _detail.Children.Add(Section("AddDevicesSection"));
        _detail.Children.Add(_cannotAdd);
        _addPanel.Children.Add(Row(new TextBlock { Text = L.T("InstallMode"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _mode));
        _addPanel.Children.Add(new TextBlock { Text = L.T("InviteLabel"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        _addPanel.Children.Add(Row(new TextBlock { Text = L.T("InviteExpiry"), VerticalAlignment = VerticalAlignment.Center }, _expiry, _createInvite));
        _addPanel.Children.Add(Stretch(_invite, _copyInvite));
        _addPanel.Children.Add(new TextBlock { Text = L.T("WindowsAgentLabel"), Margin = new Thickness(0, 14, 0, 0) });
        _addPanel.Children.Add(Row(_platform, _download));
        _addPanel.Children.Add(new TextBlock { Text = L.T("LinuxLabel"), Margin = new Thickness(0, 14, 0, 0) });
        _addPanel.Children.Add(Stretch(_linux, _copyLinux));
        _detail.Children.Add(_addPanel);

        var right = new Grid();
        right.Children.Add(_empty);
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _groups.SelectionChanged += (_, _) => ShowSelected();
        _members.SelectionChanged += (_, _) => UpdateMemberButtons();
        _new.Click += async (_, _) => await NewGroup();
        _edit.Click += async (_, _) => await EditGroup();
        _delete.Click += async (_, _) => await DeleteGroup();
        _addUsers.Click += async (_, _) => await AddUsers();
        _addUserGroup.Click += async (_, _) => await AddUserGroup();
        _changeRights.Click += async (_, _) => await ChangeRights();
        _removeUser.Click += async (_, _) => await RemoveUser();
        _createInvite.Click += async (_, _) => await CreateInvite();
        _copyInvite.Click += (_, _) => Copy(_invite.Text);
        _copyLinux.Click += (_, _) => Copy(_linux.Text);
        _download.Click += async (_, _) => await Download();
        // A new choice makes a new link. / Otra elección hace otro enlace.
        _expiry.SelectionChanged += (_, _) => ClearInvite();
        _mode.SelectionChanged += (_, _) => ClearInvite();

        EventHandler<GroupChangedEventArgs> live = (_, _) => Dispatcher.BeginInvoke(Refresh);
        session.GroupChanged += live;
        Closed += (_, _) => session.GroupChanged -= live;
        Refresh();
        if (_groups.Items.Count > 0) _groups.SelectedIndex = 0;
    }

    private DeviceGroup? Selected => (_groups.SelectedItem as GroupItem)?.Group;

    /// <summary>Rebuilds the list from the session's groups, keeping the choice. / Rehace la lista con los grupos de la sesión, sin perder la elección.</summary>
    private void Refresh()
    {
        var keep = _selectId ?? Selected?.Id;
        _selectId = null;
        _groups.Items.Clear();
        foreach (var g in _session.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)) _groups.Items.Add(new GroupItem(g));
        _groups.SelectedItem = _groups.Items.OfType<GroupItem>().FirstOrDefault(i => i.Group.Id == keep);
        _empty.Text = L.T(_groups.Items.Count == 0 ? "NoGroups" : "NoGroupSelected");
        _new.IsEnabled = _session.CanCreateGroups;
        ShowSelected();
    }

    private void ShowSelected()
    {
        var g = Selected;
        _detail.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = g is null ? Visibility.Visible : Visibility.Collapsed;
        if (g is null) return;
        var can = _session.GroupAbilitiesOn(g);
        _name.Text = g.Name;
        _desc.Text = g.Description ?? "";
        _desc.Visibility = string.IsNullOrEmpty(g.Description) ? Visibility.Collapsed : Visibility.Visible;
        var kind = g.Kind switch
        {
            DeviceGroupKind.Agents => "GroupKindAgents", DeviceGroupKind.IntelAmtOnly => "GroupKindAmt",
            DeviceGroupKind.LocalDevices => "GroupKindLocal", DeviceGroupKind.IpKvm => "GroupKindKvm", _ => "",
        };
        _facts.Text = $"{L.T(kind)} · {L.F("GroupDevices", _deviceCount(g.Id))}";
        _edit.IsEnabled = can.Edit;
        _delete.IsEnabled = can.Delete;
        var memberId = (_members.SelectedItem as MemberItem)?.Member.Id;
        _members.Items.Clear();
        foreach (var m in g.Members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)) _members.Items.Add(new MemberItem(m));
        _members.SelectedItem = _members.Items.OfType<MemberItem>().FirstOrDefault(i => i.Member.Id == memberId);
        _addUsers.IsEnabled = can.ManageUsers;
        _addUserGroup.IsEnabled = can.ManageUsers;
        UpdateMemberButtons();
        _addPanel.Visibility = can.AddDevices ? Visibility.Visible : Visibility.Collapsed;
        _cannotAdd.Visibility = can.AddDevices ? Visibility.Collapsed : Visibility.Visible;
        if (can.AddDevices) _linux.Text = _session.LinuxInstallCommand(g);
        if (_inviteFor != g.Id) ClearInvite();
    }

    private void UpdateMemberButtons()
    {
        var g = Selected;
        var m = (_members.SelectedItem as MemberItem)?.Member;
        // Not oneself, and only a group administrator touches another administrator. / Uno mismo no, y a un administrador solo lo toca otro.
        var ok = g is not null && m is not null && m.Id != _session.User.Id && _session.GroupAbilitiesOn(g).ManageUsers
                 && (m.Rights != 0xFFFFFFFF || _session.GroupAbilitiesOn(g).Delete);
        _changeRights.IsEnabled = ok;
        _removeUser.IsEnabled = ok;
    }

    private void ClearInvite() { _invite.Text = ""; _copyInvite.IsEnabled = false; _inviteFor = null; }

    private void Copy(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); _status.Text = L.T("LinkCopied"); } catch (Exception) { }
    }

    /// <summary>Runs a request; its error goes to the error window with the burburito code. / Hace una petición; su error va a la ventana de error.</summary>
    private async Task Run(Func<Task<string?>> op)
    {
        IsEnabled = false;
        try { if (await op() is { } done) _status.Text = done; }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorWindow.Show(this, UserError.Unexpected(ex)); }
        finally { IsEnabled = true; }
    }

    private Task NewGroup() => Run(async () =>
    {
        if (GroupForm.Ask(this, L.T("NewGroup").TrimEnd('…'), "", "") is not { } f) return null;
        _selectId = await _session.CreateGroupAsync(f.Name, f.Description);
        // The createmesh event usually arrives first; if not, the next one refreshes. / El aviso createmesh suele llegar antes.
        Refresh();
        return L.F("GroupCreated", f.Name);
    });

    private Task EditGroup() => Run(async () =>
    {
        if (Selected is not { } g || GroupForm.Ask(this, L.T("EditGroup").TrimEnd('…'), g.Name, g.Description ?? "") is not { } f) return null;
        await _session.EditGroupAsync(g, f.Name, f.Description);
        return L.T("GroupSaved");
    });

    private Task DeleteGroup() => Run(async () =>
    {
        if (Selected is not { } g) return null;
        if (MessageBox.Show(this, L.F("ConfirmDeleteGroup", g.Name, _deviceCount(g.Id)), L.T("DeleteGroup").TrimEnd('…'),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        await _session.DeleteGroupAsync(g);
        return L.F("GroupDeleted", g.Name);
    });

    private Task AddUsers() => Run(async () =>
    {
        if (Selected is not { } g) return null;
        if (RightsDialog.Ask(this, L.F("AddUsersTitle", g.Name), askNames: true, 0, canGrantFull: _session.GroupAbilitiesOn(g).Delete) is not { } c) return null;
        var said = await _session.AddMembersAsync(g, c.UserNames, c.Rights);
        return L.F("UsersAdded", said);
    });

    /// <summary>Every member of a user group gets these rights on the device group. / Cada miembro de un grupo de usuarios recibe estos permisos.</summary>
    private Task AddUserGroup() => Run(async () =>
    {
        if (Selected is not { } g) return null;
        var candidates = (await _session.GetUserGroupsAsync()).Where(u => !g.Links.ContainsKey(u.Id)).ToList();
        if (candidates.Count == 0) return L.T("NoUserGroupsToAdd");
        if (PickDialog.Ask(this, L.T("PickUserGroupTitle"), candidates, u => u.Name) is not { } ug) return null;
        if (RightsDialog.Ask(this, L.F("RightsTitle", L.F("UserGroupSuffix", ug.Name), g.Name), askNames: false, 0, canGrantFull: _session.GroupAbilitiesOn(g).Delete) is not { } c) return null;
        await _session.AddUserGroupAsync(g, ug, c.Rights);
        return L.F("UserGroupAdded", L.F("UserGroupSuffix", ug.Name));
    });

    private Task ChangeRights() => Run(async () =>
    {
        if (Selected is not { } g || (_members.SelectedItem as MemberItem) is not { } m) return null;
        if (RightsDialog.Ask(this, L.F("RightsTitle", m.Name, g.Name), askNames: false, m.Member.Rights, canGrantFull: _session.GroupAbilitiesOn(g).Delete) is not { } c) return null;
        await _session.ChangeMemberRightsAsync(g, m.Member.Id, c.Rights);
        return L.F("RightsChanged", m.Name);
    });

    private Task RemoveUser() => Run(async () =>
    {
        if (Selected is not { } g || (_members.SelectedItem as MemberItem) is not { } m) return null;
        if (MessageBox.Show(this, L.F("ConfirmRemoveUser", m.Name, g.Name), L.T("RemoveUser"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        await _session.RemoveMemberAsync(g, m.Member.Id);
        return L.F("UserRemoved", m.Name);
    });

    private Task CreateInvite() => Run(async () =>
    {
        if (Selected is not { } g) return null;
        var link = await _session.CreateInviteLinkAsync(g, Expiries[Math.Max(0, _expiry.SelectedIndex)].Hours, Modes[Math.Max(0, _mode.SelectedIndex)].Mode);
        _invite.Text = link;
        _inviteFor = g.Id;
        _copyInvite.IsEnabled = true;
        return null;
    });

    private Task Download() => Run(async () =>
    {
        if (Selected is not { } g) return null;
        var p = Platforms[Math.Max(0, _platform.SelectedIndex)];
        // The website's file name, without the characters Windows does not allow. / El nombre de la web, sin lo que Windows no admite.
        var safe = string.Concat(g.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not ' ' and not '\''));
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{p.File}-{safe}.exe", DefaultExt = ".exe", Filter = "Windows (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return null;
        var target = dialog.FileName;
        var part = target + ".part";
        _status.Text = L.F("Downloading", Path.GetFileName(target), "");
        long bytes;
        try
        {
            // Written aside and renamed at the end: a cut never leaves a broken agent under the real name.
            // Se escribe aparte y se renombra al final: un corte nunca deja un agente roto con el nombre bueno.
            await using (var file = File.Create(part)) bytes = await _session.DownloadAgentAsync(g, p.Platform, Modes[Math.Max(0, _mode.SelectedIndex)].Mode, file);
            File.Move(part, target, overwrite: true);
        }
        finally { try { File.Delete(part); } catch (IOException) { } }
        return L.F("AgentSaved", target, bytes / 1024);
    });
}

/// <summary>Name and description of a group. / Nombre y descripción de un grupo.</summary>
public static class GroupForm
{
    public sealed record Result(string Name, string Description);

    public static Result? Ask(Window owner, string title, string name, string description)
    {
        var nameBox = new TextBox { Text = name, MaxLength = 128, Margin = new Thickness(0, 4, 0, 12), MinWidth = 380 };
        var descBox = new TextBox { Text = description, MaxLength = 1024, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Margin = new Thickness(0, 4, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = name.Trim().Length > 0 };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(nameBox, "GroupNameBox"); AP.SetAutomationId(descBox, "GroupDescBox"); AP.SetAutomationId(ok, "GroupFormOk");
        nameBox.TextChanged += (_, _) => ok.IsEnabled = nameBox.Text.Trim().Length > 0;
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.T("GroupName") }, nameBox,
                    new TextBlock { Text = L.T("GroupDescription") }, descBox,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "GroupForm");
        Result? result = null;
        ok.Click += (_, _) => { result = new(nameBox.Text.Trim(), descBox.Text.Trim()); w.Close(); };
        w.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        w.ShowDialog();
        return result;
    }
}
