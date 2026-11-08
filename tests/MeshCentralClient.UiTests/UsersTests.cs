using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MeshCentralClient.Core;
using Xunit;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// Users and the own account as a person uses them, on real Windows against the real server: an administrator creates
/// an account with a server right, locks it, gives it a new password, puts it in a new user group and deletes both; a
/// person changes their password, adds an authenticator app from its QR key, makes backup codes and a login token.
/// Every step is checked against the server itself, and signing in proves the passwords and the token.
/// Usuarios y la propia cuenta como los usa una persona, en Windows de verdad contra el servidor real. Cada paso se
/// comprueba contra el propio servidor, y entrar con la cuenta demuestra las contraseñas y el token.
/// </summary>
[Trait("Category", "UI")]
public sealed class UsersTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-users-ui-");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        // Whatever the test left on the server, by its unique tag. / Lo que dejó en el servidor, por su etiqueta única.
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static async Task<MeshServerSession> As(string user, string pass, string? code = null)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new(user, pass, code), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
    }

    private async Task CleanServer()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        foreach (var g in (await s.GetUserGroupsAsync()).Where(g => g.Name.Contains(_tag))) await s.DeleteUserGroupAsync(g);
        foreach (var u in (await s.GetUsersAsync()).Where(u => u.Name.Contains(_tag))) await s.DeleteUserAsync(u);
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what) =>
        Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result ?? throw new Exception(what + " did not appear");

    private static AutomationElement Id(AutomationElement root, string id) => WaitFor(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private AutomationElement Anywhere(string id) => WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void Until(Func<bool> condition, string what, Func<string>? seen = null)
    {
        if (Retry.WhileFalse(condition, Timeout, TimeSpan.FromMilliseconds(250)).Result) return;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + (seen is null ? "" : " — seen: " + seen()));
    }

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"users-{name}.png"));
    }

    private static string Text(AutomationElement e) { try { return string.Join(" | ", new[] { e.Name }.Concat(e.FindAllDescendants().Select(x => x.Name))); } catch (Exception) { return ""; } }

    private static void Type(AutomationElement box, string text)
    {
        box.Focus();
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
    }

    private static AutomationElement Item(AutomationElement list, string name) =>
        WaitFor(() => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)).And(cf.ByName(name))), $"\"{name}\" in {list.AutomationId}");

    private void ClickYes(AutomationElement owner, string what)
    {
        var yes = WaitFor(() => owner.AsWindow().ModalWindows.FirstOrDefault()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes"))), what);
        yes.AsButton().Invoke();
    }

    private AutomationElement SignIn(string user, string pass)
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Id(win, "UserBox").Click(); Keyboard.Type(user);
        Id(win, "PasswordBox").Click(); Keyboard.Type(pass);
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        WaitFor(() => win.FindFirstDescendant(cf => cf.ByAutomationId("AccountButton")) is { IsOffscreen: false } b ? b : null, "the signed-in window");
        return win;
    }

    [Fact]
    public async Task An_administrator_manages_an_account_and_a_user_group()
    {
        var name = "ana" + _tag;
        var win = SignIn("admin", "Admin-Pass-1234");
        Id(win, "UsersButton").AsButton().Invoke();
        var uw = Anywhere("UsersWindow");
        var list = Id(uw, "UserList");
        Item(list, "admin");

        // 1. A new account with one server right. / Una cuenta nueva con un permiso de servidor.
        Id(uw, "NewUserButton").Click();
        var form = Anywhere("UserForm");
        Type(Id(form, "UserNameBox"), name);
        Type(Id(form, "UserPasswordBox"), "Ana-Pass-1234");
        Type(Id(form, "UserRepeatBox"), "Ana-Pass-1234");
        Type(Id(form, "UserEmailBox"), "ana@example.org");
        Type(Id(form, "UserRealNameBox"), "Ana Pérez");
        Id(form, "SiteNoNewDeviceGroups").AsCheckBox().IsChecked = true;
        Shot("new-user");
        Id(form, "UserFormOk").AsButton().Invoke();
        Until(() => Id(uw, "UsersStatus").Name == $"Account {name} created.", "the created status", () => Id(uw, "UsersStatus").Name);
        Until(() => Text(Item(list, name)).Contains("No new device groups"), "the row with its right", () => Text(list));
        Shot("created");
        await using (var s = await As("admin", "Admin-Pass-1234"))
        {
            var u = (await s.GetUsersAsync()).Single(x => x.Name == name);
            Assert.Equal(("ana@example.org", "Ana Pérez", (long)SiteRight.NoNewDeviceGroups), (u.Email, u.RealName, u.SiteRights));
        }

        // 2. Locked, then a new password, then unlocked: the server agrees and signing in proves it.
        //    Bloqueada, contraseña nueva y desbloqueada: el servidor lo confirma y entrar lo demuestra.
        Item(list, name).Click();
        Id(uw, "LockUserButton").AsButton().Invoke();
        Until(() => Text(Item(list, name)).Contains("Locked"), "the locked state", () => Text(list));
        await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Ana-Pass-1234"));
        Item(list, name).Click();
        Id(uw, "SetPasswordButton").Click();
        var pf = Anywhere("PasswordForm");
        Type(Id(pf, "NewPasswordBox"), "Ana-New-5678");
        Type(Id(pf, "RepeatPasswordBox"), "Ana-New-5678");
        Id(pf, "PasswordFormOk").AsButton().Invoke();
        Until(() => Id(uw, "UsersStatus").Name == $"New password set for {name}.", "the password status", () => Id(uw, "UsersStatus").Name);
        Item(list, name).Click();
        Id(uw, "LockUserButton").AsButton().Invoke();
        Until(() => !Text(Item(list, name)).Contains("Locked"), "the unlocked state", () => Text(list));
        await using (var ana = await As(name, "Ana-New-5678")) Assert.Equal(name, ana.User.Name);
        Shot("unlocked");

        // 3. A user group with her in it. / Un grupo de usuarios con ella dentro.
        var groupName = "Soporte " + _tag;
        Id(uw, "UserGroupsTab").AsTabItem().Select();
        Id(uw, "NewUserGroupButton").Click();
        var gf = Anywhere("GroupForm");
        Type(Id(gf, "GroupNameBox"), groupName);
        Id(gf, "GroupFormOk").AsButton().Invoke();
        Until(() => Id(uw, "UserGroupNameText").Name == groupName, "the new user group", () => Id(uw, "UserGroupNameText").Name);
        Id(uw, "AddMembersButton").Click();
        var nf = Anywhere("NamesForm");
        Type(Id(nf, "NamesBox"), name);
        Id(nf, "NamesOk").AsButton().Invoke();
        Item(Id(uw, "UserGroupMembers"), name);
        Shot("user-group");
        await using (var s = await As("admin", "Admin-Pass-1234"))
            Assert.Equal([name], (await s.GetUserGroupsAsync()).Single(g => g.Name == groupName).Members.Values);

        // 4. Both deleted, asked first. / Los dos borrados, preguntando antes.
        Id(uw, "DeleteUserGroupButton").Click();
        ClickYes(uw, "the user group delete confirmation");
        Until(() => Id(uw, "UsersStatus").Name == $"User group {groupName} deleted.", "the group delete status", () => Id(uw, "UsersStatus").Name);
        Id(uw, "UsersTab").AsTabItem().Select();
        Item(list, name).Click();
        Id(uw, "DeleteUserButton").Click();
        ClickYes(uw, "the account delete confirmation");
        Until(() => Id(uw, "UsersStatus").Name == $"Account {name} deleted.", "the delete status", () => Id(uw, "UsersStatus").Name);
        Until(() => list.FindFirstDescendant(cf => cf.ByName(name)) is null, "the account leaving the list");
        await using (var s = await As("admin", "Admin-Pass-1234"))
        {
            Assert.DoesNotContain(await s.GetUsersAsync(), u => u.Name == name);
            Assert.DoesNotContain(await s.GetUserGroupsAsync(), g => g.Name == groupName);
        }
        Shot("deleted");
    }

    [Fact]
    public async Task A_person_secures_their_own_account()
    {
        var name = "yo" + _tag;
        await using (var admin = await As("admin", "Admin-Pass-1234")) await admin.CreateUserAsync(new(name, "Mine-Pass-1234"));
        var win = SignIn(name, "Mine-Pass-1234");
        // Not an administrator: no Users button (collapsed, so not even in the tree). / No es administrador: sin botón de Usuarios.
        Assert.True(win.FindFirstDescendant(cf => cf.ByAutomationId("UsersButton")) is null or { IsOffscreen: true });
        Id(win, "AccountButton").AsButton().Invoke();
        var aw = Anywhere("AccountWindow");

        // 1. Password. / Contraseña.
        Type(Id(aw, "CurrentPasswordBox"), "Mine-Pass-1234");
        Type(Id(aw, "NewPasswordBox"), "Mine-New-5678");
        Type(Id(aw, "RepeatPasswordBox"), "Mine-New-5678");
        Id(aw, "ChangePasswordButton").AsButton().Invoke();
        Until(() => Id(aw, "AccountStatus").Name == "Password changed.", "the password status", () => Id(aw, "AccountStatus").Name);
        await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Mine-Pass-1234"));

        // 2. Authenticator app: the key shown next to the QR code makes the code, as a phone would.
        //    App de autenticación: la clave que sale junto al QR da el código, como lo haría un móvil.
        Assert.False(Id(aw, "ShowCodesButton").IsEnabled);
        Id(aw, "AddAuthenticatorButton").AsButton().Invoke();
        var ad = Anywhere("AuthenticatorDialog");
        Id(ad, "AuthenticatorQr");
        var secret = Id(ad, "AuthenticatorKey").AsTextBox().Text.Replace(" ", "");
        Shot("authenticator");
        Type(Id(ad, "AuthenticatorCode"), Totp.Code(secret) == "000000" ? "111111" : "000000");
        Id(ad, "AuthenticatorOk").AsButton().Invoke();
        Until(() => Id(ad, "AuthenticatorError").Name.Length > 0, "the wrong-code message in the dialog");
        Type(Id(ad, "AuthenticatorCode"), Totp.Code(secret));
        Id(ad, "AuthenticatorOk").AsButton().Invoke();
        Until(() => Id(aw, "AccountStatus").Name == "Authenticator app added.", "the authenticator status", () => Id(aw, "AccountStatus").Name);
        Until(() => Id(aw, "AuthenticatorState").Name.StartsWith("On", StringComparison.Ordinal), "the authenticator on", () => Id(aw, "AuthenticatorState").Name);
        var asked = await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Mine-New-5678"));
        Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);

        // 3. Backup codes: ten, and one of them signs in. / Códigos de respaldo: diez, y uno sirve para entrar.
        Id(aw, "NewCodesButton").AsButton().Invoke();
        Until(() => Id(aw, "CodesBox").AsTextBox().Text.Split('\n').Length == 10, "the ten codes", () => Id(aw, "CodesBox").AsTextBox().Text);
        var first = Id(aw, "CodesBox").AsTextBox().Text.Split('\n')[0].Trim();
        Shot("codes");
        await using (var viaCode = await As(name, "Mine-New-5678", first)) Assert.Equal(name, viaCode.User.Name);

        // 4. A login token, shown once, that signs in; then removed. / Un token, enseñado una vez, que entra; luego quitado.
        Id(aw, "NewTokenButton").Click();
        var tf = Anywhere("TokenForm");
        Type(Id(tf, "TokenNameBox"), "script " + _tag);
        Id(tf, "TokenFormOk").AsButton().Invoke();
        var created = Anywhere("TokenCreated");
        var tokenUser = Id(created, "TokenUserBox").AsTextBox().Text;
        var tokenPass = Id(created, "TokenPasswordBox").AsTextBox().Text;
        Shot("token");
        created.AsWindow().Close();
        await using (var viaToken = await As(tokenUser, tokenPass)) Assert.Equal(name, viaToken.User.Name);
        var tokens = Id(aw, "TokenList");
        Item(tokens, "script " + _tag).Click();
        Id(aw, "RemoveTokenButton").AsButton().Invoke();
        ClickYes(aw, "the token remove confirmation");
        Until(() => tokens.FindFirstDescendant(cf => cf.ByName("script " + _tag)) is null, "the token leaving the list");
        await Assert.ThrowsAsync<MeshClientException>(() => As(tokenUser, tokenPass));

        // 5. Authenticator removed: signing in no longer asks for a code. / App quitada: entrar ya no pide código.
        Id(aw, "RemoveAuthenticatorButton").AsButton().Invoke();
        ClickYes(aw, "the authenticator remove confirmation");
        Until(() => Id(aw, "AuthenticatorState").Name.StartsWith("Off", StringComparison.Ordinal), "the authenticator off", () => Id(aw, "AuthenticatorState").Name);
        await using (var plain = await As(name, "Mine-New-5678")) Assert.Equal(name, plain.User.Name);
        Shot("done");
    }
}
