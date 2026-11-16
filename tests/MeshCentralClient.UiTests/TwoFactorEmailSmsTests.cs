using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Capture = FlaUI.Core.Capturing.Capture;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// Codes by email and by SMS as a person sets them up in the account window and then signs in with them, on real
/// Windows: the server sends both to the bench's mailbox (tests/server/sink.mjs), which the test reads as the person would
/// read their email or phone. Uses the "mfa" account, made for it by run-ui-test.ps1.
/// Códigos por correo y por SMS como los pone una persona en la ventana de la cuenta y luego entra con ellos: el servidor
/// los manda al buzón del banco, que la prueba lee como la persona leería su correo o su móvil.
/// </summary>
[Trait("Category", "UI")]
public sealed class TwoFactorEmailSmsTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly HttpClient Http = new();
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-2fa-ui-");
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"twofactor-{name}.png"));
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
        if (box.Patterns.Value.PatternOrDefault is { } v) Until(() => v.Value.Value == text, $"\"{text}\" typed in {box.AutomationId}", () => v.Value.Value);
    }

    private static async Task<JsonArray> Texts() => (JsonArray)JsonNode.Parse(await Http.GetStringAsync(Env("MESHUI_SINK") + "/read"))!["sms"]!;

    /// <summary>The newest SMS code after <paramref name="since"/> messages. / El código del último SMS.</summary>
    private static async Task<string> SmsCode(int since)
    {
        for (var i = 0; i < 40; i++)
        {
            var list = await Texts();
            if (list.Count > since) return Regex.Match(list[^1]!["msg"]!.GetValue<string>(), @"\d{6}").Value;
            await Task.Delay(250);
        }
        throw new Exception("no SMS arrived");
    }

    private void SignInForm(AutomationElement win)
    {
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Type(Id(win, "UserBox"), "mfa");
        Id(win, "PasswordBox").Click(); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("Mfa-Pass-1234");
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(10)).Result is { } trust) trust.AsButton().Invoke();
    }

    private static bool Shown(AutomationElement e) { try { return !e.IsOffscreen; } catch (Exception) { return false; } }

    [Fact]
    public async Task A_person_turns_on_email_and_SMS_codes_and_signs_in_with_the_SMS_one()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        SignInForm(win);
        Until(() => Shown(Id(win, "AccountButton")), "signed in", () => Text(win));

        // 1. Email codes on, from the account window. / Códigos por correo, desde la ventana de la cuenta.
        Id(win, "AccountButton").AsButton().Invoke();
        var aw = Anywhere("AccountWindow");
        Until(() => Id(aw, "EmailCodesState").Name.StartsWith("Off", StringComparison.Ordinal), "email codes off", () => Id(aw, "EmailCodesState").Name);
        Id(aw, "EmailCodesButton").AsButton().Invoke();
        Until(() => Id(aw, "EmailCodesState").Name.StartsWith("On", StringComparison.Ordinal), "email codes on", () => Id(aw, "EmailCodesState").Name);

        // 2. A phone, checked with the code it gets by SMS. / Un teléfono, comprobado con el código que le llega.
        var before = (await Texts()).Count;
        Id(aw, "AddPhoneButton").AsButton().Invoke();
        var ask = Anywhere("InputDialog");
        Type(Id(ask, "InputBox"), "+34600000003");
        Id(ask, "InputOk").AsButton().Invoke();
        var code = await SmsCode(before);
        var askCode = Anywhere("InputDialog");
        Type(Id(askCode, "InputBox"), code);
        Shot("phone-code");
        Id(askCode, "InputOk").AsButton().Invoke();
        Until(() => Id(aw, "PhoneState").Name == "+34600000003", "the phone checked", () => Id(aw, "PhoneState").Name);
        Shot("account");
        // Keys are added here with Windows' own window (Windows Hello, a USB key or a phone): the button is there and on.
        Assert.True(Id(aw, "AddKeyButton").IsEnabled, "the add-a-key button");
        aw.AsWindow().Close();

        // 3. Signing in again asks for the code; the SMS one lets in. / Al volver a entrar pide el código; el del SMS deja entrar.
        Id(win, "DisconnectButton").AsButton().Invoke();
        SignInForm(win);
        Until(() => Shown(Id(win, "SmsCodeButton")), "the SMS code button", () => Text(win));
        Assert.True(Shown(Id(win, "KeyButton")), "the use-a-security-key button");
        before = (await Texts()).Count;
        Id(win, "SmsCodeButton").AsButton().Invoke();
        var signInCode = await SmsCode(before);
        Type(Id(win, "CodeBox"), signInCode);
        Shot("sign-in-code");
        Id(win, "ConnectButton").AsButton().Invoke();
        Until(() => Shown(Id(win, "AccountButton")), "signed in with the SMS code", () => Text(win));

        // 4. Both off again, so the account is left as it was. / Los dos se quitan, y la cuenta queda como estaba.
        Id(win, "AccountButton").AsButton().Invoke();
        aw = Anywhere("AccountWindow");
        Id(aw, "EmailCodesButton").AsButton().Invoke();
        Until(() => Id(aw, "EmailCodesState").Name.StartsWith("Off", StringComparison.Ordinal), "email codes off again", () => Id(aw, "EmailCodesState").Name);
        Id(aw, "RemovePhoneButton").AsButton().Invoke();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes"))), "the confirmation").AsButton().Invoke();
        Until(() => Id(aw, "PhoneState").Name == "No phone", "the phone removed", () => Id(aw, "PhoneState").Name);
        output.WriteLine("email and SMS codes on, signed in by SMS, both off");
    }
}
