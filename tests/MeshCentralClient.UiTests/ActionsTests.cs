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
    private string? _boxTitle;

    public void Dispose()
    {
        // A message window left open freezes UI Automation for the next tests: closed by its title. / Una ventana de mensaje abierta congela UI Automation: se cierra por su título.
        if (_boxTitle is not null) { try { CloseAgentBox(_boxTitle); } catch (Exception) { } }
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

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Closes the agent's message window. Measured 08-11 on GitHub (2 runs of 4): the agent's window came up blank and
    /// never answered WM_CLOSE, and left open it covered the next tests. The app's part was already checked (the window
    /// appeared with its title). The window belongs sometimes to a child process of the agent and sometimes (seen 08-11)
    /// to the agent itself: a hung child is stopped; a hung agent is restarted as the run script starts it, and the next
    /// tests begin only once it is back online. Either way a screenshot says so.
    /// Cierra la ventana de mensaje del agente. Medido el 08-11 en GitHub (2 pasadas de 4): la ventana del agente salió en
    /// blanco y nunca atendió WM_CLOSE, y abierta tapaba las pruebas siguientes. Lo de la app ya se comprobó (la ventana
    /// salió con su título). La ventana es a veces de un proceso hijo del agente y a veces (visto el 08-11) del propio
    /// agente: un hijo colgado se para; un agente colgado se reinicia como lo arranca el guion, y las pruebas siguientes
    /// empiezan cuando vuelve a estar conectado. En los dos casos, una captura lo dice.
    /// </summary>
    private static void CloseAgentBox(string title)
    {
        var h = FindWindow(null, title);
        if (h == IntPtr.Zero) return;
        PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        if (Retry.WhileFalse(() => FindWindow(null, title) == IntPtr.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250)).Result) return;
        GetWindowThreadProcessId(h, out var pid);
        var agent = int.Parse(Env("MESHUI_AGENT_PID"));
        if (pid == 0) throw new Exception("the message window did not close, and it belongs to no process");
        if (pid == agent) { RestartAgent(title); return; }
        Shot("message-box-hung-stopped");
        Console.WriteLine($"the agent's message window hung; its process {pid} (agent {agent}) was stopped");
        Process.GetProcessById((int)pid).Kill();
        Until(() => FindWindow(null, title) == IntPtr.Zero, "the message window did not close");
    }

    /// <summary>The agent itself hung on its message window: started again the way the run script starts it, and back
    /// online before the next test. / El propio agente se colgó en su ventana de mensaje: se arranca otra vez como lo
    /// arranca el guion, y conectado antes de la prueba siguiente.</summary>
    private static void RestartAgent(string title)
    {
        Shot("message-box-hung-agent-restarted");
        var old = Process.GetProcessById(int.Parse(Env("MESHUI_AGENT_PID")));
        var exe = old.MainModule!.FileName;
        Console.WriteLine($"the agent's own message window hung; the agent {old.Id} ({exe}) is restarted");
        old.Kill();
        old.WaitForExit(15_000);
        Until(() => FindWindow(null, title) == IntPtr.Zero, "the hung message window gone with the agent");
        var fresh = Process.Start(new ProcessStartInfo(exe, "connect") { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        Environment.SetEnvironmentVariable("MESHUI_AGENT_PID", fresh.Id.ToString());
        Until(() => AgentOnline().GetAwaiter().GetResult(), "the restarted agent back online");
    }

    private static async Task<bool> AgentOnline()
    {
        try
        {
            Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
            await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
            await s.GetDeviceGroupsAsync();
            return (await s.GetDevicesAsync()).Any(d => d.Name == Environment.MachineName && d.IsOnline);
        }
        catch (MeshClientException) { return false; }
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

        //    As the signed-in user, the box a person ticks. Expanded there, not the echo of what was typed.
        //    Como el usuario con sesión, la casilla que marca una persona. Expandido allí, no el eco de lo tecleado.
        Id(cw, "RunAsUser").AsCheckBox().IsChecked = true;
        Replace(Id(cw, "CommandsBox"), "Write-Output ('usuario-' + $env:USERNAME)");
        Id(cw, "RunButton").AsButton().Invoke();
        Until(() => Output(cw).Contains("usuario-" + Environment.UserName, StringComparison.OrdinalIgnoreCase), "output as the user", () => Output(cw));
        Shot("as-user");
        cw.AsWindow().Close();

        // 2. A message window: it appears on this desktop, shown by the agent. / Una ventana de mensaje: aparece en este escritorio, la enseña el agente.
        var tag = "prueba-" + Guid.NewGuid().ToString("N")[..6];
        Menu(win, "SendMessageItem");
        var md = Anywhere("MessageDialog");
        // Found and closed through Windows by its unique title: while it is up, UI Automation searches of the whole
        // desktop time out (measured 07-11: COMException "Operation timed out", and the next test waited 11 minutes).
        // Se busca y se cierra por Windows con su título único: mientras está abierta, las búsquedas de UI Automation
        // en todo el escritorio se agotan (medido el 07-11: COMException «Operation timed out», y la prueba siguiente
        // esperó 11 minutos).
        var title = _boxTitle = "MCC " + tag;
        Replace(Id(md, "MessageTitleBox"), title);
        Replace(Id(md, "MessageTextBox"), "Hola desde la app " + tag);
        Id(md, "AsMessageBox").AsRadioButton().IsChecked = true;
        Id(md, "MessageSend").AsButton().Invoke();
        IntPtr shown = IntPtr.Zero;
        Until(() => (shown = FindWindow(null, title)) != IntPtr.Zero && IsWindowVisible(shown), "the agent's message window did not appear");
        Shot("message-box");
        Until(() => Id(win, "ActionStatus").Name.StartsWith("Message sent to 1"), "message status", () => Id(win, "ActionStatus").Name);
        CloseAgentBox(title);

        //    A notification. / Una notificación.
        Menu(win, "SendMessageItem");
        md = Anywhere("MessageDialog");
        Replace(Id(md, "MessageTextBox"), "Aviso " + tag);
        Id(md, "MessageSend").AsButton().Invoke();
        Until(() => Id(win, "ActionStatus").Name.StartsWith("Message sent to 1"), "notification status", () => Id(win, "ActionStatus").Name);

        // 3. Wake: sent by the agent of this same network. / Encender: lo manda el agente de esta misma red.
        Menu(win, "WakeItem");
        Until(() => Id(win, "ActionStatus").Name == "Wake up sent by 1 device(s) of their network.", "wake status", () => Id(win, "ActionStatus").Name);

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
