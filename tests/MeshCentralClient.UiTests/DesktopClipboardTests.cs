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
/// it become virtual ones that only Explorer's real paste can turn into files; text and images, by the viewer's notice and
/// by being intact after the trip.
/// Copiar y pegar de todo con el escritorio remoto, en Windows de verdad donde visor y equipo son la misma máquina y
/// comparten UN portapapeles. Lo que prueba cada sentido es dónde acaba cada cosa: los ficheros llevados al equipo quedan
/// en su portapapeles desde la carpeta de copias del equipo, los traídos pasan a ser virtuales, que solo el pegado de verdad
/// del Explorador convierte en ficheros; texto e imágenes, por el aviso del visor y por llegar intactos.
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
    private Window? _explorer;

    public void Dispose()
    {
        try { _explorer?.Close(); } catch (Exception) { }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    /// <summary>Ctrl and Alt up for a whole second. / Ctrl y Alt sueltas durante un segundo entero.</summary>
    private static bool ModifiersQuiet()
    {
        for (var i = 0; i < 20; i++)
        {
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    /// <summary>The window is in front and the focused element is in its process. / La ventana está delante y el elemento con el foco es de su proceso.</summary>
    private bool HoldsKeyboard(Window window)
    {
        try
        {
            return GetForegroundWindow() == window.Properties.NativeWindowHandle.ValueOrDefault
                && _automation.FocusedElement() is { } f && f.Properties.ProcessId.ValueOrDefault == window.Properties.ProcessId.ValueOrDefault;
        }
        catch (Exception) { return false; }
    }

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

    private (AutomationElement Main, AutomationElement Desktop) OpenDesktop()
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
        var device = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.Patterns.SelectionItem.Pattern.Select();
        device.RightClick();
        Anywhere("DesktopItem").AsMenuItem().Invoke();
        return (win, Anywhere("DesktopWindow"));
    }

    /// <summary>Ctrl+V in a real Explorer window on the folder, the gesture of a person. / Ctrl+V en una ventana de verdad del Explorador sobre la carpeta, el gesto de una persona.</summary>
    private void PasteInExplorer(string folder, params AutomationElement[] inTheWay)
    {
        _explorer ??= OpenExplorer(folder);
        // Windows does not bring to the front a window another process opened, it only flashes it (seen in a capture): nothing
        // may cover it, and a click activates it. / Windows no trae al frente una ventana que abrió otro proceso, solo la hace
        // parpadear (visto en una captura): nada debe taparla, y un clic la activa.
        foreach (var w in inTheWay) w.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
        Thread.Sleep(500);
        _explorer.Click();
        Thread.Sleep(500);
        Shot("explorer-before-paste");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    }

    /// <summary>WPF keeps a hidden element in the automation tree for a while, only marked as off screen. / WPF deja un rato en el árbol de automatización un elemento escondido, solo marcado fuera de pantalla.</summary>
    private static bool Shown(AutomationElement root, string id) =>
        root.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e && !e.IsOffscreen;

    private static void Restore(params AutomationElement[] windows)
    {
        foreach (var w in windows) w.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
    }

    private Window OpenExplorer(string folder)
    {
        Process.Start("explorer.exe", "\"" + folder + "\"");
        return WaitFor(() => _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("CabinetWClass")), "the Explorer window").AsWindow();
    }

    /// <summary>A file bigger than the moment Explorer takes to show its window. / Un fichero mayor que lo que tarda el Explorador en enseñar su ventana.</summary>
    private static string BigFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        var block = new byte[1024 * 1024];
        new Random(5).NextBytes(block);
        using var f = File.Create(path);
        for (var i = 0; i < 400; i++) f.Write(block);
        return path;
    }

    [Fact]
    public void Text_images_and_files_go_both_ways_with_the_remote_desktop()
    {
        // Already on the clipboard when the desktop opens: it goes to the device. / Ya en el portapapeles al abrir: va al equipo.
        var t0 = "inicio ñ€ " + _tag;
        ClipboardTool.SetText(t0);
        var (win, desk) = OpenDesktop();
        var notice = Id(desk, "ClipboardNotice");
        // Visible only when images and files can go too (Windows, run commands and files rights). / Solo visible si también pueden ir imágenes y ficheros.
        Id(desk, "ClipToDeviceButton");
        string Notice() => notice.Name;

        // 1. Text from this PC, on opening. / Texto de este PC, al abrir.
        Until(() => Notice() == $"Copied text ({t0.Length} characters) is on the device.", "the text taken to the device", Notice);
        Shot("text-to-device");

        // 2. Files copied on the device: a promise on the clipboard, and the bytes when Explorer pastes. / Ficheros copiados en el equipo: una promesa en el portapapeles, y los bytes cuando pega el Explorador.
        var there = Folder("mcc-from-device-" + _tag, "desde el equipo " + _tag);
        ClipboardTool.SetFiles(there);
        win.SetForeground();
        const string offered = "1 item(s) from the device: paste them here and they will come, with Windows' own bar.";
        Until(() => Notice() == offered, "the files offered from the device", Notice);
        Assert.Empty(ClipboardTool.Files());
        var target = Directory.CreateDirectory(Path.Combine(_dir.FullName, "pasted")).FullName;
        var pasted = Path.Combine(target, Path.GetFileName(there));
        PasteInExplorer(target, win, desk);
        Until(() => File.Exists(Path.Combine(pasted, "sub", "b.txt")) && new FileInfo(Path.Combine(pasted, "sub", "b.txt")).Length > 0, "the folder pasted by Explorer");
        Assert.Equal("desde el equipo " + _tag, File.ReadAllText(Path.Combine(pasted, "a.txt")));
        Assert.Equal("b desde el equipo " + _tag, File.ReadAllText(Path.Combine(pasted, "sub", "b.txt")));
        Restore(win, desk);

        // 2b. A big one: Explorer's own window with its bar, what Remote Desktop shows. / Uno grande: la ventana del propio Explorador con su barra, lo que enseña Escritorio remoto.
        desk.AsWindow().SetForeground();
        Thread.Sleep(1500);
        var big = BigFile("mcc-big-" + _tag + ".bin");
        ClipboardTool.SetFiles(big);
        win.SetForeground();
        // The notice reads the same as before: what tells is the real file leaving the clipboard. / El aviso dice lo mismo que antes: lo que distingue es que el fichero de verdad deja el portapapeles.
        Until(() => ClipboardTool.Files().Length == 0 && Notice() == offered, "the big file offered from the device", Notice);
        PasteInExplorer(target, win, desk);
        // Its title as seen in a capture of this machine: Windows in English. / Su título tal como se vio en una captura de esta máquina: Windows en inglés.
        AutomationElement? CopyWindow() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Copying..."));
        var copying = Retry.WhileNull(CopyWindow, Timeout, TimeSpan.FromMilliseconds(200)).Result;
        Shot("explorer-copy-window");
        Assert.True(copying is not null, "Explorer never showed its copy window");
        var pastedBig = Path.Combine(target, Path.GetFileName(big));
        Until(() => CopyWindow() is null && File.Exists(pastedBig), "the big file pasted");
        Assert.Equal(new FileInfo(big).Length, new FileInfo(pastedBig).Length);
        using (var a = File.OpenRead(big)) using (var b = File.OpenRead(pastedBig)) Assert.Equal(System.Security.Cryptography.SHA256.HashData(a), System.Security.Cryptography.SHA256.HashData(b));
        Restore(win, desk);

        // 2c. Taking it to the device shows a bar, and Cancel stops it. / Llevarlo al equipo enseña una barra, y Cancelar lo para.
        // Only coming into the viewer takes it: first somewhere else. / Solo entrar en el visor lo lleva: primero a otro sitio.
        win.SetForeground();
        Thread.Sleep(3000);
        ClipboardTool.SetFiles(big);
        desk.AsWindow().SetForeground();
        Until(() => Shown(desk, "ClipCancelButton") && Shown(desk, "ClipProgress"), "the bar and Cancel while taking it", Notice);
        Shot("taking-with-bar");
        Id(desk, "ClipCancelButton").AsButton().Invoke();
        Until(() => Notice() == "Cancelled: nothing was left on the device's clipboard.", "the transfer cancelled", Notice);
        Until(() => !Shown(desk, "ClipCancelButton"), "Cancel leaving when it ended");
        // Leaving reads the device's clipboard (the same file, nothing new); let it end before copying something else.
        // Al salir se lee el portapapeles del equipo (el mismo fichero, nada nuevo); que acabe antes de copiar otra cosa.
        win.SetForeground();
        Thread.Sleep(3000);
        File.Delete(big);

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

    [Fact]
    public void Full_screen_covers_the_screen_and_the_bar_floats_and_hides()
    {
        var (_, desk) = OpenDesktop();
        var window = desk.AsWindow();
        var normal = window.BoundingRectangle;

        // The viewer offers what the device's menu offers: from it, a terminal opens. / El visor ofrece lo que ofrece el menú del equipo: desde él se abre una terminal.
        Id(desk, "DeviceMenuButton").AsButton().Invoke();
        foreach (var offered in new[] { "ViewerDetailsItem", "ViewerFilesItem", "ViewerRunCommandsItem", "ViewerPowerMenu" }) Anywhere(offered);
        Assert.Null(_automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("ViewerDesktopItem"))); // already here / ya está aquí
        Shot("viewer-device-menu");
        Anywhere("ViewerTerminalItem").AsMenuItem().Invoke();
        var term = Anywhere("TerminalWindow");
        term.AsWindow().Close();
        window.SetForeground();
        // A menu closes if its window is still coming to the front, so it is opened again until it stays. / Un menú se cierra si su ventana aún está llegando al frente, así que se abre otra vez hasta que se quede.
        Until(() =>
        {
            Id(desk, "KeysMenuButton").AsButton().Invoke();
            return Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("LockInputItem")), TimeSpan.FromSeconds(3)).Result is not null;
        }, "the keys menu");
        Shot("viewer-keys-menu");
        Keyboard.Type(VirtualKeyShort.ESCAPE);

        var screen = _automation.GetDesktop().BoundingRectangle;
        Id(desk, "FullScreenButton").AsButton().Invoke();
        Until(() => window.BoundingRectangle.Width >= screen.Width && window.BoundingRectangle.Height >= screen.Height, "the window covering the screen", () => window.BoundingRectangle.ToString());
        Shot("full-screen-bar-floating");
        // It hides by itself with the mouse away; the top edge brings it back. / Se esconde sola con el ratón lejos; el borde de arriba la devuelve.
        Mouse.MoveTo(screen.Width / 2, screen.Height / 2);
        Until(() => !Shown(desk, "FullScreenButton"), "the bar hiding");
        Shot("full-screen-bar-hidden");
        Mouse.MoveTo(screen.Width / 2, screen.Height / 3);
        Mouse.MoveTo(screen.Width / 2, 0);
        Until(() => Shown(desk, "FullScreenButton"), "the bar back");
        Shot("full-screen-bar-back");
        Id(desk, "FullScreenButton").AsButton().Invoke();
        // The usual size can be as wide as a small screen, so it is compared with itself. / El tamaño normal puede ser tan ancho como una pantalla pequeña, así que se compara consigo mismo.
        Until(() => window.BoundingRectangle == normal, "the window back to its size", () => window.BoundingRectangle.ToString());

        // The same keys as Remote Desktop, there and back, with the picture holding the keyboard. / Las mismas teclas que Escritorio remoto, ida y vuelta, con el teclado en la imagen.
        // Leaving full screen hands the keyboard back to the picture. / Al salir de pantalla completa el teclado vuelve a la imagen.
        window.SetForeground();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.PAUSE);
        Until(() => window.BoundingRectangle.Height >= screen.Height, "full screen by keys", () => window.BoundingRectangle.ToString());
        // The new frame takes the keyboard back a moment later; a person never presses again within those milliseconds.
        // El marco nuevo recupera el teclado un momento después; una persona nunca vuelve a pulsar en esos milisegundos.
        Until(() => HoldsKeyboard(window), "the keyboard back in full screen", () => _automation.FocusedElement()?.Properties.ProcessId.ValueOrDefault.ToString() ?? "none");
        // Here the device IS this PC: the Ctrl and Alt the viewer sent come back through the agent into this same keyboard, and one
        // landing in the middle of the next press breaks it. A real remote device has its own keyboard.
        // Aquí el equipo ES este PC: el Ctrl y el Alt que mandó el visor vuelven por el agente a este mismo teclado, y uno que caiga en
        // mitad de la siguiente pulsación la rompe. Un equipo remoto de verdad tiene su propio teclado.
        Until(ModifiersQuiet, "Ctrl and Alt quiet after the echo from the agent");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.PAUSE);
        Until(() => window.BoundingRectangle == normal, "back by keys", () => window.BoundingRectangle.ToString());
    }
}
