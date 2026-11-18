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
/// What a person does with the device, the account and the server's files, on real Windows: a guest link made, seen and
/// ended; the device's installed programs; notices when it connects; previous sign-ins; and the server's own files: a
/// folder made, a file uploaded and downloaded the same, then deleted.
/// Lo que hace una persona con el equipo, la cuenta y los ficheros del servidor: un enlace de invitado creado, visto y
/// acabado; los programas del equipo; avisos al conectarse; las entradas anteriores; y los ficheros del propio servidor.
/// </summary>
[Trait("Category", "UI")]
public sealed class Phase17Tests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-f17-ui-");
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"f17-{name}.png"));
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
        Anywhere("MoreMenu").AsMenuItem().Open();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(item)), item).Click();
    }

    [Fact]
    public void A_person_shares_the_device_with_a_guest()
    {
        var win = SignIn();

        // A guest link: made, listed, then ended. / Un enlace de invitado: creado, listado y acabado.
        var guest = "Invitado " + _tag;
        Menu(win, "ShareItem");
        var sw = Anywhere("SharingWindow");
        Type(Id(sw, "ShareGuest"), guest);
        Id(sw, "ShareCreate").AsButton().Invoke();
        Until(() => Id(sw, "ShareLink").AsTextBox().Text.StartsWith("https://", StringComparison.Ordinal), "the guest link", () => Id(sw, "ShareLink").AsTextBox().Text);
        var row = WaitFor(() => Id(sw, "ShareList").FindFirstDescendant(cf => cf.ByName(guest)), "the link in the list");
        Shot("share");
        row.Click();
        Id(sw, "ShareRemove").AsButton().Invoke();
        Yes();
        Until(() => Id(sw, "ShareList").FindFirstDescendant(cf => cf.ByName(guest)) is null, "the link gone");
    }

    [Fact]
    public void A_person_sees_the_programs_of_the_device()
    {
        var win = SignIn();
        // The device's installed programs. / Los programas instalados del equipo.
        Menu(win, "SoftwareItem");
        var pw = Anywhere("SoftwareWindow");
        Until(() => Id(pw, "SoftwareList").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 3, "the installed programs", () => Text(Id(pw, "SoftwareStatus")));
        Shot("software");
    }

    [Fact]
    public async Task A_person_asks_for_a_notice_when_the_device_connects()
    {
        var win = SignIn();
        // An email when it connects: kept by the server for this account. / Un correo al conectarse: lo guarda el servidor.
        Menu(win, "DeviceNotifyItem");
        var nd = Anywhere("NotifyDialog");
        Id(nd, "NotifyEmailConnect").AsCheckBox().IsChecked = true;
        Id(nd, "NotifyOk").AsButton().Invoke();
        await using var s = await Admin();
        var dev = (await s.GetDevicesAsync()).First(d => d.Name == Environment.MachineName);
        Until(() => s.NotifyOn(dev.Id).HasFlag(DeviceNotify.EmailConnect), "the notice kept by the server");
        await s.SetDeviceNotifyAsync(dev, DeviceNotify.None);
    }

    [Fact]
    public async Task A_person_sees_the_previous_sign_ins()
    {
        // One failed sign-in through the website, which is what the server notes. / Una entrada fallida por la web.
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        var pin = Env("MESHUI_CERT_SHA256").Replace(":", "", StringComparison.Ordinal);
        using (var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c is not null && string.Equals(c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase) })
        using (var http = new HttpClient(h))
            (await http.PostAsync(a!.BaseUri, new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "login", ["username"] = "admin", ["password"] = "Not-The-Pass-" + _tag }))).Dispose();

        var win = SignIn();
        Id(win, "AccountButton").AsButton().Invoke();
        var aw = Anywhere("AccountWindow");
        Id(aw, "LoginsButton").AsButton().Invoke();
        var lw = Anywhere("LoginsWindow");
        Until(() => Id(lw, "LoginsList").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 0, "the previous sign-ins", () => Text(Id(lw, "LoginsStatus")));
        Until(() => Text(Id(lw, "LoginsList")).Contains("Wrong user or password", StringComparison.Ordinal), "the failed sign-in", () => Text(Id(lw, "LoginsList")));
        Shot("logins");
    }

    [Fact]
    public void A_person_keeps_files_on_the_server()
    {
        var local = Path.Combine(_dir.FullName, "nota-" + _tag + ".txt");
        File.WriteAllText(local, "Hola desde la prueba " + _tag);
        var folder = "ui17-" + _tag;

        var win = SignIn();
        Id(win, "ServerMenuButton").AsButton().Invoke();
        Anywhere("ServerFilesItem").AsMenuItem().Invoke();
        var fw = Anywhere("ServerFilesWindow");
        Id(fw, "SfNewFolder").AsButton().Invoke();
        Input(folder);
        var row = WaitFor(() => Id(fw, "SfList").FindFirstDescendant(cf => cf.ByName(folder)), "the new folder");
        row.DoubleClick();
        Until(() => Id(fw, "SfPath").Name == "/" + folder, "inside the folder", () => Id(fw, "SfPath").Name);

        Id(fw, "SfUpload").AsButton().Invoke();
        FileDialog("Upload…", local);
        var file = WaitFor(() => Id(fw, "SfList").FindFirstDescendant(cf => cf.ByName(Path.GetFileName(local))), "the uploaded file");
        file.Click();
        var saved = Path.Combine(_dir.FullName, "bajado.txt");
        Id(fw, "SfDownload").AsButton().Invoke();
        FileDialog("Download…", saved);
        Until(() => File.Exists(saved) && File.ReadAllText(saved) == File.ReadAllText(local), "the same file downloaded");
        Shot("server-files");

        Id(fw, "SfUp").AsButton().Invoke();
        WaitFor(() => Id(fw, "SfList").FindFirstDescendant(cf => cf.ByName(folder)), "the folder again").Click();
        Id(fw, "SfDelete").AsButton().Invoke();
        Yes();
        Until(() => Id(fw, "SfList").FindFirstDescendant(cf => cf.ByName(folder)) is null, "the folder gone");
    }
}
