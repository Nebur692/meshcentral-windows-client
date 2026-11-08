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
/// Copy and paste of everything with the remote desktop, on real Windows where the viewer and the device are the same
/// machine and so share ONE clipboard. What proves each direction is where things end up: files sent to the device are
/// put on its clipboard from the device's copy folder (the remote user's TEMP\MeshCentralClient\clip), files brought from
/// it from the app's own folder (LOCALAPPDATA\MeshCentralClient\clipboard); text and images, by the viewer's notice and
/// by being intact after the trip.
/// Copiar y pegar de todo con el escritorio remoto, en Windows de verdad donde visor y equipo son la misma máquina y
/// comparten UN portapapeles. Lo que prueba cada sentido es dónde acaba cada cosa: los ficheros llevados al equipo quedan
/// en su portapapeles desde la carpeta de copias del equipo, los traídos desde la carpeta de la app; texto e imágenes,
/// por el aviso del visor y por llegar intactos.
/// </summary>
[Trait("Category", "UI")]
public sealed class DesktopClipboardTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-clip-ui-");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DesktopWindow"))?.AsWindow().Close(); Thread.Sleep(1000); } catch (Exception) { }
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        try { ClipboardTool.SetText(" "); } catch (Exception) { }
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
        if (Retry.WhileFalse(condition, Timeout, TimeSpan.FromMilliseconds(300)).Result) return;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + (seen is null ? "" : " — seen: " + seen()));
    }

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"clipboard-{name}.png"));
    }

    private static string Folder(string name, string content)
    {
        var d = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName;
        File.WriteAllText(Path.Combine(d, "a.txt"), content);
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(d, "sub", "b.txt"), "b " + content);
        return d;
    }

    [Fact]
    public void Text_images_and_files_go_both_ways_with_the_remote_desktop()
    {
        // Already on the clipboard when the desktop opens: it goes to the device. / Ya en el portapapeles al abrir: va al equipo.
        var t0 = "inicio ñ€ " + _tag;
        ClipboardTool.SetText(t0);

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
        var device = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.Patterns.SelectionItem.Pattern.Select();
        device.RightClick();
        Anywhere("DesktopItem").AsMenuItem().Invoke();
        var desk = Anywhere("DesktopWindow");
        var notice = Id(desk, "ClipboardNotice");
        // Visible only when images and files can go too (Windows, run commands and files rights). / Solo visible si también pueden ir imágenes y ficheros.
        Id(desk, "ClipToDeviceButton");
        string Notice() => notice.Name;

        // 1. Text from this PC, on opening. / Texto de este PC, al abrir.
        Until(() => Notice() == $"Copied text ({t0.Length} characters) is on the device.", "the text taken to the device", Notice);
        Shot("text-to-device");

        // 2. Files copied on the device come here, from the app's folder. / Ficheros copiados en el equipo vienen aquí, desde la carpeta de la app.
        var there = Folder("mcc-from-device-" + _tag, "desde el equipo " + _tag);
        ClipboardTool.SetFiles(there);
        win.SetForeground();
        Until(() => Notice() == "1 item(s) from the device ready to paste here.", "the files brought from the device", Notice);
        var local = Assert.Single(ClipboardTool.Files());
        output.WriteLine("brought: " + local);
        Assert.StartsWith(Path.Combine(_dir.FullName, "localappdata", "MeshCentralClient", "clipboard"), local, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("desde el equipo " + _tag, File.ReadAllText(Path.Combine(local, "a.txt")));
        Assert.Equal("b desde el equipo " + _tag, File.ReadAllText(Path.Combine(local, "sub", "b.txt")));

        // 3. Files copied here go to the device, into its copy folder. / Ficheros copiados aquí van al equipo, a su carpeta de copias.
        var here = Folder("mcc-to-device-" + _tag, "desde este PC " + _tag);
        ClipboardTool.SetFiles(here);
        desk.AsWindow().SetForeground();
        Until(() => Notice() == "1 item(s) ready to paste on the device.", "the files taken to the device", Notice);
        var remote = Assert.Single(ClipboardTool.Files());
        output.WriteLine("taken: " + remote);
        Assert.Contains("\\MeshCentralClient\\clip\\", remote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("desde este PC " + _tag, File.ReadAllText(Path.Combine(remote, "a.txt")));
        Assert.Equal("b desde este PC " + _tag, File.ReadAllText(Path.Combine(remote, "sub", "b.txt")));
        Shot("files-to-device");

        // 4. An image copied on the device comes here intact. / Una imagen copiada en el equipo llega intacta.
        var red = unchecked((int)0xFFD02020);
        ClipboardTool.SetImage(64, 48, red);
        win.SetForeground();
        Until(() => Notice() == "The device's image is on this PC.", "the image brought from the device", Notice);
        Assert.Equal($"64x48:{red}", ClipboardTool.Image());

        // 5. An image copied here goes to the device intact. / Una imagen copiada aquí llega intacta al equipo.
        var blue = unchecked((int)0xFF2040C0);
        ClipboardTool.SetImage(80, 30, blue);
        desk.AsWindow().SetForeground();
        Until(() => Notice() == "The image (80×30) is on the device.", "the image taken to the device", Notice);
        Assert.Equal($"80x30:{blue}", ClipboardTool.Image());

        // 6. Text copied on the device comes here. / Texto copiado en el equipo viene aquí.
        var t2 = "desde el equipo «ñ» " + _tag + "\r\nsegunda línea";
        ClipboardTool.SetText(t2);
        win.SetForeground();
        Until(() => Notice().StartsWith("Text copied on the device (", StringComparison.Ordinal), "the text brought from the device", Notice);
        Assert.Equal(t2, ClipboardTool.Text());
        Shot("text-from-device");

        // 7. A long text, far larger than any terminal screen, comes whole. / Un texto largo, mucho mayor que una pantalla de terminal, llega entero.
        desk.AsWindow().SetForeground();
        Thread.Sleep(1500);
        var longText = string.Concat(Enumerable.Repeat("línea larga ñ€ " + _tag + "\r\n", 800));
        ClipboardTool.SetLongText(longText);
        win.SetForeground();
        Until(() => System.Text.RegularExpressions.Regex.Match(Notice(), @"^Text copied on the device \((\d+) characters\)") is { Success: true } m && int.Parse(m.Groups[1].Value) > 10_000,
            "the long text brought from the device", Notice);
        Assert.Equal(longText.TrimEnd(), ClipboardTool.Text().TrimEnd());

        foreach (var d in new[] { there, here }) { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
