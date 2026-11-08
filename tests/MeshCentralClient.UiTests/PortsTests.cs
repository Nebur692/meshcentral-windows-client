using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
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
/// Port mapping and Windows Remote Desktop as a person uses them, on real Windows: the agent of this machine carries the
/// connections to the test server's own website (127.0.0.1:4443 from the agent), so a page that comes back through the
/// local port crossed the relay. / Redirección de puertos y Escritorio remoto de Windows como los usa una persona: el
/// agente de esta máquina lleva las conexiones a la web del servidor de prueba, así que una página que vuelve por el
/// puerto local cruzó el relé.
/// </summary>
[Trait("Category", "UI")]
public sealed class PortsTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-ports-ui-");
    private Application? _app;
    private Process? _mstsc;

    public void Dispose()
    {
        try { _mstsc?.Kill(true); } catch (Exception) { }
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"ports-{name}.png"));
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

    private AutomationElement SignIn()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        Connect(win);
        return win;
    }

    private void Connect(AutomationElement win)
    {
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Type(Id(win, "UserBox"), "admin");
        Id(win, "PasswordBox").Click(); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("Admin-Pass-1234");
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
    }

    private void DeviceMenu(AutomationElement win, string item)
    {
        win.SetForeground();
        var device = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.Patterns.SelectionItem.Pattern.Select();
        device.RightClick();
        Anywhere(item).AsMenuItem().Invoke();
    }

    /// <summary>The page's title through any address. / El título de la página por cualquier dirección.</summary>
    private static async Task<string> Title(string host, int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("mesh.test");
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: mesh.test:4443\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(tls, Encoding.UTF8);
        var all = await reader.ReadToEndAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        var m = Regex.Match(all, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(m.Success, "no title in: " + all[..Math.Min(300, all.Length)]);
        return m.Groups[1].Value;
    }

    private static bool Listening(int port)
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", port); return true; }
        catch (SocketException) { return false; }
    }

    private static int LocalPort(AutomationElement list) =>
        Regex.Match(Text(list), @"127\.0\.0\.1:(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    [Fact]
    public async Task A_person_maps_a_port_uses_it_gets_it_back_at_sign_in_and_removes_it()
    {
        var win = SignIn();
        var direct = await Title("mesh.test", 4443);

        // 1. From the device menu: the server's web port, as the device sees it, remembered. / Desde el menú del equipo: el puerto web, recordado.
        DeviceMenu(win, "MapPortItem");
        var form = Anywhere("MappingForm");
        Type(Id(form, "MappingRemotePortBox"), "4443");
        Id(form, "MappingRemember").AsCheckBox().IsChecked = true;
        Id(form, "MappingOk").AsButton().Invoke();
        var pw = Anywhere("PortsWindow");
        var list = Id(pw, "MappingList");
        Until(() => LocalPort(list) > 0, "the mapping in the list", () => Text(list));
        var port = LocalPort(list);
        Assert.Equal(direct, await Title("127.0.0.1", port));
        Until(() => Text(list).Contains("1 in all"), "the connection counted", () => Text(list));
        Shot("mapping");

        // 2. Copy address puts it on the clipboard as another program sees it. / Copiar la dirección la pone en el portapapeles.
        list.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem))!.Patterns.SelectionItem.Pattern.Select();
        Id(pw, "CopyMappingButton").AsButton().Invoke();
        Until(() => ClipboardTool.Text() == $"127.0.0.1:{port}", "the address on the clipboard", ClipboardTool.Text);
        pw.AsWindow().Close();

        // 3. Sign out and in: the remembered mapping is back on the same port. / Salir y entrar: la recordada vuelve en el mismo puerto.
        Id(win, "DisconnectButton").AsButton().Invoke();
        Until(() => !Listening(port), "the port closed at sign-out");
        Connect(win);
        Until(() => Id(win, "ActionStatus").Name.Contains("Port mappings back: 1"), "the mapping back", () => Id(win, "ActionStatus").Name);
        Assert.Equal(direct, await Title("127.0.0.1", port));

        // 4. Removed: the port closes and is not remembered any more. / Quitada: el puerto se cierra y ya no se recuerda.
        Id(win, "PortsButton").AsButton().Invoke();
        pw = Anywhere("PortsWindow");
        list = Id(pw, "MappingList");
        WaitFor(() => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)), "the row").Patterns.SelectionItem.Pattern.Select();
        Id(pw, "RemoveMappingButton").AsButton().Invoke();
        Until(() => !Listening(port), "the port closed after removing it");
        Assert.DoesNotContain("\"RemotePort\": 4443", File.ReadAllText(Path.Combine(_dir.FullName, "appdata", "MeshCentralClient", "relays.json")));
    }

    [Fact]
    public void Windows_Remote_Desktop_opens_through_a_private_port_that_closes_with_it()
    {
        var win = SignIn();
        var before = Process.GetProcessesByName("mstsc").Select(p => p.Id).ToHashSet();
        DeviceMenu(win, "RdpItem");
        Until(() => Process.GetProcessesByName("mstsc").Any(p => !before.Contains(p.Id)), "mstsc started");
        _mstsc = Process.GetProcessesByName("mstsc").First(p => !before.Contains(p.Id));
        var command = CommandLine(_mstsc.Id);
        output.WriteLine("mstsc: " + command);
        var m = Regex.Match(command, @"/v:127\.0\.0\.1:(\d+)");
        Assert.True(m.Success, command);
        var port = int.Parse(m.Groups[1].Value);
        Until(() => Id(win, "ActionStatus").Name.Contains($"127.0.0.1:{port}"), "the status naming the port", () => Id(win, "ActionStatus").Name);
        Assert.True(Listening(port));
        Thread.Sleep(3000);
        Shot("rdp");
        Id(win, "PortsButton").AsButton().Invoke();
        var pw = Anywhere("PortsWindow");
        output.WriteLine("mapping: " + Text(Id(pw, "MappingList")));
        Assert.Contains("RDP", Text(Id(pw, "MappingList")));
        _mstsc.Kill(true);
        Until(() => !Listening(port), "the port closed with Remote Desktop");
        Until(() => !Text(Id(pw, "MappingList")).Contains("RDP"), "the row gone", () => Text(Id(pw, "MappingList")));
    }

    private static string CommandLine(int pid)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.Trim();
    }
}
