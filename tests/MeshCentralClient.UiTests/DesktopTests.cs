using System.Diagnostics;
using Regex = System.Text.RegularExpressions.Regex;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// The remote desktop of the real Windows agent, which is THIS machine. Viewer and device share screen and keyboard, so
/// only effects that can come only through the agent count: a click made on the viewer that brings up a window the
/// viewer does not cover, and the Windows key (a toolbar button) opening the real Start menu.
/// El escritorio remoto del agente de Windows real, que es ESTA máquina. Visor y equipo comparten pantalla y teclado, así
/// que solo cuentan efectos que solo pueden llegar por el agente: un clic hecho en el visor que trae al frente una ventana
/// que el visor no tapa, y la tecla Windows (un botón) abriendo el menú Inicio de verdad.
/// </summary>
[Trait("Category", "UI")]
public sealed class DesktopTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-desk-ui-");
    private Application? _app;
    private Process? _notepad;

    public void Dispose()
    {
        try { _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DesktopWindow"))?.AsWindow().Close(); Thread.Sleep(1000); } catch (Exception) { }
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch (Exception) { } }
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what) =>
        Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result ?? throw new Exception(what + " did not appear");

    private AutomationElement Anywhere(string id) => WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(500);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"desktop-{name}.png"));
    }

    private void Until(Func<bool> ok, string what, Func<string>? seen = null)
    {
        if (Retry.WhileFalse(ok, Timeout, TimeSpan.FromMilliseconds(300)).Result) return;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + (seen is null ? "" : " — seen: " + seen()));
    }

    /// <summary>The clipboard text as it is, line breaks included. / El texto del portapapeles tal cual, saltos de línea incluidos.</summary>
    private static string ClipboardExact()
    {
        using var ps = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::Out.Write((Get-Clipboard -Raw))\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })!;
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return text;
    }

    /// <summary>Control characters written out, so a log shows them. / Los caracteres de control escritos, para que se vean en un registro.</summary>
    private static string Visible(string s) =>
        "\"" + string.Concat(s.Select(c => c switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ when char.IsControl(c) || c == '﻿' => $"\\u{(int)c:X4}", _ => c.ToString() })) + "\" (" + s.Length + ")";

    private static void Place(AutomationElement window, int x, int y, int w, int h)
    {
        var t = window.Patterns.Transform.Pattern;
        t.Move(x, y);
        t.Resize(w, h);
    }

    [Fact]
    public void The_remote_desktop_shows_the_screen_and_carries_mouse_and_keys()
    {
        var screen = _automation.GetDesktop().BoundingRectangle;
        output.WriteLine($"screen {screen.Width}x{screen.Height}");

        // A window the viewer will NOT cover. / Una ventana que el visor NO va a tapar.
        _notepad = Process.Start("notepad.exe");
        var notepad = WaitFor(() => _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("Notepad")), "Notepad");
        Place(notepad, 0, 0, screen.Width / 3, screen.Height / 3);

        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        win.SetForeground();
        WaitFor(() => win.FindFirstDescendant(cf => cf.ByAutomationId("ServerBox")), "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        win.FindFirstDescendant(cf => cf.ByAutomationId("UserBox"))!.Click(); Keyboard.Type("admin");
        win.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))!.Click(); Keyboard.Type("Admin-Pass-1234");
        win.FindFirstDescendant(cf => cf.ByAutomationId("ConnectButton"))!.AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        var device = WaitFor(() => win.FindFirstDescendant(cf => cf.ByAutomationId("DeviceList"))?.FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.RightClick();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DesktopItem")), "Open remote desktop").Click();

        var desk = Anywhere("DesktopWindow");
        // The viewer in the lower right part, away from Notepad. / El visor abajo a la derecha, lejos del Bloc de notas.
        Place(desk, screen.Width / 3 + 20, screen.Height / 3 + 20, screen.Width * 2 / 3 - 30, screen.Height * 2 / 3 - 30);
        var status = Anywhere("DesktopStatus");
        var info = new Regex(@"^(\d+) × (\d+) · (\d+)");
        Until(() => info.Match(status.Name) is { Success: true } m && int.Parse(m.Groups[3].Value) > 0, "no image arrived", () => status.Name);
        var size = info.Match(status.Name);
        int w = int.Parse(size.Groups[1].Value), h = int.Parse(size.Groups[2].Value);
        output.WriteLine($"remote {w}x{h}, {status.Name}");
        Assert.Equal(screen.Width, w);   // it really is this screen / de verdad es esta pantalla
        Assert.Equal(screen.Height, h);
        Shot("image");

        // Mouse: click ON THE VIEWER where Notepad is on the remote screen. Locally that click hits the viewer; only the
        // agent's injected click can bring Notepad to the front.
        // Ratón: clic EN EL VISOR donde está el Bloc de notas en la pantalla remota. En local ese clic cae en el visor;
        // solo el clic inyectado por el agente puede traer al frente el Bloc de notas.
        // The app's main window covered Notepad (seen in a screenshot): the remote click landed on it. Minimised now,
        // so the only thing over that spot is Notepad. / La ventana principal tapaba el Bloc de notas (se vio en una
        // captura): el clic remoto caía en ella. Ahora se minimiza y en ese punto solo queda el Bloc de notas.
        win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);
        Thread.Sleep(800);
        var target = notepad.BoundingRectangle;
        var remoteTop = _automation.FromPoint(new System.Drawing.Point(target.X + target.Width / 2, target.Y + target.Height / 2));
        Assert.True(IsInside(remoteTop, notepad), "something still covers Notepad: " + SafeName(remoteTop));
        // The image (a Border has no UI Automation element) is exactly where the remote screen is drawn.
        // La imagen (un Border no tiene elemento de UI Automation) es justo donde se pinta la pantalla remota.
        var surface = Anywhere("DesktopImage").BoundingRectangle;
        var scale = Math.Min((double)surface.Width / w, (double)surface.Height / h);
        var ox = surface.X + (surface.Width - w * scale) / 2;
        var oy = surface.Y + (surface.Height - h * scale) / 2;
        var remote = new System.Drawing.Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        var onViewer = new System.Drawing.Point((int)(ox + remote.X * scale), (int)(oy + remote.Y * scale));
        Assert.True(surface.Contains(onViewer) && !target.Contains(onViewer), "the click point must be on the viewer and off Notepad");
        desk.AsWindow().SetForeground();
        Mouse.Click(onViewer);
        IntPtr Foreground() => GetForegroundWindow();
        Until(() => Foreground() == notepad.Properties.NativeWindowHandle.Value, "the remote click did not bring Notepad to the front");
        Shot("remote-click");

        // Keys: "Type clipboard", pressed through UI Automation (Invoke does not change the foreground window), types
        // into Notepad, which the remote click left in front. Nobody types locally: the text can only come through the agent.
        // Teclas: «Escribir el portapapeles», pulsado por UI Automation (Invoke no cambia la ventana activa), escribe en el
        // Bloc de notas que dejó al frente el clic remoto. Nadie teclea en local: el texto solo puede llegar por el agente.
        const string typed = "hola Mesh 123";
        // clip.exe is the standard way to fill the clipboard from a script; read back to know it is really there.
        // clip.exe es la forma estándar de llenar el portapapeles desde un guion; se relee para saber que está de verdad.
        // Viewer and device share one clipboard here, and leaving the viewer has just read it: a read in that moment comes back empty
        // (seen in a run). So until it reads back. / Aquí visor y equipo comparten un portapapeles, y salir del visor acaba de leerlo: una
        // lectura en ese momento vuelve vacía (visto en una pasada). Así que hasta que se relea.
        // Read back EXACTLY (-Raw, no Trim): a run once typed 15 characters for these 13, and a trimmed read had called the
        // clipboard right. / Se relee EXACTO (-Raw, sin Trim): una pasada escribió 15 caracteres por estos 13, y una lectura
        // recortada había dado el portapapeles por bueno.
        var back = "";
        for (var attempt = 0; attempt < 15 && back != typed; attempt++)
        {
            if (attempt > 0) Thread.Sleep(1000);
            using (var clip = Process.Start(new ProcessStartInfo("clip.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!)
            { clip.StandardInput.Write(typed); clip.StandardInput.Close(); clip.WaitForExit(); }
            back = ClipboardExact();
        }
        output.WriteLine("clipboard: " + Visible(back) + " · " + ClipboardWriter());
        Assert.Equal(typed, back);
        Assert.Equal(notepad.Properties.NativeWindowHandle.Value, GetForegroundWindow());
        var clipNotice = Anywhere("ClipboardNotice");
        output.WriteLine("clipboard notice before the button: " + clipNotice.Name);
        Anywhere("TypeClipboardButton").AsButton().Invoke();
        output.WriteLine("foreground right after the button: " + ForegroundTitle());
        output.WriteLine("clipboard right after the button: " + Visible(ClipboardExact()) + " · " + ClipboardWriter());
        // The app says it sent them: if they do not arrive, the cause is past the app. If the count is wrong, what the clipboard
        // holds NOW says who changed it. / La app dice que los mandó: si no llegan, la causa está más allá de la app. Si la cuenta
        // no cuadra, lo que tiene AHORA el portapapeles dice quién lo cambió.
        var notice = Anywhere("DesktopNotice");
        Until(() => notice.Name.StartsWith("Typed 13 characters"), "the app did not send the text",
            () => notice.Name + " | sync: " + clipNotice.Name + " | clipboard now: " + Visible(ClipboardExact()) + " · " + ClipboardWriter());
        Shot("after-type-clipboard");
        // Where the keys land is checked in KeysTests, without any local window: here, pressing the button through UI
        // Automation activates the viewer (measured: foreground "Remote desktop — …"), so the keys go to it.
        // Dónde caen las teclas se comprueba en KeysTests, sin ninguna ventana local: aquí, pulsar el botón por UI
        // Automation activa el visor (medido: al frente «Remote desktop — …»), así que las teclas van a él.
    }

    private static bool IsInside(AutomationElement? e, AutomationElement window)
    {
        for (var x = e; x is not null; x = x.Parent) if (x.Equals(window)) return true;
        return false;
    }

    private static string SafeName(AutomationElement e) { try { return e.Name ?? ""; } catch (Exception) { return ""; } }
    private static string SafeClass(AutomationElement e) { try { return e.ClassName ?? ""; } catch (Exception) { return ""; } }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Who wrote the clipboard last, as Windows keeps it: the process owning it and the change counter. Two programs write
    /// it here (the app bringing the device's, the agent putting the app's), so a wrong text says which one it was.
    /// Quién escribió el portapapeles por última vez, tal como lo guarda Windows: el proceso dueño y el contador de cambios.
    /// Aquí lo escriben dos programas (la app al traer el del equipo, el agente al poner el de la app), así que un texto
    /// equivocado dice cuál fue.
    /// </summary>
    private static string ClipboardWriter()
    {
        var seq = GetClipboardSequenceNumber();
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return $"seq {seq}, no owner";
        GetWindowThreadProcessId(owner, out var pid);
        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch (ArgumentException) { name = "gone"; }
        return $"seq {seq}, owner {name} ({pid})";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    private static string ForegroundTitle()
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }
}
