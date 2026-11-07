using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using MeshCentralClient.Core.Updates;

namespace MeshCentralClient.App;

public partial class MainWindow : Window
{
    private readonly SavedServers _saved = new();
    private readonly ObservableCollection<DeviceRow> _rows = new();
    private readonly Dictionary<string, string> _groupNames = new();
    private ICollectionView? _view;
    private MeshServerSession? _session;
    private CancellationTokenSource? _connectCts;
    private UserError? _shownError;
    private bool _emailCodeAvailable;
    private readonly List<Window> _sessionWindows = new();
    private static readonly Version AppVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private AvailableUpdate? _update;

    public MainWindow()
    {
        InitializeComponent();
        ColName.Header = L.T("ColName");
        ColOs.Header = L.T("ColOs");
        ColAddress.Header = L.T("ColAddress");
        ColTags.Header = L.T("ColTags");

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRow.GroupName)));
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.GroupName), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.Name), ListSortDirection.Ascending));
        DeviceList.ItemsSource = _view;

        ThemeBox.ItemsSource = new[] { L.T("ThemeSystem"), L.T("ThemeLight"), L.T("ThemeDark") };
        ThemeBox.SelectedIndex = (int)ThemeService.Current;

        Loaded += (_, _) =>
        {
            var problem = _saved.Load();
            RefreshSavedList();
            if (_saved.Items.Count > 0) ServerBox.SelectedIndex = 0;
            if (problem is not null) ShowError(problem);
            (string.IsNullOrEmpty(ServerBox.Text) ? (Control)ServerBox : PasswordBox).Focus();
            VersionText.Text = L.F("Version", AppVersion.ToString(3));
            _ = CheckForUpdateAsync();
        };
        Closing += async (_, _) => { if (_session is not null) await _session.DisposeAsync(); };
    }

    // ---------------- Login / Entrada ----------------

    private void RefreshSavedList()
    {
        ServerBox.ItemsSource = null;
        ServerBox.ItemsSource = _saved.Items.OrderByDescending(s => s.LastUsed).ToList();
    }

    private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerBox.SelectedItem is not SavedServer s) { ForgetButton.Visibility = Visibility.Collapsed; return; }
        // Show the address, not "user @ address", in the editable box. / En la caja se ve la dirección, no «usuario @ dirección».
        Dispatcher.BeginInvoke(() => ServerBox.Text = s.Address);
        UserBox.Text = s.Username;
        var pw = s.ReadPassword();
        PasswordBox.Password = pw ?? "";
        RememberBox.IsChecked = pw is not null;
        ForgetButton.Visibility = Visibility.Visible;
        HideTwoFactor();
        _ = DiscoverSsoAsync(s.Address);
    }

    // ---------------- Single sign-on / Inicio de sesión único ----------------

    private IReadOnlyList<SingleSignOn> _sso = Array.Empty<SingleSignOn>();
    private string? _ssoFor;

    private void ServerBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => _ = DiscoverSsoAsync(ServerBox.Text);

    /// <summary>Shows the buttons of the single sign-on the server offers; silent if it cannot tell.
    /// Enseña los botones del inicio único que ofrece el servidor; calla si no lo puede saber.</summary>
    private async Task DiscoverSsoAsync(string text)
    {
        if (!ServerAddress.TryParse(text, out var address)) { ShowSso(Array.Empty<SingleSignOn>(), null); return; }
        var key = address!.ToString();
        if (key == _ssoFor) return;
        var found = await SignInMethods.DiscoverAsync(address, new CertificateTrust(_saved.PinFor(key)));
        if (ServerAddress.TryParse(ServerBox.Text, out var now) && now!.ToString() == key) ShowSso(found, key);
    }

    private void ShowSso(IReadOnlyList<SingleSignOn> methods, string? key)
    {
        _sso = methods;
        _ssoFor = key;
        SsoButtons.Children.Clear();
        foreach (var m in methods)
        {
            var b = new Button { Content = L.F("SignInWith", m.DisplayName), Margin = new Thickness(0, 0, 8, 8), MinWidth = 160 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "Sso_" + m.Strategy);
            b.Click += async (_, _) => await SignInWithSsoAsync(m);
            SsoButtons.Children.Add(b);
        }
        SsoPanel.Visibility = methods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Single sign-on: <paramref name="method"/> null means "whatever the server offers" (Connect with no password).
    /// Inicio de sesión único: <paramref name="method"/> nulo es «lo que ofrezca el servidor» (Conectar sin contraseña).
    /// </summary>
    private async Task SignInWithSsoAsync(SingleSignOn? method)
    {
        HideError();
        if (!ServerAddress.TryParse(ServerBox.Text, out var address))
        {
            ShowError(UserError.From(new MeshClientException(MeshFailure.InvalidAddress, "conexion/iniciar-sesion/dato-invalido", "Invalid server address.")));
            return;
        }
        var key = address!.ToString();
        SetBusy(true);
        try
        {
            var trust = new CertificateTrust(_saved.PinFor(key));
            if (method is null)
            {
                var found = await SignInMethods.DiscoverAsync(address, trust);
                if (found.Count == 0 && trust.RejectedSha256 is { } fp)
                {
                    // A server with its own certificate: the same question as with a password. / Un servidor con certificado propio: la misma pregunta que con contraseña.
                    if (!CertificateWindow.Ask(this, address.Host, trust.RejectedSubject, fp, trust.PinnedSha256 is not null))
                    {
                        ShowError(UserError.From(new MeshClientException(MeshFailure.UntrustedCertificate, "conexion/iniciar-sesion/tls", "Certificate not accepted.")));
                        return;
                    }
                    SavePin(key, UserBox.Text.Trim(), fp);
                    trust = new CertificateTrust(fp);
                    found = await SignInMethods.DiscoverAsync(address, trust);
                }
                ShowSso(found, key);
                if (found.Count == 0) { ShowError(UserError.NeedPassword()); PasswordBox.Focus(); return; }
                method = found[0];
            }
            var w = new SsoWindow(this, address, method, trust);
            w.ShowDialog();
            if (w.Session is { } session)
            {
                var saved = _saved.Upsert(key, session.User.Name);
                saved.SingleSignOn = method.Strategy;
                saved.CertificateSha256 = trust.PinnedSha256 ?? saved.CertificateSha256;
                // The entry with no user, made only to keep the certificate before signing in, is not needed any more.
                // La entrada sin usuario, hecha solo para guardar el certificado antes de entrar, ya no hace falta.
                _saved.Items.RemoveAll(i => !ReferenceEquals(i, saved) && i.Address == key && i.Username.Length == 0);
                saved.WritePassword(null);
                if (_saved.Save() is { } problem) ErrorWindow.Show(this, problem);
                RefreshSavedList();
                await OpenSessionAsync(session);
            }
            else if (w.Failure is { } f)
            {
                ShowError(f.BurburitoKey == "app/abrir-detalle/dependencia-caida" ? UserError.MissingWebView(f) : UserError.From(f));
            }
            // closed by the person: nothing to say / la cerró la persona: nada que decir
        }
        finally { SetBusy(false); }
    }

    private void SavePin(string key, string user, string fp)
    {
        _saved.Upsert(key, user);
        foreach (var s in _saved.Items.Where(i => i.Address == key)) s.CertificateSha256 = fp;
        if (_saved.Save() is { } problem) ErrorWindow.Show(this, problem);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _connectCts?.Cancel();

    private async void EmailCodeButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync(emailCode: true);

    private void ForgetButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServerBox.SelectedItem is not SavedServer s) return;
        _saved.Items.Remove(s);
        if (_saved.Save() is { } problem) ShowError(problem);
        // Also what the Windows browser keeps of a single sign-on there. / También lo que guarda el navegador de Windows de un inicio único allí.
        if (s.SingleSignOn is not null && ServerAddress.TryParse(s.Address, out var forgotten)) _ = SsoWindow.ForgetAsync(forgotten!);
        RefreshSavedList();
        ServerBox.Text = ""; UserBox.Text = ""; PasswordBox.Password = ""; RememberBox.IsChecked = false;
        ForgetButton.Visibility = Visibility.Collapsed;
    }

    private async Task ConnectAsync(bool emailCode = false)
    {
        HideError();
        if (!ServerAddress.TryParse(ServerBox.Text, out var address))
        {
            ShowError(UserError.From(new MeshClientException(MeshFailure.InvalidAddress, "conexion/iniciar-sesion/dato-invalido", "Invalid server address.")));
            ServerBox.Focus();
            return;
        }
        var user = UserBox.Text.Trim();
        var password = PasswordBox.Password;
        // No password: single sign-on. A password login always needs one. / Sin contraseña: inicio único. Entrar con contraseña siempre la necesita.
        if (password.Length == 0 && !emailCode && TwoFactorPanel.Visibility != Visibility.Visible) { await SignInWithSsoAsync(null); return; }
        var code = emailCode ? "**email**" : (TwoFactorPanel.Visibility == Visibility.Visible ? CodeBox.Text.Trim() : null);
        if (code is { Length: 0 }) code = null;
        var key = address!.ToString();

        SetBusy(true);
        _connectCts = new CancellationTokenSource();
        try
        {
            var trust = new CertificateTrust(_saved.PinFor(key));
            MeshServerSession session;
            try
            {
                session = await MeshServerSession.ConnectAsync(address, new PasswordCredentials(user, password, code), trust, _connectCts.Token);
            }
            catch (MeshClientException ex) when (ex.Failure == MeshFailure.UntrustedCertificate && ex.CertificateSha256 is not null)
            {
                var changed = trust.PinnedSha256 is not null;
                if (!CertificateWindow.Ask(this, address.Host, ex.CertificateSubject, ex.CertificateSha256, changed)) { ShowError(UserError.From(ex)); return; }
                trust = new CertificateTrust(ex.CertificateSha256);
                // Saved now, so a second-factor round trip does not ask again. / Se guarda ya, para que la vuelta del 2FA no lo vuelva a preguntar.
                _saved.Upsert(key, user);
                foreach (var s in _saved.Items.Where(i => i.Address == key)) s.CertificateSha256 = trust.PinnedSha256;
                if (_saved.Save() is { } pinProblem) ErrorWindow.Show(this, pinProblem);
                session = await MeshServerSession.ConnectAsync(address, new PasswordCredentials(user, password, code), trust, _connectCts.Token);
            }

            var saved = _saved.Upsert(key, user);
            saved.WritePassword(RememberBox.IsChecked == true ? password : null);
            if (_saved.Save() is { } problem) ErrorWindow.Show(this, problem);

            HideTwoFactor();
            await OpenSessionAsync(session);
        }
        catch (MeshClientException ex) when (ex.Failure == MeshFailure.TwoFactorRequired)
        {
            ShowTwoFactor(ex.EmailCodeAvailable, ex.EmailCodeSent);
        }
        catch (MeshClientException ex)
        {
            if (ex.Failure == MeshFailure.InvalidTwoFactorCode) { CodeBox.SelectAll(); CodeBox.Focus(); }
            ShowError(UserError.From(ex));
        }
        catch (OperationCanceledException) { /* the person cancelled / lo canceló la persona */ }
        finally
        {
            SetBusy(false);
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        ConnectButton.Content = L.T(busy ? "Connecting" : "Connect");
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ServerBox.IsEnabled = UserBox.IsEnabled = PasswordBox.IsEnabled = !busy;
    }

    private void ShowTwoFactor(bool emailAvailable, bool emailSent)
    {
        _emailCodeAvailable |= emailAvailable;
        TwoFactorPanel.Visibility = Visibility.Visible;
        TwoFactorHelp.Text = L.T(emailSent ? "TwoFactorEmailSent" : "TwoFactorHelp");
        EmailCodeButton.Visibility = _emailCodeAvailable ? Visibility.Visible : Visibility.Collapsed;
        CodeBox.Text = "";
        CodeBox.Focus();
    }

    private void HideTwoFactor()
    {
        _emailCodeAvailable = false;
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        CodeBox.Text = "";
    }

    private void ShowError(UserError error)
    {
        _shownError = error;
        ErrorText.Text = error.Message;
        ErrorAct.Text = error.ActLine;
        ErrorFooter.Text = error.Footer;
        ErrorTech.Text = error.Technical;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() { ErrorPanel.Visibility = Visibility.Collapsed; _shownError = null; }

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (_shownError is null) return;
        try { Clipboard.SetText(_shownError.CopyText); } catch (Exception) { }
    }

    // ---------------- Updates / Actualizaciones ----------------

    private static readonly System.Net.Http.HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    private async Task CheckForUpdateAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        _ = SelfUpdater.CleanUpAsync(exe); // what a previous update left / lo que dejó una actualización anterior
        var feed = UpdateChecker.FeedFromEnvironment(Environment.GetEnvironmentVariable("MESHCLIENT_UPDATE_FEED"));
        if (feed is null) return;
        try
        {
            _update = await new UpdateChecker(UpdateHttp, feed).CheckAsync(AppVersion);
        }
        catch (MeshClientException) { return; } // checking is not worth an error: next start / buscar no merece un error: al siguiente arranque
        if (_update is null) return;
        UpdateText.Text = L.F("UpdateAvailable", _update.Version.ToString(3));
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } u || Environment.ProcessPath is not { } exe) return;
        UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = false;
        UpdateText.Text = L.F("UpdateDownloading", u.Version.ToString(3));
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var downloaded = await SelfUpdater.DownloadAsync(UpdateHttp, u, exe, new Progress<double>(p => UpdateProgress.Value = p));
            await CloseSessionAsync();
            SelfUpdater.Swap(exe, downloaded);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe)! });
            Application.Current.Shutdown();
        }
        catch (MeshClientException ex)
        {
            UpdateText.Text = L.F("UpdateAvailable", u.Version.ToString(3));
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = true;
            ErrorWindow.Show(this, UserError.Update(ex, u.PageUrl));
        }
    }

    // ---------------- Devices / Equipos ----------------

    private async Task OpenSessionAsync(MeshServerSession session)
    {
        _session = session;
        session.DeviceChanged += (_, e) => Dispatcher.BeginInvoke(() => ApplyChange(session, e));
        session.Closed += (_, e) => Dispatcher.BeginInvoke(() => OnSessionLost(session, e));
        SessionTitle.Text = string.IsNullOrEmpty(session.ServerInfo.Name) ? session.Server.Host : session.ServerInfo.Name;
        SessionSubtitle.Text = $"{session.User.Name} · {session.Server}";
        Title = $"{SessionTitle.Text} — {L.T("AppTitle")}";
        LostBanner.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Visible;
        SearchBox.Text = "";
        await LoadDevicesAsync();
        SearchBox.Focus();
    }

    private async Task LoadDevicesAsync()
    {
        if (_session is not { } s) return;
        try
        {
            var groups = await s.GetDeviceGroupsAsync();
            var devices = await s.GetDevicesAsync();
            if (!ReferenceEquals(s, _session)) return;
            _groupNames.Clear();
            foreach (var g in groups) _groupNames[g.Id] = g.Name;
            _rows.Clear();
            foreach (var d in devices) _rows.Add(new DeviceRow(d, GroupName(d.GroupId)));
            UpdateCount();
        }
        catch (MeshClientException ex)
        {
            ErrorWindow.Show(this, UserError.From(ex, s.ServerInfo.Version));
        }
    }

    private string GroupName(string id) => _groupNames.TryGetValue(id, out var n) ? n : id;

    private void ApplyChange(MeshServerSession from, DeviceChangedEventArgs e)
    {
        if (!ReferenceEquals(from, _session)) return;
        var row = _rows.FirstOrDefault(r => r.Id == e.DeviceId);
        switch (e.Kind)
        {
            case DeviceChangeKind.Removed:
                if (row is not null) _rows.Remove(row);
                break;
            case DeviceChangeKind.ConnectionChanged:
                if (row is not null) row.Device = row.Device with { Connectivity = e.Device!.Connectivity, PowerState = e.Device.PowerState };
                break;
            case DeviceChangeKind.Added:
            case DeviceChangeKind.Changed:
                if (row is null)
                {
                    // A device in a group we did not know: reload groups and devices once.
                    // Un equipo de un grupo que no conocíamos: se recargan grupos y equipos una vez.
                    if (!_groupNames.ContainsKey(e.Device!.GroupId)) { _ = LoadDevicesAsync(); return; }
                    _rows.Add(new DeviceRow(e.Device, GroupName(e.Device.GroupId)));
                }
                else
                {
                    var merged = e.Kind == DeviceChangeKind.Changed ? row.Device.WithDetails(e.Device!) : e.Device!;
                    row.Device = merged;
                    row.GroupName = GroupName(merged.GroupId);
                }
                break;
        }
        _view?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText.Text = L.F("DevicesCount", _rows.Count, _rows.Count(r => r.IsOnline));
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSessionLost(MeshServerSession from, MeshClientException reason)
    {
        if (!ReferenceEquals(from, _session)) return;
        var err = UserError.From(reason, from.ServerInfo.Version);
        LostText.Text = $"{err.Message}  {err.Footer}";
        LostBanner.Visibility = Visibility.Visible;
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        await CloseSessionAsync();
        await ConnectAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var f = SearchBox.Text.Trim();
        _view.Filter = f.Length == 0 ? null : o => o is DeviceRow r && r.Matches(f);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await CloseSessionAsync();
        if (RememberBox.IsChecked != true) PasswordBox.Password = "";
    }

    private async Task CloseSessionAsync()
    {
        var s = _session;
        _session = null;
        // Their tunnels belong to this session. / Sus túneles son de esta sesión.
        foreach (var w in _sessionWindows.ToList()) w.Close();
        _rows.Clear();
        _groupNames.Clear();
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        Title = L.T("AppTitle");
        if (s is not null) await s.DisposeAsync();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedIndex >= 0 && (AppTheme)ThemeBox.SelectedIndex != ThemeService.Current)
            ThemeService.Apply((AppTheme)ThemeBox.SelectedIndex);
    }

    // Double click: a terminal if the device can, otherwise its page in the browser.
    // Doble clic: una terminal si el equipo puede; si no, su página en el navegador.
    private void DeviceList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceRow { CanOpenTerminal: true }) OpenTerminal_Click(sender, e);
        else OpenInBrowser_Click(sender, e);
    }

    private void DeviceMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = DeviceList.SelectedItem as DeviceRow;
        TerminalItem.IsEnabled = row is { CanOpenTerminal: true };
        PowerShellItem.Visibility = row is { IsWindows: true } ? Visibility.Visible : Visibility.Collapsed;
        PowerShellItem.IsEnabled = row is { CanOpenTerminal: true };
        FilesItem.IsEnabled = row is { CanOpenFiles: true };
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) => OpenTerminal(TunnelProtocol.Terminal);

    private void OpenPowerShell_Click(object sender, RoutedEventArgs e) => OpenTerminal(TunnelProtocol.PowerShell);

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row || _session is null || !row.CanOpenFiles) return;
        var w = new FilesWindow(_session, row);
        _sessionWindows.Add(w);
        w.Closed += (_, _) => _sessionWindows.Remove(w);
        w.Show();
    }

    private void OpenTerminal(TunnelProtocol kind)
    {
        if (DeviceList.SelectedItem is not DeviceRow row || _session is null || !row.CanOpenTerminal) return;
        var w = new TerminalWindow(_session, row, kind);
        _sessionWindows.Add(w);
        w.Closed += (_, _) => _sessionWindows.Remove(w);
        w.Show();
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row || _session is null) return;
        var url = new Uri(_session.Server.BaseUri, "?viewmode=10&gotonode=" + DeviceLinks.HexNodeId(row.Id));
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorWindow.Show(this, UserError.Unexpected(ex)); }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceRow row) { try { Clipboard.SetText(row.Name); } catch (Exception) { } }
    }
}
