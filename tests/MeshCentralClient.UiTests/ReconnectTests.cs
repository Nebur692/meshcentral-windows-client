using System.Diagnostics;
using System.Net.Http;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MeshCentralClient.Core;
using Xunit;
using Capture = FlaUI.Core.Capturing.Capture;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// When the server closes the channel (here: the account is locked, which drops its sessions), the app says so and signs
/// in again by itself; while it cannot (still locked) it keeps trying, and once unlocked it is back on the list.
/// Si el servidor cierra el canal (aquí: se bloquea la cuenta), la app lo dice y vuelve a entrar sola en cuanto puede.
/// </summary>
[Trait("Category", "UI")]
public sealed class ReconnectTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-rec-ui-");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static async Task<MeshServerSession> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what)
    {
        if (Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result is { } found) return found;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + " did not appear");
    }

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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"rec-{name}.png"));
    }

    private static string Text(AutomationElement e) { try { return string.Join(" | ", new[] { e.Name }.Concat(e.FindAllDescendants().Select(x => x.Name))); } catch (Exception) { return ""; } }

    private static void Type(AutomationElement box, string text)
    {
        box.Focus();
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
    }

    private void Yes() =>
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes"))), "the confirmation").AsButton().Invoke();

    private void FileDialog(string title, string path)
    {
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title))), "the dialog " + title);
        Thread.Sleep(700);
        Keyboard.Type(path);
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    private void Input(string text)
    {
        var box = Anywhere("InputBox");
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Anywhere("InputOk").AsButton().Invoke();
    }

    private AutomationElement SignIn()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Type(Id(win, "UserBox"), "admin");
        Id(win, "PasswordBox").Click(); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("Admin-Pass-1234");
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        return win;
    }

    private void Menu(AutomationElement win, string item)
    {
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device").RightClick();
        // These live in the device menu's "More" submenu. / Estas viven en el submenú «Más» del menú del equipo.
        // Invoked, not clicked: a click lands while the submenu is still sliding open. / Invocado, no pulsado: el clic cae mientras se abre.
        Anywhere("MoreMenu").AsMenuItem().Expand();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(item)), item).AsMenuItem().Invoke();
    }

    [Fact]
    public async Task The_app_signs_in_again_by_itself_after_the_server_drops_it()
    {
        await using var admin = await Admin();
        var target = (await admin.GetUsersAsync()).First(u => u.Name == "User");
        try
        {
            var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
            psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
            psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
            psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
            _app = Application.Launch(psi);
            var win = _app.GetMainWindow(_automation, Timeout)!;
            win.SetForeground();
            Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
            Type(Id(win, "UserBox"), "user");
            Id(win, "PasswordBox").Click(); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("User-Pass-1234");
            Id(win, "ConnectButton").AsButton().Invoke();
            if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
            bool Shown(string id) => win.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { IsOffscreen: false };
            Until(() => Shown("DeviceList"), "signed in");

            // The server closes every session of a locked account. / El servidor cierra las sesiones de una cuenta bloqueada.
            await admin.SetLockedAsync(target, true);
            Until(() => Text(Id(win, "LostText")).Contains("Reconnecting by itself", StringComparison.Ordinal), "the reconnection announced", () => Text(Id(win, "LostText")));
            Shot("lost");
            // While locked, the try fails and the login form says why. / Mientras siga bloqueada, el intento falla.
            Until(() => Shown("ConnectButton"), "the failed try on the login form");
            await admin.SetLockedAsync((await admin.GetUsersAsync()).First(u => u.Id == target.Id), false);
            Until(() => Shown("DeviceList"), "back in by itself");
            Shot("back");
        }
        finally
        {
            var now = (await admin.GetUsersAsync()).First(u => u.Id == target.Id);
            if (now.IsLocked) await admin.SetLockedAsync(now, false);
        }
    }
}
