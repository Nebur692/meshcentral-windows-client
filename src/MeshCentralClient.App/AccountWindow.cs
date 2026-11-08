using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// The signed-in account's own settings: password, authenticator app, backup codes, security keys and login tokens.
/// It follows the account live (a change made on the website shows here at once).
/// Los ajustes de la propia cuenta: contraseña, app de autenticación, códigos de respaldo, llaves de seguridad y tokens de
/// acceso. Sigue a la cuenta en vivo (un cambio hecho en la web se ve aquí enseguida).
/// </summary>
public sealed class AccountWindow : Window
{
    public sealed class TokenRow(LoginToken t)
    {
        public LoginToken Token { get; } = t;
        public string Name => Token.Name;
        public string User => Token.TokenUser;
        public string Expires => Token.Expires?.LocalDateTime.ToString("g") ?? L.T("ExpireNever");
        public override string ToString() => Name;
    }

    private sealed class KeyItem(SecurityKey k)
    {
        public SecurityKey Key { get; } = k;
        public override string ToString() => string.IsNullOrEmpty(Key.Type) ? Key.Name : $"{Key.Name} ({Key.Type})";
    }

    private static readonly (int Hours, string Key)[] TokenLifetimes = { (0, "ExpireNever"), (1, "Expire1h"), (24, "Expire1d"), (168, "Expire1w"), (720, "Expire30d") };

