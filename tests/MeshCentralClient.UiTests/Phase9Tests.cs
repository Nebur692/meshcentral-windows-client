using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MeshCentralClient.Core;
using Xunit;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// What phase 9 brought, as a person uses it on real Windows against the real server: a chat with the person at this
/// very machine (the person is played by what the chat page does: it joins the relay), the agent update asked and its
/// confirmation, a group's consent and features, and the server's recordings. What the app shows is checked against
/// the server itself, and the person's side against what reached it.
/// Lo que trajo la fase 9, como lo usa una persona en Windows de verdad contra el servidor real: un chat con la persona
/// de esta misma máquina (la persona la hace lo que hace la página de chat: entrar en el relé), pedir actualizar el
/// agente y su confirmación, el consentimiento y las funciones de un grupo, y las grabaciones del servidor.
/// </summary>
[Trait("Category", "UI")]
public sealed class Phase9Tests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-phase9-ui-");
    private readonly string _group = "UI9 " + Guid.NewGuid().ToString("N")[..6];
    private Application? _app;
    private readonly DateTime _started = DateTime.Now;

    /// <summary>
    /// The agent of this machine opens the chat page in its browser, as it must on a person's PC (measured on CI: an Edge
    /// window stays open and takes the keyboard from the next test). Only the browsers started during this test close.
    /// El agente de esta máquina abre la página del chat en su navegador, como debe en el PC de una persona (medido en la
    /// CI: un Edge se queda abierto y le quita el teclado a la prueba siguiente). Solo se cierran los que abrió esta prueba.
    /// </summary>
    private void CloseBrowsersItOpened()
    {
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try { if (p.StartTime >= _started) p.Kill(entireProcessTree: true); } catch (Exception) { }
            finally { p.Dispose(); }
        }
    }

    public void Dispose()
    {
        CloseBrowsersItOpened();
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        // The group made for the test goes. / El grupo hecho para la prueba se va.
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private async Task CleanServer()
    {
        await using var s = await Server();
        if (s.Groups.FirstOrDefault(x => x.Name == _group) is { } g) await s.DeleteGroupAsync(g);
    }

    private static async Task<MeshServerSession> Server()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what)
    {
        if (Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result is { } found) return found;
        // What was on screen instead: without it there is only guessing. / Lo que había en pantalla: sin eso solo se adivina.
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"phase9-{name}.png"));
    }

    private static string Text(AutomationElement e) { try { return string.Join(" | ", new[] { e.Name }.Concat(e.FindAllDescendants().Select(x => x.Name))); } catch (Exception) { return ""; } }

    private static void Replace(AutomationElement box, string text)
    {
        box.Focus();
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
    }

    private static AutomationElement Item(AutomationElement list, string name) =>
        WaitFor(() => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)).And(cf.ByName(name))), $"\"{name}\" in {list.AutomationId}");

    /// <summary>What the device's chat page does: it joins the relay at the same id. / Lo que hace la página de chat del equipo.</summary>
    private static async Task<ClientWebSocket> Person(string nodeId)
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())).Equals(Env("MESHUI_CERT_SHA256"), StringComparison.OrdinalIgnoreCase);
        await ws.ConnectAsync(new Uri($"wss://{Env("MESHUI_SERVER")}/meshrelay.ashx?id={Uri.EscapeDataString($"meshmessenger/{nodeId}/user//admin")}"), CancellationToken.None);
        return ws;
    }

    private static async Task<string?> Heard(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var buffer = new byte[8192];
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            var t = Encoding.UTF8.GetString(buffer, 0, r.Count);
            // The relay's "c"/"cr" and its control messages are not the app's. / El «c»/«cr» del relé y su control no son de la app.
            if (t is "c" or "cr" || t.Contains("ctrlChannel", StringComparison.Ordinal)) continue;
            return t;
        }
    }

    [Fact]
    public async Task A_person_chats_asks_for_an_agent_update_sets_a_group_and_sees_the_recordings()
    {
        string nodeId;
        await using (var s = await Server())
        {
            nodeId = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName).Id;
            await s.CreateGroupAsync(_group, "Made by the phase 9 UI test");
        }

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
        var me = WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");

        // 1. The chat: the app waits, the person joins, both hear each other, the person leaves. / El chat de punta a punta.
        me.RightClick();
        Anywhere("ChatItem").Click();
        var chat = Anywhere("ChatWindow");
        Until(() => Id(chat, "ChatState").Name.StartsWith("The chat is opening", StringComparison.Ordinal), "the chat waiting for the person", () => Id(chat, "ChatState").Name);
        Assert.False(Id(chat, "ChatBox").IsEnabled, "nothing can be written before the person is in");
        Shot("chat-waiting");
        using (var person = await Person(nodeId))
        {
            Until(() => Id(chat, "ChatState").Name == "In the chat", "the person in the chat", () => Id(chat, "ChatState").Name);
            // The agent's browser comes up a moment later and takes the keyboard: it goes before typing. / El navegador del agente llega después y se queda el teclado.
            Retry.WhileFalse(() => Process.GetProcessesByName("msedge").Any(p => { try { return p.StartTime >= _started; } catch (Exception) { return false; } }), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500));
            Thread.Sleep(1500);
            CloseBrowsersItOpened();
            chat.AsWindow().SetForeground();
            await person.SendAsync(Encoding.UTF8.GetBytes("""{"action":"chat","msg":"Hola desde la página: ñ €"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
            Item(Id(chat, "ChatLines"), "Hola desde la página: ñ €");
            Id(chat, "ChatBox").Click();
            Keyboard.Type("Hello from the app");
            // Sent when the box has it all, as a person presses Send after typing. / Se manda con la caja entera, como hace una persona.
            Until(() => Id(chat, "ChatBox").AsTextBox().Text == "Hello from the app", "the typed message", () => Id(chat, "ChatBox").AsTextBox().Text);
            Id(chat, "ChatSend").AsButton().Invoke();
            string? heard;
            do heard = await Heard(person); while (heard is not null && heard.Contains("\"outtext\"", StringComparison.Ordinal));
            Assert.Equal("""{"action":"chat","msg":"Hello from the app"}""", heard);
            Item(Id(chat, "ChatLines"), "Hello from the app");
            Shot("chat-talking");
            await person.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        Until(() => Id(chat, "ChatState").Name.StartsWith("The person closed the chat", StringComparison.Ordinal), "the end of the chat", () => Id(chat, "ChatState").Name);
        Assert.True(Id(chat, "ChatAgain").IsOffscreen == false);
        chat.AsWindow().Close();
        CloseBrowsersItOpened();
        win.SetForeground();

        // 2. The agent update is offered and asks first; "No" sends nothing (the agent serves the other tests). / Pregunta antes.
        me.RightClick();
        Anywhere("UpdateAgentItem").Click();
        var ask = WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Window).And(cf.ByName("Update the agent…"))), "the update confirmation");
        Assert.Contains(Environment.MachineName, Text(ask));
        Shot("update-confirm");
        WaitFor(() => ask.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("No"))), "No").AsButton().Invoke();

        // 3. A group's consent and features, saved and read back from the server. / Consentimiento y funciones, guardados y releídos.
        Id(win, "GroupsButton").AsButton().Invoke();
        var gw = Anywhere("GroupsWindow");
        Item(Id(gw, "GroupList"), _group).Click();
        Until(() => Id(gw, "GroupSettingsSummary").Name == "No consent asked and no features on", "the empty summary", () => Id(gw, "GroupSettingsSummary").Name);
        Id(gw, "GroupSettingsButton").AsButton().Invoke();
        var gs = Anywhere("GroupSettingsWindow");
        Id(gs, "ConsentDesktopNotify").AsCheckBox().IsChecked = true;
        Id(gs, "FeatSync").AsCheckBox().IsChecked = true;
        Assert.True(Id(gs, "FeatPrefer").AsCheckBox().IsChecked, "ticking the sync ticks its options, as the website");
        Id(gs, "FeatInactive").AsCheckBox().IsChecked = true;
        Replace(Id(gs, "FeatDays"), "45");
        Shot("group-settings");
        Id(gs, "GroupSettingsSave").AsButton().Invoke();
        Until(() => Id(gw, "GroupSettingsSummary").Name == "Desktop: notify · Name synced · Removed after 45 day(s) without connecting", "the summary after saving", () => Id(gw, "GroupSettingsSummary").Name);
        await using (var s = await Server())
        {
            var g = s.Groups.Single(x => x.Name == _group);
            Assert.Equal(new GroupSettings((int)GroupConsent.DesktopNotify, (int)(GroupFeature.SyncName | GroupFeature.PreferAgentName | GroupFeature.AllowOverride), 45), g.Settings);
        }
        gw.AsWindow().Close();

        // 4. The recordings: the chat above is there, and a password session opens them on the website. / Las grabaciones.
        win.SetForeground();
        Id(win, "ServerMenuButton").AsButton().Invoke();
        // Through its pattern, not a mouse point: the menu is a popup above other windows (as ServerTests). / Por su patrón, como ServerTests.
        Anywhere("RecordingsItem").AsMenuItem().Invoke();
        var rw = Anywhere("RecordingsWindow");
        var list = Id(rw, "RecordingsList");
        Until(() => Text(list).Contains("Chat", StringComparison.Ordinal), "the chat's recording", () => Text(rw));
        Assert.Equal("Open on the website", Id(rw, "RecordingsDownload").Name);
        Shot("recordings");
        rw.AsWindow().Close();
    }
}
