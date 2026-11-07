using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// Single sign-on as a person does it: Connect with no password, the provider's page in the Windows browser, back in
/// the app. Against the throwaway OpenID Connect provider of tests/oidc (set up by tests/ui/run-ui-test.ps1).
/// Inicio de sesión único como lo hace una persona: Conectar sin contraseña, la página del proveedor en el navegador
/// de Windows y de vuelta en la app. Contra el proveedor OpenID Connect desechable de tests/oidc.
/// </summary>
[Trait("Category", "UI")]
public sealed class SsoTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-sso-ui-");
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private Window Start()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        // Its own browser profile: the provider remembers nobody yet. / Su propio perfil de navegador: el proveedor aún no recuerda a nadie.
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var w = _app.GetMainWindow(_automation, Timeout) ?? throw new Exception("main window did not appear");
        w.SetForeground();
        return w;
    }

    private AutomationElement Find(AutomationElement root, string id) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), Timeout, TimeSpan.FromMilliseconds(250)).Result
        ?? throw new Exception($"'{id}' did not appear / no apareció '{id}'");

    private AutomationElement? Anywhere(string id) => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what) =>
        Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(300)).Result ?? throw new Exception(what + " did not appear");

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        var dir = Env("MESHUI_SHOTS");
        Directory.CreateDirectory(dir);
        Capture.Screen().ToFile(Path.Combine(dir, $"sso-{name}.png"));
    }

    /// <summary>Connect with no password: accepts the certificate and signs in at the provider as <paramref name="user"/>.
    /// Conectar sin contraseña: acepta el certificado y entra en el proveedor como <paramref name="user"/>.</summary>
    private void SignIn(Window win, string user, bool expectProviderPage)
    {
        Find(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Find(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => Anywhere("CertTrust"), TimeSpan.FromSeconds(10)).Result is { } trust) trust.AsButton().Invoke();
        var sso = WaitFor(() => Anywhere("SsoWindow"), "the sign-on window");
        if (!expectProviderPage) return;
        // Both fields are required by the page, as a person would find. / La página exige los dos campos, como se lo encontraría una persona.
        var fields = Retry.WhileFalse(() => sso.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Length >= 2, Timeout, TimeSpan.FromMilliseconds(300)).Result
            ? sso.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            : throw new Exception("the provider's login and password fields did not appear");
        Shot($"{user}-provider-page");
        // A click, as a person does: UI Automation focus does not activate a field inside a web page.
        // Un clic, como una persona: el foco de UI Automation no activa un campo dentro de una página web.
        fields[0].Click();
        Keyboard.Type(user);
        fields[1].Click();
        Keyboard.Type("any-password");
        WaitFor(() => sso.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Sign-in"))), "Sign-in").Click();
        Shot($"{user}-after-sign-in");
        WaitFor(() => sso.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Continue"))), "Continue").Click();
    }

    [Fact]
    public void A_person_signs_in_with_single_sign_on_and_the_provider_remembers_them()
    {
        var win = Start();
        SignIn(win, "alice", expectProviderPage: true);
        Assert.Equal("mesh.test", Find(win, "SessionTitle").Name);
        Assert.StartsWith("alice ·", Find(win, "SessionSubtitle").Name);
        Assert.Null(Anywhere("SsoWindow")); // it closed itself / se cerró sola
        Shot("alice-in");

        // Second time: the provider remembers the person, no page to fill. / La segunda vez el proveedor la recuerda: nada que rellenar.
        Find(win, "DisconnectButton").AsButton().Invoke();
        Find(win, "ConnectButton");
        Assert.Contains("single sign-on", Find(win, "ServerBox").AsComboBox().Items.First().Text);
        SignIn(win, "alice", expectProviderPage: false);
        Assert.StartsWith("alice ·", Find(win, "SessionSubtitle").Name);
        Shot("alice-again");
    }

    /// <summary>MENTIRA: the provider lets "denied" in, but MeshCentral does not: the app must not look signed in.
    /// El proveedor deja entrar a «denied», pero MeshCentral no: la app no puede parecer dentro.</summary>
    [Fact]
    public void Lie_an_account_the_server_refuses_stays_out()
    {
        var win = Start();
        SignIn(win, "denied", expectProviderPage: true);
        var footer = Find(win, "ErrorFooter").Name;
        Shot("denied");
        Assert.EndsWith("Burburito code: 035-001-001-044", footer);
        Assert.Null(win.FindFirstDescendant(cf => cf.ByAutomationId("SessionTitle")));
    }
}
