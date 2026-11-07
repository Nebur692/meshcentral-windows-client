using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Keys reach the device: the app's own core opens the remote desktop of the real Windows agent (this machine) with no
/// local window at all, clicks remotely on Notepad and types into it — Unicode keys (what "Type clipboard" sends) and
/// normal keys. Without a local window nothing local can take the keys, so what appears in Notepad came through the agent.
/// Las teclas llegan al equipo: el núcleo de la propia app abre el escritorio remoto del agente de Windows real (esta
/// máquina) sin ninguna ventana local, hace clic en remoto sobre el Bloc de notas y escribe en él: teclas Unicode (lo que
/// manda «Escribir el portapapeles») y teclas normales. Sin ventana local nada local puede quedarse las teclas.
/// </summary>
[Trait("Category", "UI")]
public sealed class KeysTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private readonly UIA3Automation _automation = new();

    public void Dispose()
    {
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch (Exception) { } }
        _automation.Dispose();
    }

    [Fact]
    public async Task Unicode_and_normal_keys_reach_the_device()
    {
        Process.Start("notepad.exe");
        var notepad = Retry.WhileNull(() => _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("Notepad")), TimeSpan.FromSeconds(30)).Result!;
        var t = notepad.Patterns.Transform.Pattern;
        t.Move(100, 100);
        t.Resize(500, 300);
        var r = notepad.BoundingRectangle;

        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var server));
        await using var s = await MeshServerSession.ConnectAsync(server!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        var dev = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName);
        await using var desk = await DesktopSession.OpenAsync(s, dev.Id);
        var size = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desk.ScreenSizeChanged += (_, _) => size.TrySetResult();
        await size.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(1000);

        // A remote click on Notepad's text area. / Un clic remoto en la zona de texto del Bloc de notas.
        int x = r.X + r.Width / 2, y = r.Y + r.Height / 2;
        await desk.SendMouseMoveAsync(x, y);
        await desk.SendMouseButtonAsync(MouseButton.Left, true, x, y);
        await desk.SendMouseButtonAsync(MouseButton.Left, false, x, y);
        Assert.True(Retry.WhileFalse(() => GetForegroundWindow() == notepad.Properties.NativeWindowHandle.Value, TimeSpan.FromSeconds(15)).Result, "the remote click did not bring Notepad up");

        const string typed = "hola Mesh 123 ñ€";
        foreach (var c in typed) await desk.SendCharAsync(c);           // Unicode keys / teclas Unicode
        await desk.SendKeyAsync(13, true); await desk.SendKeyAsync(13, false); // Enter
        await desk.SendKeyAsync(0x58, true); await desk.SendKeyAsync(0x58, false); // X (normal key / tecla normal)

        var edit = notepad.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.Document)))!;
        string Text() { try { return edit.Patterns.Value.IsSupported ? edit.Patterns.Value.Pattern.Value.Value : edit.Patterns.Text.Pattern.DocumentRange.GetText(-1); } catch (Exception) { return ""; } }
        var ok = Retry.WhileFalse(() => Text().Replace("\r", "").Contains(typed + "\n") && Text().TrimEnd().EndsWith("x", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20)).Result;
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), "keys-notepad.png"));
        output.WriteLine($"notepad: \"{Text()}\"");
        Assert.True(ok, $"keys did not arrive: \"{Text()}\"");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
