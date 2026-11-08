using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>Server-wide rights as the website names them. / Los permisos de servidor con el nombre de la web.</summary>
public static class SiteRightsText
{
    internal static readonly (SiteRight Right, string Key)[] Items =
    {
        (SiteRight.ServerBackup, "SiteBackup"), (SiteRight.ServerRestore, "SiteRestore"), (SiteRight.ServerFiles, "SiteFiles"), (SiteRight.ServerUpdates, "SiteUpdates"),
        (SiteRight.ManageUsers, "SiteUsers"), (SiteRight.ManageUserGroups, "SiteUserGroups"), (SiteRight.ManageRecordings, "SiteRecordings"), (SiteRight.AllEvents, "SiteAllEvents"),
        (SiteRight.Locked, "SiteLocked"), (SiteRight.NoNewDeviceGroups, "SiteNoGroups"), (SiteRight.NoNewDevices, "SiteNoDevices"), (SiteRight.NoTools, "SiteNoTools"),
        (SiteRight.LockSettings, "SiteLockSettings"),
    };

    public static string Summary(long rights) =>
        rights == (long)SiteRight.FullAdministrator ? L.T("SiteFull")
        : Items.Where(i => (rights & (long)i.Right) != 0).Select(i => L.T(i.Key)).DefaultIfEmpty(L.T("SiteNone")).Aggregate((a, b) => a + ", " + b);
}

/// <summary>
/// The accounts of the server and its user groups, for whoever manages them. It follows the server live, and each
/// button is enabled only when this account may do it (the server would otherwise say "ok" and change nothing).
/// Las cuentas del servidor y sus grupos de usuarios, para quien los gestiona. Sigue al servidor en vivo, y cada botón
/// se activa solo si esta cuenta puede hacerlo (si no, el servidor diría «ok» sin cambiar nada).
/// </summary>
public sealed class UsersWindow : Window
{
    public sealed class UserRow(UserInfo u, string me)
    {
        public UserInfo User { get; } = u;
        public string Name => User.Name;
        public string RealName => User.RealName ?? "";
        public string Email => User.Email ?? "";
        public string Rights => SiteRightsText.Summary(User.SiteRights);
        public string LastLogin => User.LastLogin?.LocalDateTime.ToString("g") ?? L.T("Never");
        public string State => string.Join(", ", new[]
        {
            User.Id == me ? L.T("StateYou") : null, User.IsLocked ? L.T("StateLocked") : null,
            User.HasTwoFactor ? L.T("StateTwoFactor") : null, User.MustChangePassword ? L.T("StateMustChange") : null,
        }.OfType<string>());
        public override string ToString() => Name;
    }

    private sealed class GroupItem(UserGroup g)
    {
        public UserGroup Group { get; } = g;
        public override string ToString() => Group.Name;
    }

    private sealed class MemberItem(string id, string name)
    {
        public string Id { get; } = id;
        public override string ToString() => name;
    }