    private readonly MeshServerSession _session;
    private readonly TextBlock _who = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _facts = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _locked = new() { Text = L.T("SettingsLocked"), Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly PasswordBox _current = new() { MaxLength = 256, Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly PasswordBox _new = new() { MaxLength = 256, Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly PasswordBox _repeat = new() { MaxLength = 256, Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _change = new() { Content = L.T("ChangePassword"), MinWidth = 140 };
    private readonly TextBlock _authState = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _addAuth = new() { Content = L.T("AddAuthenticator"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _removeAuth = new() { Content = L.T("RemoveAuthenticator"), MinWidth = 90 };
    private readonly TextBlock _codesState = new() { Opacity = 0.8 };
    private readonly Button _showCodes = new() { Content = L.T("ShowCodes"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _newCodes = new() { Content = L.T("NewCodes"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _clearCodes = new() { Content = L.T("ClearCodes"), MinWidth = 110 };
    private readonly TextBox _codes = new() { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), AcceptsReturn = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _keys = new() { MinHeight = 50, MaxHeight = 120, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _removeKey = new() { Content = L.T("RemoveItem"), MinWidth = 90 };
    private readonly ListView _tokens = new() { MinHeight = 70, MaxHeight = 180, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _newToken = new() { Content = L.T("NewToken"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _removeToken = new() { Content = L.T("RemoveItem"), MinWidth = 90 };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 10), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 };

    public AccountWindow(MeshServerSession session)
    {
        _session = session;
        Title = L.F("AccountTitle", session.User.Name);
        Width = Math.Min(760, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(560, Width); MinHeight = Math.Min(420, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "AccountWindow"), (_who, "AccountName"), (_current, "CurrentPasswordBox"), (_new, "NewPasswordBox"), (_repeat, "RepeatPasswordBox"),
            (_change, "ChangePasswordButton"), (_authState, "AuthenticatorState"), (_addAuth, "AddAuthenticatorButton"), (_removeAuth, "RemoveAuthenticatorButton"),
            (_codesState, "CodesState"), (_showCodes, "ShowCodesButton"), (_newCodes, "NewCodesButton"), (_clearCodes, "ClearCodesButton"), (_codes, "CodesBox"),
            (_keys, "KeyList"), (_removeKey, "RemoveKeyButton"), (_tokens, "TokenList"), (_newToken, "NewTokenButton"), (_removeToken, "RemoveTokenButton"),
            (_status, "AccountStatus"),
        }) AP.SetAutomationId(e, id);
        AP.SetName(_current, L.T("CurrentPassword")); AP.SetName(_new, L.T("NewPassword")); AP.SetName(_repeat, L.T("RepeatPassword"));

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = L.T("TokenName"), Width = 200, DisplayMemberBinding = new Binding(nameof(TokenRow.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("TokenUser"), Width = 220, DisplayMemberBinding = new Binding(nameof(TokenRow.User)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("TokenExpiry"), Width = 150, DisplayMemberBinding = new Binding(nameof(TokenRow.Expires)) });
        _tokens.View = grid;

        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 22, 0, 2) };
        static TextBlock Help(string key) => new() { Text = L.T(key), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) }; foreach (var i in items) p.Children.Add(i); return p; }
        static StackPanel Labeled(string key, Control box) => new() { Children = { new TextBlock { Text = L.T(key), Opacity = 0.8 }, box } };

        var page = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };
        page.Children.Add(_who);
        page.Children.Add(_facts);
        page.Children.Add(_locked);
        page.Children.Add(Section("PasswordSection"));
        var pw = new WrapPanel();
        pw.Children.Add(Labeled("CurrentPassword", _current));
        pw.Children.Add(Labeled("NewPassword", _new));
        pw.Children.Add(Labeled("RepeatPassword", _repeat));
        page.Children.Add(pw);
        page.Children.Add(Row(_change));
        page.Children.Add(Section("AuthenticatorSection"));
        page.Children.Add(Row(_authState, _addAuth, _removeAuth));
        page.Children.Add(Section("BackupSection"));
        page.Children.Add(Help("BackupHelp"));
        page.Children.Add(_codesState);
        page.Children.Add(Row(_showCodes, _newCodes, _clearCodes));
        page.Children.Add(_codes);
        page.Children.Add(Section("KeysSection"));
        page.Children.Add(Help("KeysHelp"));
        page.Children.Add(_keys);
        page.Children.Add(Row(_removeKey));
        page.Children.Add(Section("TokensSection"));
        page.Children.Add(Help("TokensHelp"));
        page.Children.Add(_tokens);
        page.Children.Add(Row(_newToken, _removeToken));

        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _change.Click += async (_, _) => await ChangePassword();
        _addAuth.Click += async (_, _) => await AddAuthenticator();
        _removeAuth.Click += async (_, _) => await RemoveAuthenticator();
        _showCodes.Click += async (_, _) => await Codes(BackupCodeAction.Show);
        _newCodes.Click += async (_, _) => await Codes(BackupCodeAction.New);
        _clearCodes.Click += async (_, _) => await Codes(BackupCodeAction.Clear);
        _keys.SelectionChanged += (_, _) => ShowState();
        _removeKey.Click += async (_, _) => await RemoveKey();
        _tokens.SelectionChanged += (_, _) => ShowState();
        _newToken.Click += async (_, _) => await NewToken();
        _removeToken.Click += async (_, _) => await RemoveToken();

        EventHandler<UserChangedEventArgs> mine = (_, e) => { if (e.UserId == _session.User.Id) Dispatcher.BeginInvoke(ShowState); };
        session.UserChanged += mine;
        Closed += (_, _) => session.UserChanged -= mine;
        ShowState();
        Loaded += async (_, _) => await Run(async () => { await LoadKeys(); await LoadTokens(); return null; });
    }

    private void ShowState()
    {
        var u = _session.User;
        var locked = _session.OwnSettingsLocked;
        _who.Text = string.IsNullOrEmpty(u.RealName) ? u.Name : $"{u.RealName} ({u.Name})";
        _facts.Text = string.Join(" · ", new[] { u.Email, u.SiteRights == 0 ? null : SiteRightsText.Summary(u.SiteRights) }.Where(x => !string.IsNullOrEmpty(x)));
        _locked.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        _change.IsEnabled = !locked;
        _authState.Text = L.T(u.HasAuthenticator ? "AuthenticatorOn" : "AuthenticatorOff");
        _addAuth.IsEnabled = !locked && !u.HasAuthenticator;
        _removeAuth.IsEnabled = !locked && u.HasAuthenticator;
        _codesState.Text = L.T(!u.HasTwoFactor ? "BackupNeedsFactor" : u.HasBackupCodes ? "CodesThere" : "NoCodes");
        _showCodes.IsEnabled = u.HasTwoFactor;
        _newCodes.IsEnabled = !locked && u.HasTwoFactor;
        _clearCodes.IsEnabled = !locked && u.HasTwoFactor && u.HasBackupCodes;
        if (!u.HasTwoFactor) { _codes.Text = ""; _codes.Visibility = Visibility.Collapsed; }
        _removeKey.IsEnabled = !locked && _keys.SelectedItem is KeyItem;
        _removeToken.IsEnabled = _tokens.SelectedItem is TokenRow;
    }

    private async Task Run(Func<Task<string?>> op)
    {
        IsEnabled = false;
        try { if (await op() is { } done) _status.Text = done; }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally { IsEnabled = true; ShowState(); }
    }

    private bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private Task ChangePassword() => Run(async () =>
    {
        if (_new.Password != _repeat.Password) { _status.Text = L.T("PasswordsDiffer"); return null; }
        await _session.ChangePasswordAsync(_current.Password, _new.Password);
        _current.Clear(); _new.Clear(); _repeat.Clear();
        return L.T("PasswordChanged");
    });

    private Task AddAuthenticator() => Run(async () =>
    {
        var setup = await _session.StartAuthenticatorAsync();
        // The dialog checks each code with the server, so a wrong one is told there and can be typed again.
        // El diálogo comprueba cada código con el servidor: uno malo se dice allí y se puede volver a escribir.
        if (!AuthenticatorDialog.Ask(this, setup, code => _session.ConfirmAuthenticatorAsync(setup, code), _session.ServerInfo.Version)) return null;
        return L.T("AuthenticatorAdded");
    });

    private Task RemoveAuthenticator() => Run(async () =>
    {
        if (!Confirm(L.T("ConfirmRemoveAuthenticator"), L.T("AuthenticatorSection"))) return null;
        await _session.RemoveAuthenticatorAsync();
        return L.T("AuthenticatorRemoved");
    });

    private Task Codes(BackupCodeAction action) => Run(async () =>
    {
        if (action == BackupCodeAction.New && _session.User.HasBackupCodes && !Confirm(L.T("ConfirmNewCodes"), L.T("BackupSection"))) return null;
        if (action == BackupCodeAction.Clear && !Confirm(L.T("ConfirmClearCodes"), L.T("BackupSection"))) return null;
        var codes = await _session.BackupCodesAsync(action);
        _codes.Text = string.Join("\n", codes.Select(c => c.Used ? L.F("CodeUsed", c.Code) : c.Code));
        _codes.Visibility = codes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return action == BackupCodeAction.Clear ? L.T("CodesCleared") : null;
    });

    private async Task LoadKeys()
    {
        _keys.Items.Clear();
        foreach (var k in await _session.GetSecurityKeysAsync()) _keys.Items.Add(new KeyItem(k));
        if (_keys.Items.Count == 0) _keys.Items.Add(new ListBoxItem { Content = L.T("NoKeys"), IsEnabled = false });
    }

    private Task RemoveKey() => Run(async () =>
    {
        if (_keys.SelectedItem is not KeyItem k || !Confirm(L.F("ConfirmRemoveKey", k), L.T("KeysSection"))) return null;
        await _session.RemoveSecurityKeyAsync(k.Key);
        await LoadKeys();
        return L.F("KeyRemoved", k);
    });

    private void ShowTokens(IReadOnlyList<LoginToken> tokens)
    {
        _tokens.Items.Clear();
        foreach (var t in tokens.OrderBy(t => t.Created)) _tokens.Items.Add(new TokenRow(t));
    }

    private async Task LoadTokens() => ShowTokens(await _session.GetLoginTokensAsync());

    private Task NewToken() => Run(async () =>
    {
        if (TokenForm.Ask(this, TokenLifetimes) is not { } f) return null;
        var token = await _session.CreateLoginTokenAsync(f.Name, f.Hours > 0 ? TimeSpan.FromHours(f.Hours) : null);
        await LoadTokens();
        TokenForm.ShowCreated(this, token);
        return null;
    });

    private Task RemoveToken() => Run(async () =>
    {
        if (_tokens.SelectedItem is not TokenRow t || !Confirm(L.F("ConfirmRemoveToken", t.Name), L.T("TokensSection"))) return null;
        ShowTokens(await _session.RemoveLoginTokenAsync(t.Token));
        return L.F("TokenRemoved", t.Name);
    });
}

/// <summary>Adds the authenticator app: QR code and key, then a code from the app checked by the server.
/// Añade la app de autenticación: código QR y clave, y luego un código de la app que comprueba el servidor.</summary>
public static class AuthenticatorDialog
{
    internal static BitmapImage Qr(string text)
    {
        var png = new QRCoder.PngByteQRCode(new QRCoder.QRCodeGenerator().CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>The key in groups of four, as authenticator apps show it. / La clave en grupos de cuatro, como la enseñan las apps.</summary>
    internal static string Grouped(string secret) => string.Join(' ', secret.Chunk(4).Select(c => new string(c)));

    public static bool Ask(Window owner, AuthenticatorSetup setup, Func<string, Task> confirm, string? serverVersion)
    {
        var qr = new Image { Source = Qr(setup.Uri), Width = 220, Height = 220, Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        var key = new TextBox { Text = Grouped(setup.Secret), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 2, 0, 10) };
        var code = new TextBox { MaxLength = 7, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 18, Margin = new Thickness(0, 2, 0, 0) };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        foreach (var (e, id) in new (FrameworkElement, string)[] { (qr, "AuthenticatorQr"), (key, "AuthenticatorKey"), (code, "AuthenticatorCode"), (ok, "AuthenticatorOk"), (error, "AuthenticatorError") })
            AP.SetAutomationId(e, id);
        AP.SetName(key, L.T("AuthenticatorKey"));
        code.TextChanged += (_, _) => ok.IsEnabled = code.Text.Count(char.IsDigit) == 6;
        var w = new Window
        {
            Title = L.T("AuthenticatorSection"), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 440,
                Children =
                {
                    new TextBlock { Text = L.T("AuthenticatorHelp"), TextWrapping = TextWrapping.Wrap }, qr,
                    new TextBlock { Text = L.T("AuthenticatorKey"), Opacity = 0.8 }, key,
                    new TextBlock { Text = L.T("Code"), Opacity = 0.8 }, code, error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "AuthenticatorDialog");
        var done = false;
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            try { await confirm(code.Text); done = true; w.Close(); }
            catch (MeshClientException ex) when (ex.BurburitoKey is "usuarios/cambiar-ajustes/credencial-invalida" or "usuarios/cambiar-ajustes/dato-invalido")
            {
                error.Text = UserError.From(ex, serverVersion).Message;
                code.SelectAll();
                code.Focus();
            }
            catch (MeshClientException ex) { ErrorWindow.Show(w, UserError.From(ex, serverVersion)); }
            finally { ok.IsEnabled = !done && code.Text.Count(char.IsDigit) == 6; }
        };
        w.Loaded += (_, _) => code.Focus();
        w.ShowDialog();
        return done;
    }
}

/// <summary>A new login token: its name and how long it lasts; then its user name and password, shown once.
/// Un token nuevo: su nombre y cuánto dura; luego su usuario y contraseña, enseñados una vez.</summary>
public static class TokenForm
{
    public sealed record Result(string Name, int Hours);

    public static Result? Ask(Window owner, (int Hours, string Key)[] lifetimes)
    {
        var name = new TextBox { MaxLength = 100, MinWidth = 320, Margin = new Thickness(0, 4, 0, 10) };
        var expiry = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var x in lifetimes) expiry.Items.Add(L.T(x.Key));
        expiry.SelectedIndex = 0;
        var ok = new Button { Content = L.T("Ok"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90 };
        AP.SetAutomationId(name, "TokenNameBox"); AP.SetAutomationId(expiry, "TokenExpiryBox"); AP.SetAutomationId(ok, "TokenFormOk");
        name.TextChanged += (_, _) => ok.IsEnabled = name.Text.Trim().Length > 0;
        var w = new Window
        {
            Title = L.T("NewToken").TrimEnd('…'), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.T("TokenName") }, name, new TextBlock { Text = L.T("TokenExpiry") }, expiry,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        AP.SetAutomationId(w, "TokenForm");
        Result? result = null;
        ok.Click += (_, _) => { result = new(name.Text.Trim(), lifetimes[Math.Max(0, expiry.SelectedIndex)].Hours); w.Close(); };
        w.Loaded += (_, _) => name.Focus();
        w.ShowDialog();
        return result;
    }

    public static void ShowCreated(Window owner, LoginToken token)
    {
        TextBox Box(string text, string id)
        {
            var b = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), MinWidth = 360, Margin = new Thickness(0, 2, 8, 10) };
            AP.SetAutomationId(b, id);
            return b;
        }
        StackPanel Line(TextBox b)
        {
            var copy = new Button { Content = L.T("Copy"), MinWidth = 70, VerticalAlignment = VerticalAlignment.Top };
            copy.Click += (_, _) => { try { Clipboard.SetText(b.Text); } catch (Exception) { } };
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { b, copy } };
        }
        var close = new Button { Content = L.T("Close"), IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var w = new Window
        {
            Title = L.F("TokenCreatedTitle", token.Name), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), MaxWidth = 520,
                Children =
                {
                    new TextBlock { Text = L.T("TokenCreatedHelp"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                    new TextBlock { Text = L.T("TokenUser"), Opacity = 0.8 }, Line(Box(token.TokenUser, "TokenUserBox")),
                    new TextBlock { Text = L.T("Password"), Opacity = 0.8 }, Line(Box(token.Password ?? "", "TokenPasswordBox")),
                    close,
                },
            },
        };
        AP.SetAutomationId(w, "TokenCreated");
        close.Click += (_, _) => w.Close();
        w.ShowDialog();
    }
}
