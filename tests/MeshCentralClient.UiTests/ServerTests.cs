using System.Diagnostics;
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
/// The server as a person uses it from the app, on real Windows against the real server: the event log (a change made
/// elsewhere appears live, and it exports to CSV through the save dialog), a remote-session report that has the session
/// the test itself opened, the server's live state, and a notification sent from the app that comes back to it.
/// El servidor como lo usa una persona desde la app, en Windows de verdad contra el servidor real.
/// </summary>
[Trait("Category", "UI")]
public sealed class ServerTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-server-ui-");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static async Task<MeshServerSession> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    private async Task CleanServer()
    {
        await using var s = await Admin();
        foreach (var g in s.Groups.Where(g => g.Name.Contains(_tag))) await s.DeleteGroupAsync(g);
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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"server-{name}.png"));
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

    private static int Rows(AutomationElement list) => list.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length;

    private void Menu(AutomationElement win, string item)
    {
        win.SetForeground();
        Id(win, "ServerMenuButton").AsButton().Invoke();
        Anywhere(item).Click();
    }

    /// <summary>Saves through the Windows save dialog of <paramref name="owner"/>. / Guarda por el diálogo de guardar de Windows.</summary>
    private string SaveAs(AutomationElement owner, string button, string file)
    {
        Id(owner, button).Click();
        var save = WaitFor(() => owner.AsWindow().ModalWindows.FirstOrDefault(), "the save dialog");
        var nameBox = WaitFor(() => save.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByControlType(ControlType.Edit))), "the file name box");
        Type(nameBox, file);
        Keyboard.Type(VirtualKeyShort.ENTER);
        Until(() => File.Exists(file), "the CSV file " + file);
        Thread.Sleep(300);
        return File.ReadAllText(file, Encoding.UTF8);
    }

    [Fact]
    public async Task A_person_reads_events_reports_and_the_server_s_state_and_sends_a_notification()
    {
        // A real remote session first, so the report has something of the test's own. / Una sesión remota de verdad antes.
        await using (var s = await Admin())
        {
            var me = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName);
            await using var term = await TerminalSession.OpenAsync(s, me.Id, TunnelProtocol.Terminal, 80, 25);
            await Task.Delay(1500);
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
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");

        // 1. Events: the log is there, and a group created elsewhere appears live. / Sucesos: está el registro, y un grupo creado fuera aparece en vivo.
        Menu(win, "EventsItem");
        var ew = Anywhere("EventsWindow");
        var events = Id(ew, "EventList");
        Until(() => Rows(events) > 0, "the event log", () => Id(ew, "EventsStatus").Name);
        var name = "Live " + _tag;
        await using (var s = await Admin()) await s.CreateGroupAsync(name);
        Until(() => Text(events).Contains(name), "the live event of the new group", () => Text(events));
        Type(Id(ew, "EventFilter"), name);
        Until(() => events.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)) is { Length: > 0 } rows && rows.All(r => Text(r).Contains(name)), "only the filtered events", () => Text(events));
        Shot("events");
        var csv = SaveAs(ew, "ExportEventsButton", Path.Combine(_dir.FullName, "events.csv"));
        Assert.StartsWith("Time,User,", csv, StringComparison.Ordinal); // ReadAllText drops the BOM / ReadAllText quita el BOM
        Assert.Contains(name, csv, StringComparison.Ordinal);
        ew.AsWindow().Close();

        // 2. A remote-session report, per user, has the session opened above. / Un informe de sesiones por usuario tiene la sesión de arriba.
        Menu(win, "ReportsItem");
        var rw = Anywhere("ReportsWindow");
        Id(rw, "RunReportButton").AsButton().Invoke();
        var report = Id(rw, "ReportList");
        Until(() => Rows(report) > 0, "the report rows", () => Id(rw, "ReportStatus").Name);
        Until(() => Text(report).Contains("Terminal") && Text(report).Contains(Environment.MachineName), "the test's own terminal session in the report", () => Text(report));
        Shot("report");
        csv = SaveAs(rw, "ExportReportButton", Path.Combine(_dir.FullName, "report.csv"));
        Assert.Contains(Environment.MachineName, csv, StringComparison.Ordinal);
        rw.AsWindow().Close();

        // 3. The server's live state. / El estado del servidor en vivo.
        Menu(win, "ServerStateItem");
        var sw = Anywhere("ServerWindow");
        Until(() => Id(sw, "MemoryText").Name.StartsWith("Memory:", StringComparison.Ordinal), "the live memory line", () => Id(sw, "MemoryText").Name);
        Until(() => Id(sw, "CountersText").Name.Contains("Agents connected: 1"), "the live counters", () => Id(sw, "CountersText").Name);
        Shot("state");
        sw.AsWindow().Close();

        // 4. A notification to everyone, sent from the app, comes back to the app itself. / Un aviso a todos, mandado desde la app, vuelve a la propia app.
        win.SetForeground();
        Id(win, "UsersButton").AsButton().Invoke();
        var uw = Anywhere("UsersWindow");
        Id(uw, "SendNotificationButton").Click();
        var nf = Anywhere("NotificationForm");
        Type(Id(nf, "NotificationTextBox"), "Hola a todos " + _tag);
        Id(nf, "NotificationOk").AsButton().Invoke();
        var shown = Anywhere("NotificationWindow");
        Until(() => Id(shown, "NotificationText").Name == "Hola a todos " + _tag, "the notification shown", () => Id(shown, "NotificationText").Name);
        Assert.Equal("Notification sent.", Id(uw, "UsersStatus").Name);
        Shot("notification");
    }
}