    private readonly MeshServerSession _session;
    private readonly ListView _users = new();
    private readonly Button _newUser = new() { Content = L.T("NewUser"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _editUser = new() { Content = L.T("EditUser"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _password = new() { Content = L.T("SetPassword"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _lock = new() { Content = L.T("LockUser"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _deleteUser = new() { Content = L.T("DeleteUser"), MinWidth = 90 };
    private readonly ListBox _groups = new() { MinWidth = 220 };
    private readonly Button _newGroup = new() { Content = L.T("NewUserGroup"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _groupEmpty = new() { Opacity = 0.65, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _groupDetail = new() { Margin = new Thickness(20, 12, 20, 12) };
    private readonly TextBlock _groupName = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _groupDesc = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _groupNote = new() { Opacity = 0.65, Margin = new Thickness(0, 4, 0, 0), Text = L.T("ManagedElsewhere") };
    private readonly Button _editGroup = new() { Content = L.T("EditUserGroup"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _deleteGroup = new() { Content = L.T("DeleteUserGroup"), MinWidth = 90 };
    private readonly ListBox _members = new() { MinHeight = 90, MaxHeight = 220, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _addMembers = new() { Content = L.T("AddGroupMembers"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _removeMember = new() { Content = L.T("RemoveGroupMember"), MinWidth = 90 };
    private readonly TextBlock _groupRights = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 10), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 };
    private List<UserInfo> _userList = [];
    private List<UserGroup> _groupList = [];

    public UsersWindow(MeshServerSession session)
    {
        _session = session;
        Title = L.F("UsersTitle", string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name);
        Width = Math.Min(1000, SystemParameters.WorkArea.Width); Height = Math.Min(680, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(720, Width); MinHeight = Math.Min(460, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "UsersWindow"), (_users, "UserList"), (_newUser, "NewUserButton"), (_editUser, "EditUserButton"), (_password, "SetPasswordButton"),
            (_lock, "LockUserButton"), (_deleteUser, "DeleteUserButton"), (_groups, "UserGroupList"), (_newGroup, "NewUserGroupButton"),
            (_groupName, "UserGroupNameText"), (_editGroup, "EditUserGroupButton"), (_deleteGroup, "DeleteUserGroupButton"), (_members, "UserGroupMembers"),
            (_addMembers, "AddMembersButton"), (_removeMember, "RemoveMemberButton"), (_groupRights, "UserGroupRightsText"), (_status, "UsersStatus"),
        }) AP.SetAutomationId(e, id);

        var grid = new GridView();
        foreach (var (key, path, width) in new[] { ("ColUser", nameof(UserRow.Name), 140.0), ("ColRealName", nameof(UserRow.RealName), 150.0), ("ColEmail", nameof(UserRow.Email), 180.0),
                     ("ColSiteRights", nameof(UserRow.Rights), 200.0), ("ColLastLogin", nameof(UserRow.LastLogin), 130.0), ("ColState", nameof(UserRow.State), 160.0) })
            grid.Columns.Add(new GridViewColumn { Header = L.T(key), Width = width, DisplayMemberBinding = new Binding(path) });
        _users.View = grid;

        static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) }; foreach (var i in items) p.Children.Add(i); return p; }
        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 20, 0, 2) };

        var usersTab = new DockPanel { Margin = new Thickness(12) };
        var userButtons = Row(_newUser, _editUser, _password, _lock, _deleteUser);
        DockPanel.SetDock(userButtons, Dock.Bottom);
        usersTab.Children.Add(userButtons);
        usersTab.Children.Add(_users);

        var left = new DockPanel { Margin = new Thickness(12, 12, 0, 12) };
        DockPanel.SetDock(_newGroup, Dock.Bottom);
        left.Children.Add(_newGroup);
        left.Children.Add(_groups);
        _groupDetail.Children.Add(_groupName);
        _groupDetail.Children.Add(_groupDesc);
        _groupDetail.Children.Add(_groupNote);
        _groupDetail.Children.Add(Row(_editGroup, _deleteGroup));
        _groupDetail.Children.Add(Section("MembersSection"));
        _groupDetail.Children.Add(_members);
        _groupDetail.Children.Add(Row(_addMembers, _removeMember));
        _groupDetail.Children.Add(Section("GroupRightsSection"));
        _groupDetail.Children.Add(_groupRights);
        var right = new Grid();
        right.Children.Add(_groupEmpty);
        right.Children.Add(new ScrollViewer { Content = _groupDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var groupsTab = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        groupsTab.Children.Add(left);
        groupsTab.Children.Add(right);

        var tabs = new TabControl { Margin = new Thickness(8, 8, 8, 0) };
        var usersItem = new TabItem { Header = L.T("UsersTab"), Content = session.CanManageUsers ? usersTab : Note("CannotManageUsers") };
        var groupsItem = new TabItem { Header = L.T("UserGroupsTab"), Content = session.CanManageUserGroups ? groupsTab : Note("CannotManageUserGroups") };
        AP.SetAutomationId(usersItem, "UsersTab"); AP.SetAutomationId(groupsItem, "UserGroupsTab");
        tabs.Items.Add(usersItem);
        tabs.Items.Add(groupsItem);
        if (!session.CanManageUsers) tabs.SelectedItem = groupsItem;
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(tabs);
        Content = root;

        _users.SelectionChanged += (_, _) => UpdateUserButtons();
        _users.MouseDoubleClick += async (_, _) => { if (_editUser.IsEnabled) await EditUser(); };
        _newUser.Click += async (_, _) => await NewUser();
        _editUser.Click += async (_, _) => await EditUser();
        _password.Click += async (_, _) => await SetPassword();
        _lock.Click += async (_, _) => await ToggleLock();
        _deleteUser.Click += async (_, _) => await DeleteUser();
        _groups.SelectionChanged += (_, _) => ShowGroup();
        _members.SelectionChanged += (_, _) => UpdateMemberButtons();
        _newGroup.Click += async (_, _) => await NewGroup();
        _editGroup.Click += async (_, _) => await EditGroup();
        _deleteGroup.Click += async (_, _) => await DeleteGroup();
        _addMembers.Click += async (_, _) => await AddMembers();
        _removeMember.Click += async (_, _) => await RemoveMember();

        // Live: a changed account or group is put in place; the lists are not asked for again.
        // En vivo: la cuenta o el grupo cambiado se pone en su sitio; las listas no se vuelven a pedir.
        EventHandler<UserChangedEventArgs> users = (_, e) => Dispatcher.BeginInvoke(() =>
        {
            _userList.RemoveAll(u => u.Id == e.UserId);
            if (e.User is { } u) _userList.Add(u);
            ShowUsers();
            // Membership is in the accounts too. / La pertenencia también está en las cuentas.
            ShowGroup();
        });
        EventHandler<UserGroupChangedEventArgs> groups = (_, e) => Dispatcher.BeginInvoke(() =>
        {
            _groupList.RemoveAll(g => g.Id == e.GroupId);
            if (e.Group is { } g) _groupList.Add(g);
            ShowGroups();
        });
        session.UserChanged += users;
        session.UserGroupChanged += groups;
        Closed += (_, _) => { session.UserChanged -= users; session.UserGroupChanged -= groups; };
        Loaded += async (_, _) => await Load();
    }

    private static TextBlock Note(string key) => new() { Text = L.T(key), Opacity = 0.65, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };

    private async Task Load() => await Run(async () =>
    {
        if (_session.CanManageUsers) _userList = (await _session.GetUsersAsync()).ToList();
        if (_session.CanManageUserGroups) _groupList = (await _session.GetUserGroupsAsync()).ToList();
        ShowUsers();
        ShowGroups();
        return null;
    });

    private UserInfo? SelectedUser => (_users.SelectedItem as UserRow)?.User;

    private void ShowUsers(string? select = null)
    {
        var keep = select ?? SelectedUser?.Id;
        _users.Items.Clear();
        foreach (var u in _userList.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)) _users.Items.Add(new UserRow(u, _session.User.Id));
        _users.SelectedItem = _users.Items.OfType<UserRow>().FirstOrDefault(r => r.User.Id == keep);
        UpdateUserButtons();
    }

    private void UpdateUserButtons()
    {
        var u = SelectedUser;
        var can = u is not null && _session.CanEditUser(u);
        _newUser.IsEnabled = _session.CanManageUsers;
        _editUser.IsEnabled = can;
        _password.IsEnabled = can;
        // Locking is a server right too: only where this account may change it, and never oneself.
        // Bloquear también es un permiso de servidor: solo donde esta cuenta puede cambiarlo, y nunca a sí misma.
        _lock.IsEnabled = can && MeshServerSession.CanSetSiteRights(_session.User, u!, u!.SiteRights ^ (long)SiteRight.Locked);
        _lock.Content = L.T(u?.IsLocked == true ? "UnlockUser" : "LockUser");
        _deleteUser.IsEnabled = can && u!.Id != _session.User.Id;
    }

    private UserGroup? SelectedGroup => (_groups.SelectedItem as GroupItem)?.Group;

    private void ShowGroups(string? select = null)
    {
        var keep = select ?? SelectedGroup?.Id;
        _groups.Items.Clear();
        foreach (var g in _groupList.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)) _groups.Items.Add(new GroupItem(g));
        _groups.SelectedItem = _groups.Items.OfType<GroupItem>().FirstOrDefault(i => i.Group.Id == keep);
        _groupEmpty.Text = L.T(_groups.Items.Count == 0 ? "NoUserGroups" : "NoUserGroupSelected");
        ShowGroup();
    }

    private void ShowGroup()
    {
        var g = SelectedGroup;
        _groupDetail.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        _groupEmpty.Visibility = g is null ? Visibility.Visible : Visibility.Collapsed;
        if (g is null) return;
        _groupName.Text = g.Name;
        _groupDesc.Text = g.Description ?? "";
        _groupDesc.Visibility = string.IsNullOrEmpty(g.Description) ? Visibility.Collapsed : Visibility.Visible;
        _groupNote.Visibility = g.ManagedElsewhere ? Visibility.Visible : Visibility.Collapsed;
        var keep = (_members.SelectedItem as MemberItem)?.Id;
        _members.Items.Clear();
        foreach (var (id, name) in g.Members.OrderBy(m => m.Value, StringComparer.CurrentCultureIgnoreCase)) _members.Items.Add(new MemberItem(id, name));
        _members.SelectedItem = _members.Items.OfType<MemberItem>().FirstOrDefault(m => m.Id == keep);
        _groupRights.Text = g.Rights.Count == 0 ? L.T("NoGroupRights")
            : string.Join("\n", g.Rights.Select(r => $"{_session.GroupById(r.Key)?.Name ?? L.T("HiddenDeviceGroup")}: {RightsDialog.Summary(r.Value)}"));
        _editGroup.IsEnabled = true;
        _deleteGroup.IsEnabled = true;
        _addMembers.IsEnabled = !g.ManagedElsewhere;
        UpdateMemberButtons();
    }

    private void UpdateMemberButtons() => _removeMember.IsEnabled = SelectedGroup is { ManagedElsewhere: false } && _members.SelectedItem is MemberItem;

    /// <summary>Runs a request; its error goes to the error window with the burburito code. / Hace una petición; su error va a la ventana de error.</summary>
    private async Task Run(Func<Task<string?>> op)
    {
        IsEnabled = false;
        try { if (await op() is { } done) _status.Text = done; }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally { IsEnabled = true; }
    }

    /// <summary>The server's list after a change, so what is shown is what it keeps. / La lista del servidor tras un cambio.</summary>
    private async Task ReloadUsers(string? select)
    {
        _userList = (await _session.GetUsersAsync()).ToList();
        ShowUsers(select);
    }

    private async Task ReloadGroups(string? select)
    {
        _groupList = (await _session.GetUserGroupsAsync()).ToList();
        ShowGroups(select);
    }

    private Task NewUser() => Run(async () =>
    {
        if (UserForm.Ask(this, _session.User, null) is not { } f) return null;
        var id = await _session.CreateUserAsync(new(f.UserName, f.Password!, f.Email, f.RealName, f.SiteRights, f.MustChangePassword));
        await ReloadUsers(id);
        return L.F("UserCreated", f.UserName);
    });

    private Task EditUser() => Run(async () =>
    {
        if (SelectedUser is not { } u || UserForm.Ask(this, _session.User, u) is not { } f) return null;
        await _session.EditUserAsync(u, f.Email ?? "", f.RealName ?? "", f.SiteRights);
        await ReloadUsers(u.Id);
        return L.F("UserSaved", u.Name);
    });

    private Task SetPassword() => Run(async () =>
    {
        if (SelectedUser is not { } u || PasswordForm.Ask(this, u) is not { } p) return null;
        await _session.SetUserPasswordAsync(u, p.Password, p.MustChange, p.RemoveTwoFactor);
        await ReloadUsers(u.Id);
        return L.F("PasswordSet", u.Name);
    });

    private Task ToggleLock() => Run(async () =>
    {
        if (SelectedUser is not { } u) return null;
        await _session.SetLockedAsync(u, !u.IsLocked);
        await ReloadUsers(u.Id);
        return L.F(u.IsLocked ? "UserUnlocked" : "UserLocked", u.Name);
    });

    private Task DeleteUser() => Run(async () =>
    {
        if (SelectedUser is not { } u) return null;
        if (MessageBox.Show(this, L.F("ConfirmDeleteUser", u.Name), L.T("DeleteUser").TrimEnd('…'),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        await _session.DeleteUserAsync(u);
        await ReloadUsers(null);
        return L.F("UserDeleted", u.Name);
    });

    private Task NewGroup() => Run(async () =>
    {
        if (GroupForm.Ask(this, L.T("NewUserGroup").TrimEnd('…'), "", "") is not { } f) return null;
        var id = await _session.CreateUserGroupAsync(f.Name, f.Description);
        await ReloadGroups(id);
        return L.F("UserGroupCreated", f.Name);
    });

    private Task EditGroup() => Run(async () =>
    {
        if (SelectedGroup is not { } g || GroupForm.Ask(this, L.T("EditUserGroup").TrimEnd('…'), g.Name, g.Description ?? "") is not { } f) return null;
        await _session.EditUserGroupAsync(g, f.Name, f.Description);
        await ReloadGroups(g.Id);
        return L.T("UserGroupSaved");
    });

    private Task DeleteGroup() => Run(async () =>
    {
        if (SelectedGroup is not { } g) return null;
        if (MessageBox.Show(this, L.F("ConfirmDeleteUserGroup", g.Name), L.T("DeleteUserGroup").TrimEnd('…'),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        await _session.DeleteUserGroupAsync(g);
        await ReloadGroups(null);
        return L.F("UserGroupDeleted", g.Name);
    });

    private Task AddMembers() => Run(async () =>
    {
        if (SelectedGroup is not { } g || NamesForm.Ask(this, L.F("AddMembersTitle", g.Name)) is not { } names) return null;
        try { await _session.AddUsersToUserGroupAsync(g, names); }
        finally { await ReloadGroups(g.Id); } // some may have been added even when others were not / unos pueden haber entrado aunque otros no
        return L.F("MembersAdded", g.Name);
    });

    private Task RemoveMember() => Run(async () =>
    {
        if (SelectedGroup is not { } g || _members.SelectedItem is not MemberItem m) return null;
        if (MessageBox.Show(this, L.F("ConfirmRemoveMember", m, g.Name), L.T("RemoveGroupMember"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        await _session.RemoveUserFromUserGroupAsync(g, m.Id);
        await ReloadGroups(g.Id);
        return L.F("MemberRemoved", m);
    });
}

/// <summary>A new account, or the editable parts of one. / Una cuenta nueva, o lo que se puede cambiar de una.</summary>
public static class UserForm
{
    public sealed record Result(string UserName, string? Password, string? Email, string? RealName, long SiteRights, bool MustChangePassword);

    public static Result? Ask(Window owner, UserInfo me, UserInfo? existing)
    {
        var isNew = existing is null;
        var userName = new TextBox { Text = existing?.Name ?? "", MaxLength = 64, IsEnabled = isNew, Margin = new Thickness(0, 4, 0, 10), MinWidth = 380 };
        var pass = new PasswordBox { MaxLength = 256, Margin = new Thickness(0, 4, 0, 10) };
        var repeat = new PasswordBox { MaxLength = 256, Margin = new Thickness(0, 4, 0, 10) };
        var email = new TextBox { Text = existing?.Email ?? "", MaxLength = 1024, Margin = new Thickness(0, 4, 0, 10) };
        var realName = new TextBox { Text = existing?.RealName ?? "", MaxLength = 256, Margin = new Thickness(0, 4, 0, 10) };
        var mustChange = new CheckBox { Content = L.T("MustChangePassword"), Margin = new Thickness(0, 0, 0, 10) };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var current = existing?.SiteRights ?? 0;
        var full = new CheckBox { Content = L.T("SiteFull"), IsChecked = current == (long)SiteRight.FullAdministrator, Margin = new Thickness(0, 2, 0, 2) };
        AP.SetAutomationId(full, "SiteFull");
        var boxes = SiteRightsText.Items.Select(i =>
        {
            var b = new CheckBox { Content = L.T(i.Key), Tag = i.Right, Margin = new Thickness(0, 2, 0, 2), IsChecked = current != (long)SiteRight.FullAdministrator && (current & (long)i.Right) != 0 };
            AP.SetAutomationId(b, "Site" + i.Right);
            return b;
        }).ToList();
        long Chosen() => full.IsChecked == true ? (long)SiteRight.FullAdministrator
            : boxes.Where(b => b.IsChecked == true).Aggregate(0L, (a, b) => a | (long)(SiteRight)b.Tag);
        // Only what this account may give: a full administrator anything; otherwise what the server would really change.
        // Solo lo que esta cuenta puede dar: un administrador completo, todo; si no, lo que el servidor cambiaría de verdad.
        bool May(long rights) => isNew ? MeshServerSession.CanGiveNewUserRights(me, rights) : rights == existing!.SiteRights || MeshServerSession.CanSetSiteRights(me, existing, rights);
        void Gate()
        {
            var now = Chosen();
            full.IsEnabled = me.IsFullAdministrator && existing?.Id != me.Id;
            foreach (var b in boxes) b.IsEnabled = full.IsChecked != true && May(now ^ (long)(SiteRight)b.Tag);
        }
        full.Checked += (_, _) => Gate(); full.Unchecked += (_, _) => Gate();
        foreach (var b in boxes) { b.Checked += (_, _) => Gate(); b.Unchecked += (_, _) => Gate(); }
        Gate();

        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        foreach (var (e, id) in new (FrameworkElement, string)[] { (userName, "UserNameBox"), (pass, "UserPasswordBox"), (repeat, "UserRepeatBox"), (email, "UserEmailBox"), (realName, "UserRealNameBox"), (mustChange, "MustChangeBox"), (ok, "UserFormOk") })
            AP.SetAutomationId(e, id);
        // Two fixed columns: a wrap panel made a third one that the dialog cut off (CI screenshot 08-11).
        // Dos columnas fijas: un panel que se reparte solo hizo una tercera que el diálogo cortaba.
        var rights = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        rights.Children.Add(full);
        foreach (var b in boxes) rights.Children.Add(b);

        var form = new StackPanel { Margin = new Thickness(20), MaxWidth = 560 };
        form.Children.Add(new TextBlock { Text = L.T("UserFormUserName") }); form.Children.Add(userName);
        if (isNew)
        {
            form.Children.Add(new TextBlock { Text = L.T("Password") }); form.Children.Add(pass);
            form.Children.Add(new TextBlock { Text = L.T("RepeatPassword") }); form.Children.Add(repeat);
            form.Children.Add(mustChange);
        }
        form.Children.Add(new TextBlock { Text = L.T("UserFormEmail") }); form.Children.Add(email);
        form.Children.Add(new TextBlock { Text = L.T("UserFormRealName") }); form.Children.Add(realName);
        form.Children.Add(new TextBlock { Text = L.T("SiteRightsSection"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        form.Children.Add(rights);
        form.Children.Add(error);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        var w = new Window
        {
            Title = isNew ? L.T("NewUser").TrimEnd('…') : existing!.Name, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 },
        };
        AP.SetAutomationId(w, "UserForm");
        Result? result = null;
        ok.Click += (_, _) =>
        {
            if (isNew && (userName.Text.Trim().Length == 0 || pass.Password.Length == 0)) { error.Text = L.T("UserFormUserName"); return; }
            if (isNew && pass.Password != repeat.Password) { error.Text = L.T("PasswordsDiffer"); return; }
            result = new(userName.Text.Trim(), isNew ? pass.Password : null, email.Text.Trim(), realName.Text.Trim(), Chosen(), mustChange.IsChecked == true);
            w.Close();
        };
        w.Loaded += (_, _) => (isNew ? userName : (Control)email).Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>Another account's new password. / La contraseña nueva de otra cuenta.</summary>
public static class PasswordForm
{
    public sealed record Result(string Password, bool MustChange, bool RemoveTwoFactor);

    public static Result? Ask(Window owner, UserInfo user)
    {
        var pass = new PasswordBox { MaxLength = 256, Margin = new Thickness(0, 4, 0, 10), MinWidth = 340 };
        var repeat = new PasswordBox { MaxLength = 256, Margin = new Thickness(0, 4, 0, 10) };
        var mustChange = new CheckBox { Content = L.T("MustChangePassword"), Margin = new Thickness(0, 0, 0, 6) };
        var removeTwo = new CheckBox { Content = L.T("RemoveTwoFactor"), Visibility = user.HasTwoFactor ? Visibility.Visible : Visibility.Collapsed };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        foreach (var (e, id) in new (FrameworkElement, string)[] { (pass, "NewPasswordBox"), (repeat, "RepeatPasswordBox"), (mustChange, "MustChangeBox"), (removeTwo, "RemoveTwoFactorBox"), (ok, "PasswordFormOk") })
            AP.SetAutomationId(e, id);
        var w = new Window
        {
            Title = L.F("SetPasswordTitle", user.Name), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.T("NewPassword") }, pass, new TextBlock { Text = L.T("RepeatPassword") }, repeat, mustChange, removeTwo, error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "PasswordForm");
        Result? result = null;
        ok.Click += (_, _) =>
        {
            if (pass.Password.Length == 0 || pass.Password != repeat.Password) { error.Text = L.T("PasswordsDiffer"); return; }
            result = new(pass.Password, mustChange.IsChecked == true, removeTwo.IsChecked == true);
            w.Close();
        };
        w.Loaded += (_, _) => pass.Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>User names separated by commas. / Nombres de usuario separados por comas.</summary>
public static class NamesForm
{
    public static IReadOnlyList<string>? Ask(Window owner, string title)
    {
        var box = new TextBox { MinWidth = 380, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(box, "NamesBox"); AP.SetAutomationId(ok, "NamesOk");
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim().Length > 0;
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.T("UserNamesLabel"), TextWrapping = TextWrapping.Wrap }, box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "NamesForm");
        IReadOnlyList<string>? result = null;
        ok.Click += (_, _) => { result = box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); w.Close(); };
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>Chooses one item of a list. / Elige un elemento de una lista.</summary>
public static class PickDialog
{
    private sealed class Item<T>(T value, string name)
    {
        public T Value { get; } = value;
        public override string ToString() => name;
    }

    public static T? Ask<T>(Window owner, string title, IEnumerable<T> items, Func<T, string> name) where T : class
    {
        var list = new ListBox { MinWidth = 340, MinHeight = 140, MaxHeight = 320 };
        foreach (var i in items) list.Items.Add(new Item<T>(i, name(i)));
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(list, "PickList"); AP.SetAutomationId(ok, "PickOk");
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children = { list, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } } },
            },
        };
        AP.SetAutomationId(w, "PickDialog");
        T? result = null;
        ok.Click += (_, _) => { result = (list.SelectedItem as Item<T>)?.Value; w.Close(); };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Item<T> i) { result = i.Value; w.Close(); } };
        w.Loaded += (_, _) => { list.Focus(); if (list.Items.Count == 1) list.SelectedIndex = 0; };
        w.ShowDialog();
        return result;
    }
}
