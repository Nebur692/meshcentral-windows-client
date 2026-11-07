using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// Device actions on the real Windows agent of this machine, as a person does them from the device menu: commands in
/// cmd and PowerShell with their output, a message window that really appears on this desktop, a notification, wake,
/// and a restart that asks first and does nothing when the answer is no.
/// Acciones sobre el agente de Windows real de esta máquina, como las hace una persona desde el menú del equipo: órdenes
/// en cmd y PowerShell con su salida, una ventana de mensaje que aparece de verdad en este escritorio, una notificación,
/// encender, y un reinicio que pregunta antes y no hace nada si la respuesta es no.
/// </summary>
[Trait("Category", "UI")]
public sealed class ActionsTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-actions-ui-");
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"actions-{name}.png"));
    }

    private static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch (Exception) { return ""; } }

    private void Menu(AutomationElement win, string item)
    {
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device").RightClick();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(item)), item).Click();
    }

    private static string Output(AutomationElement w) => Id(w, "RunOutput").AsTextBox().Text;

    private static void Replace(AutomationElement box, string text)
    {
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
    }

    [Fact]
    public void A_person_acts_on_a_device_from_its_menu()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Id(win, "UserBox").Click(); Keyboard.Type("admin");
        Id(win, "PasswordBox").Click(); Keyboard.Type("Admin-Pass-1234");
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");

        // 1. Commands in cmd: the output comes back with this machine's name, read by the agent itself.
        //    Órdenes en cmd: vuelve la salida con el nombre de esta máquina, leído por el propio agente.
        Menu(win, "RunCommandsItem");
        var cw = Anywhere("CommandsWindow");
        Replace(Id(cw, "CommandsBox"), "echo hola-%COMPUTERNAME%");
        Id(cw, "RunButton").AsButton().Invoke();
        Until(() => Output(cw).Contains("hola-" + Environment.MachineName, StringComparison.OrdinalIgnoreCase), "cmd output", () => Output(cw));
        Shot("cmd");

        //    And in PowerShell. / Y en PowerShell.
        Id(cw, "UsePowerShell").AsRadioButton().IsChecked = true;
        Replace(Id(cw, "CommandsBox"), "Write-Output ('ps-' + (6*7))");
        Id(cw, "RunButton").AsButton().Invoke();
        Until(() => Output(cw).Contains("ps-42"), "PowerShell output", () => Output(cw));
        Shot("powershell");
        cw.AsWindow().Close();

        // 2. A message window: it appears on this desktop, shown by the agent. / Una ventana de mensaje: aparece en este escritorio, la enseña el agente.
        var tag = "prueba-" + Guid.NewGuid().ToString("N")[..6];
        Menu(win, "SendMessageItem");
        var md = Anywhere("MessageDialog");
        Replace(Id(md, "MessageTextBox"), "Hola desde la app " + tag);
        Id(md, "AsMessageBox").AsRadioButton().IsChecked = true;
        Id(md, "MessageSend").AsButton().Invoke();
        var shown = WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Hola desde la app " + tag, PropertyConditionFlags.MatchSubstring)), "the agent's message window");
        Shot("message-box");
        Until(() => Id(win, "ActionStatus").Name.StartsWith("Message sent to 1"), "message status", () => Id(win, "ActionStatus").Name);
        // Closed as its reader would, with its button. / Se cierra como lo haría quien lo lee, con su botón.
        var box = shown.Parent;
        while (box is not null && box.ControlType != ControlType.Window) box = box.Parent;
        (box?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)) ?? throw new Exception("no button in the message window")).Click();
        Until(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Hola desde la app " + tag, PropertyConditionFlags.MatchSubstring)) is null, "the message window did not close");

        //    A notification. / Una notificación.
        Menu(win, "SendMessageItem");
        md = Anywhere("MessageDialog");
        Replace(Id(md, "MessageTextBox"), "Aviso " + tag);
        Id(md, "MessageSend").AsButton().Invoke();
        Until(() => Id(win, "ActionStatus").Name.StartsWith("Message sent to 1"), "notification status", () => Id(win, "ActionStatus").Name);

        // 3. Wake: sent by the agent of this same network. / Encender: lo manda el agente de esta misma red.
        Menu(win, "WakeItem");
        Until(() => Id(win, "ActionStatus").Name.Contains("wake packets"), "wake status", () => Id(win, "ActionStatus").Name);

        // 4. Restart asks first; "No" sends nothing and the device stays online. / Reiniciar pregunta antes; «No» no manda nada y el equipo sigue conectado.
        var before = Id(win, "ActionStatus").Name;
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device").RightClick();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("PowerMenu")), "PowerMenu").Click();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RestartItem")), "RestartItem").Click();
        var no = WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("No"))), "the restart confirmation");
        Shot("restart-asks");
        Assert.Contains(Environment.MachineName, string.Join(" ", no.Parent.FindAllDescendants().Select(SafeName)));
        no.AsButton().Invoke();
        Thread.Sleep(3000);
        Assert.Equal(before, Id(win, "ActionStatus").Name);
        Assert.Equal("Devices: 1 · online: 1", Id(win, "CountText").Name);
        Shot("done");
    }
}
