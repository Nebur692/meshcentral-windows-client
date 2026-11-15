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
using MeshCentralClient.Core;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// Devices without an agent as a person makes and uses them, on real Windows: a group of local devices made from the
/// groups window, a device added by its address (127.0.0.1, which for the server is the server itself), shown as "No
/// agent", and a port mapped to it from its menu: the page that comes back through the local port was fetched by the
/// server itself (localrelay.ashx), with no agent in the way.
/// Equipos sin agente como los hace y usa una persona: un grupo de equipos locales desde la ventana de grupos, un equipo
/// añadido por su dirección (127.0.0.1, que para el servidor es él mismo), que sale «Sin agente», y un puerto redirigido
/// desde su menú: la página que vuelve por el puerto local la trajo el propio servidor, sin agente de por medio.
/// </summary>
[Trait("Category", "UI")]
public sealed class LocalDevicesTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-local-ui-");
    private readonly string _group = "UI11 " + Guid.NewGuid().ToString("N")[..6];
    private readonly string _device = "caja-" + Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        // The group made for the test goes, with its device. / El grupo hecho para la prueba se va, con su equipo.
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private async Task CleanServer()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        if (s.Groups.FirstOrDefault(x => x.Name == _group) is { } g) await s.DeleteGroupAsync(g);
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"local-{name}.png"));
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

    [Fact]
    public async Task A_person_makes_a_group_without_agents_adds_a_device_by_its_address_and_maps_a_port_to_it()
    {
        var win = SignIn();
        var direct = await Title("mesh.test", 4443);

        // 1. The group, as the website asks: without agents, reached by the server itself. / El grupo, sin agentes, por el propio servidor.
        Id(win, "GroupsButton").AsButton().Invoke();
        var gw = Anywhere("GroupsWindow");
        Id(gw, "NewGroupButton").AsButton().Invoke();
        var gf = Anywhere("GroupForm");
        Type(Id(gf, "GroupNameBox"), _group);
        Id(gf, "GroupKindLocal").AsRadioButton().IsChecked = true;
        Until(() => Id(gf, "GroupRelayBox").IsEnabled, "the relay choice enabled");
        Shot("new-group");
        Id(gf, "GroupFormOk").AsButton().Invoke();
        var groups = Id(gw, "GroupList");
        WaitFor(() => groups.FindFirstDescendant(cf => cf.ByName(_group)), "the new group").Click();
        Until(() => Text(gw).Contains("reached through: The server itself"), "how the server reaches it", () => Text(gw));

        // 2. A device by its address. / Un equipo por su dirección.
        Id(gw, "AddLocalDeviceButton").AsButton().Invoke();
        var lf = Anywhere("LocalDeviceForm");
        Type(Id(lf, "LocalNameBox"), _device);
        Type(Id(lf, "LocalHostBox"), "127.0.0.1");
        Shot("add-device");
        Id(lf, "LocalOk").AsButton().Invoke();
        gw.AsWindow().Close();
        var row = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(_device)), "the device in the list");
        Until(() => Text(row).Contains("No agent"), "the device says it has no agent", () => Text(row));
        Shot("list");

        // 3. Its menu: RDP (a Windows device) and port mapping, with its own address fixed. / Su menú: RDP y redirección, con su dirección fija.
        win.SetForeground();
        row.Patterns.SelectionItem.Pattern.Select();
        row.RightClick();
        Assert.True(Anywhere("RdpItem").IsEnabled, "RDP is offered for a Windows device without an agent");
        Anywhere("MapPortItem").AsMenuItem().Invoke();
        var form = Anywhere("MappingForm");
        var host = Id(form, "MappingHostBox");
        Assert.False(host.IsEnabled, "the machine of a device without an agent is fixed");
        Assert.Equal("127.0.0.1", host.Patterns.Value.Pattern.Value.Value);
        Type(Id(form, "MappingRemotePortBox"), "4443");
        Shot("mapping-form");
        Id(form, "MappingOk").AsButton().Invoke();
        var pw = Anywhere("PortsWindow");
        var list = Id(pw, "MappingList");
        Until(() => LocalPort(list) > 0, "the mapping in the list", () => Text(list));
        Assert.Equal(direct, await Title("127.0.0.1", LocalPort(list)));
        Shot("mapping");
        output.WriteLine($"{_device}: {direct} through 127.0.0.1:{LocalPort(list)}");
        pw.AsWindow().Close();
    }

    private static int LocalPort(AutomationElement list) =>
        Regex.Match(Text(list), @"127\.0\.0\.1:(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
}
