using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// SSH inside the app as a person uses it, to the OpenSSH server that run-ui-test.ps1 turns on in this Windows with a
/// throwaway account: the first time the app asks about the server's identity, the second time it knows it; the shell
/// answers as that account, which only SSH can be. / SSH dentro de la app como lo usa una persona, al servidor OpenSSH
/// que enciende run-ui-test.ps1 en este Windows con una cuenta desechable: la primera vez pregunta por la identidad, la
/// segunda ya la conoce; el shell contesta como esa cuenta, que solo puede ser por SSH.
/// </summary>
[Trait("Category", "UI")]
public sealed class SshTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-ssh-ui-");
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what)
    {
        if (Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result is { } found) return found;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + " did not appear");
    }

    private static AutomationElement Id(AutomationElement root, string id) => WaitFor(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);
    private AutomationElement Anywhere(string id) => WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"ssh-{name}.png"));
    }

    private static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch (Exception) { return ""; } }

    private static void Type(AutomationElement box, string text)
    {
        box.Focus();
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>A row of the terminal that reads <paramref name="text"/>. / Una fila de la terminal que dice el texto.</summary>
    private static AutomationElement? Row(AutomationElement term, Func<string, bool> match) =>
        term.FindAllDescendants().FirstOrDefault(e => match(SafeName(e).Replace(" ", " ").Trim()));

    private AutomationElement OpenSsh(AutomationElement win, bool expectQuestion)
    {
        win.SetForeground();
        var device = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.Patterns.SelectionItem.Pattern.Select();
        device.RightClick();
        Anywhere("SshItem").AsMenuItem().Invoke();
        var form = Anywhere("SshForm");
        // The account is remembered from the first time, never the password. / La cuenta se recuerda de la primera vez, nunca la contraseña.
        var user = Id(form, "SshUserBox");
        if (user.Patterns.Value.Pattern.Value.Value != Env("MESHUI_SSH_USER")) Type(user, Env("MESHUI_SSH_USER"));
        Id(form, "SshPasswordBox").Click();
        Keyboard.Type(Env("MESHUI_SSH_PASS"));
        Wait.UntilInputIsProcessed();
        Id(form, "SshOk").AsButton().Invoke();
        if (expectQuestion)
        {
            // Windows' own message box: its Yes button is id 6. / La caja de mensaje de Windows: su botón Sí es el id 6.
            var question = WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("SSH server identity")), "the identity question");
            Shot("identity-question");
            output.WriteLine(string.Join(" | ", question.FindAllDescendants().Select(SafeName).Where(n => n.Length > 0)));
            Assert.Contains(question.FindAllDescendants(), e => SafeName(e).Contains("SHA256:", StringComparison.Ordinal));
            Id(question, "6").AsButton().Invoke();
        }
        var term = Anywhere("TerminalWindow");
        Assert.True(Retry.WhileFalse(() => Id(term, "TerminalStatus").Name.Contains("SHA256:"), Timeout, TimeSpan.FromMilliseconds(300)).Result,
            "the SSH terminal did not open: " + Id(term, "TerminalStatus").Name);
        return term;
    }

    [Fact]
    public void A_person_opens_ssh_through_the_device_and_the_second_time_the_identity_is_known()
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

        var term = OpenSsh(win, expectQuestion: true);
        Id(term, "TerminalView").Click();
        Keyboard.Type("whoami");
        Keyboard.Type(VirtualKeyShort.ENTER);
        var me = Env("MESHUI_SSH_USER").ToLowerInvariant();
        Assert.NotNull(Retry.WhileNull(() => Row(term, t => t.EndsWith("\\" + me, StringComparison.OrdinalIgnoreCase)), Timeout, TimeSpan.FromMilliseconds(300)).Result);
        Shot("whoami");
        term.AsWindow().Close();

        // Second time: no question, the identity is known; the account comes filled in. / Segunda vez: sin pregunta; la cuenta viene puesta.
        term = OpenSsh(win, expectQuestion: false);
        Id(term, "TerminalView").Click();
        Keyboard.Type("set /a 6*7");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.NotNull(Retry.WhileNull(() => Row(term, t => t == "42"), Timeout, TimeSpan.FromMilliseconds(300)).Result);
        var saved = File.ReadAllText(Path.Combine(_dir.FullName, "appdata", "MeshCentralClient", "relays.json"));
        Assert.Contains("SHA256:", saved, StringComparison.Ordinal);
        Assert.DoesNotContain(Env("MESHUI_SSH_PASS"), saved, StringComparison.Ordinal);
        term.AsWindow().Close();
    }
}
