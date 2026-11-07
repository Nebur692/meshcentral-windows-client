using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Wait = FlaUI.Core.Input.Wait;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The whole app, from a person's gestures, against a real MeshCentral with a real Windows agent. Screenshots of
/// every step go to MESHUI_SHOTS. Without its variables the test FAILS: a check that does not run must not look green.
/// La app entera, desde los gestos de una persona, contra un MeshCentral real con un agente de Windows real. Las
/// capturas de cada paso van a MESHUI_SHOTS. Sin sus variables la prueba FALLA: lo que no corre no puede verse verde.
/// </summary>
[Trait("Category", "UI")]
public sealed class EndToEndTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _appData = Directory.CreateTempSubdirectory("mcc-ui-");
    private Application? _app;
    private Window? _win;
    private int _shot;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        try { _appData.Delete(true); } catch (Exception) { }
    }

    private void Start()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = _appData.FullName; // isolated saved servers / servidores guardados aislados
        _app = Application.Launch(psi);
        _win = _app.GetMainWindow(_automation, Timeout) ?? throw new Exception("main window did not appear");
        _win.SetForeground();
    }

    private AutomationElement Find(string id, AutomationElement? root = null, TimeSpan? timeout = null) =>
        Retry.WhileNull(() => (root ?? _win!).FindFirstDescendant(cf => cf.ByAutomationId(id)), timeout ?? Timeout, TimeSpan.FromMilliseconds(200)).Result
        ?? throw new Exception($"'{id}' did not appear / no apareció '{id}'");

    private AutomationElement FindAnywhere(string id) =>
        Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id)), Timeout, TimeSpan.FromMilliseconds(200)).Result
        ?? throw new Exception($"window element '{id}' did not appear / no apareció '{id}'");

    private bool Gone(string id) =>
        Retry.WhileTrue(() => _win!.FindFirstDescendant(cf => cf.ByAutomationId(id)) is not null, Timeout, TimeSpan.FromMilliseconds(200)).Result;

    private void Type(string id, string text)
    {
        var e = Find(id);
        e.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Press(VirtualKeyShort.DELETE); Keyboard.Release(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
    }

    private void Click(string id) => Find(id).AsButton().Invoke();

    private static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch (Exception) { return ""; } }

    private string Text(string id) => Find(id).Name;

    private void Shot(string what)
    {
        // Let WPF finish painting what the test just checked. / Que WPF acabe de pintar lo que la prueba acaba de comprobar.
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        var dir = Env("MESHUI_SHOTS");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{++_shot:00}-{what}.png");
        Capture.Screen().ToFile(file);
        output.WriteLine("screenshot " + file);
    }

    private void Login(string user, string password)
    {
        // The editable combo box: type into its text part. / El combo editable: se escribe en su parte de texto.
        var combo = Find("ServerBox").AsComboBox();
        combo.EditableText = Env("MESHUI_SERVER");
        Type("UserBox", user);
        Type("PasswordBox", password);
        Click("ConnectButton");
    }

    [Fact]
    public void A_person_signs_in_sees_the_devices_and_uses_two_factor()
    {
        Start();
        Shot("login-empty");

        // 1. Wrong password: plain message and its code. / Contraseña mala: mensaje en cristiano y su código.
        Login("admin", "wrong-password");
        var trust = FindAnywhere("CertTrust");
        Shot("certificate-prompt");
        trust.AsButton().Invoke();
        var footer = Text("ErrorFooter");
        Shot("wrong-password");
        Assert.EndsWith("Burburito code: 035-001-001-041", footer);
        Assert.Contains("Wrong username or password", Text("ErrorText"));

        // 2. Right password: the certificate is NOT asked again, devices appear, with the real agent online.
        //    Contraseña buena: el certificado NO se vuelve a preguntar, salen los equipos, con el agente real conectado.
        Type("PasswordBox", "Admin-Pass-1234");
        Click("ConnectButton");
        Find("SessionTitle");
        Assert.Null(_automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")));
        Assert.Equal("mesh.test", Text("SessionTitle"));
        var machine = Environment.MachineName;
        var row = Retry.WhileNull(() => Find("DeviceList").FindFirstDescendant(cf => cf.ByName(machine)), TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(500)).Result;
        Shot("devices");
        Assert.NotNull(row);
        Assert.Equal("Devices: 1 · online: 1", Text("CountText"));

        Assert.Equal("Search devices", Text("SearchHint"));

        // 3. Search filters, and clearing it brings the device back. / El buscador filtra y al vaciarlo vuelve el equipo.
        Type("SearchBox", "no-such-device-xyz");
        Assert.True(Retry.WhileTrue(() => Find("DeviceList").FindFirstDescendant(cf => cf.ByName(machine)) is not null, Timeout).Result);
        Shot("search-no-match");
        Assert.True(Gone("SearchHint"));
        Type("SearchBox", machine[..Math.Min(4, machine.Length)]);
        Assert.NotNull(Retry.WhileNull(() => Find("DeviceList").FindFirstDescendant(cf => cf.ByName(machine)), Timeout).Result);

        // 3b. Dark theme. / Tema oscuro.
        Find("ThemeBox").AsComboBox().Select(2);
        Shot("dark-devices");

        // 3c. Double click opens a terminal on the real Windows agent; a command runs and its output is on screen.
        //     El doble clic abre una terminal en el agente de Windows real; una orden se ejecuta y su salida sale en pantalla.
        Find("DeviceList").FindFirstDescendant(cf => cf.ByName(machine))!.DoubleClick();
        var term = FindAnywhere("TerminalWindow");
        var ready = Retry.WhileFalse(() => term.FindFirstDescendant(cf => cf.ByAutomationId("TerminalStatus"))?.Name.StartsWith("Ctrl+Shift+C") == true,
            TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(300)).Result;
        Shot("terminal-open");
        Assert.True(ready, "terminal did not open: " + term.FindFirstDescendant(cf => cf.ByAutomationId("TerminalStatus"))?.Name);
        Find("TerminalView", term).Click();
        Keyboard.Type("set /a 6*7");
        Keyboard.Type(VirtualKeyShort.ENTER);
        var answer = Retry.WhileNull(() => term.FindFirstDescendant(cf => cf.ByName("42")) ?? term.FindAllDescendants().FirstOrDefault(e => SafeName(e).Trim() == "42"),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500)).Result;
        Shot("terminal-output");
        Assert.NotNull(answer);
        term.AsWindow().Close();

        // 4. Disconnect and sign in with the two-factor account. / Desconectar y entrar con la cuenta de doble factor.
        Click("DisconnectButton");
        Find("ConnectButton");
        Login("otp", "Otp-Pass-1234");
        Find("CodeBox");
        Shot("two-factor-asked");

        Type("CodeBox", "000000");
        Click("ConnectButton");
        footer = Text("ErrorFooter");
        Shot("two-factor-wrong");
        Assert.EndsWith("Burburito code: 035-001-003-041", footer);

        Type("CodeBox", Totp.Code(Env("MESHUI_OTP_SECRET")));
        Click("ConnectButton");
        Find("SessionTitle");
        Find("EmptyText"); // this account sees no device / esta cuenta no ve ningún equipo
        Shot("two-factor-ok-no-devices");
        Assert.Equal("Devices: 0 · online: 0", Text("CountText"));

        // 5. Saved server survives a restart, with the pin and without the password.
        //    El servidor guardado sobrevive a un reinicio, con la huella y sin la contraseña.
        _app!.Close();
        _app.Dispose();
        _app = null;
        Start();
        Assert.Contains(Env("MESHUI_SERVER").Split(':')[0], Find("ServerBox").AsComboBox().EditableText);
        Shot("restart-remembers-server");
        Assert.Equal("Dark", Find("ThemeBox").AsComboBox().SelectedItem?.Text);
        Type("PasswordBox", "Otp-Pass-1234");
        Click("ConnectButton");
        Find("CodeBox"); // straight to two-factor, no certificate question / directo al doble factor, sin preguntar el certificado
        Assert.Null(_automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")));
    }
}
