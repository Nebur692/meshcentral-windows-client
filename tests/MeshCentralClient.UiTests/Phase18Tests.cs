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
/// The agent's console answers on real Windows, and the device's icon and own SSH port are saved from its dialog.
/// La consola del agente contesta en Windows real, y el icono y el puerto SSH propio se guardan desde su diálogo.
/// </summary>
[Trait("Category", "UI")]
public sealed class Phase18Tests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-f18-ui-");
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"f18-{name}.png"));
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
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(item)), item).Click();
    }

    [Fact]
    public void A_person_asks_the_agent_on_its_console()
    {
        var win = SignIn();
        Menu(win, "ConsoleItem");
        var cw = Anywhere("AgentConsoleWindow");
        Type(Id(cw, "ConsoleInput"), "help");
        Id(cw, "ConsoleSend").AsButton().Invoke();
        Until(() => Id(cw, "ConsoleOutput").AsTextBox().Text.Contains("netinfo", StringComparison.OrdinalIgnoreCase), "the agent's answer", () => Id(cw, "ConsoleOutput").AsTextBox().Text);
        Shot("console");
    }

    [Fact]
    public async Task A_person_sets_the_icon_and_an_own_ssh_port()
    {
        await using var s = await Admin();
        await s.GetDeviceGroupsAsync(); // rights come from the groups / los permisos salen de los grupos
        var dev = (await s.GetDevicesAsync()).First(d => d.Name == Environment.MachineName);
        var before = DeviceSettings.Of(dev);
        try
        {
            var win = SignIn();
            Menu(win, "DeviceSettingsItem");
            var dw = Anywhere("DeviceSettingsDialog");
            Id(dw, "DeviceIcon").AsComboBox().Select(3);   // Server / Servidor
            Type(Id(dw, "PortSsh"), "2222");
            Shot("settings");
            Id(dw, "DeviceSettingsOk").AsButton().Invoke();
            Until(() => s.GetDevicesAsync().GetAwaiter().GetResult().First(d => d.Id == dev.Id) is { SshPort: 2222, Icon: 4 }, "the icon and port kept by the server");
        }
        finally
        {
            await s.SaveDeviceSettingsAsync((await s.GetDevicesAsync()).First(d => d.Id == dev.Id), before);
        }
    }
}
