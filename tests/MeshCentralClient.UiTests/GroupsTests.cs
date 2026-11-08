using System.Diagnostics;
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
/// Device groups as a person uses them, on real Windows against the real server: create a group, give another account
/// rights, change them, make an invitation link, download the agent (through the Windows save dialog), move this very
/// machine into the group and back, and delete the group. What the app shows is checked against the server itself.
/// Grupos de equipos como los usa una persona, en Windows de verdad contra el servidor real: crear un grupo, dar
/// permisos a otra cuenta, cambiarlos, hacer un enlace de invitación, descargar el agente (por el diálogo de guardar de
/// Windows), mover esta misma máquina al grupo y devolverla, y borrar el grupo. Lo que enseña la app se comprueba contra
/// el propio servidor.
/// </summary>
[Trait("Category", "UI")]
public sealed class GroupsTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-groups-ui-");
    private readonly string _name = "UI " + Guid.NewGuid().ToString("N")[..6];
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        // Whatever the test left on the server: the machine back home, the group gone. / Lo que dejó en el servidor: la máquina a su grupo, el grupo fuera.
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static async Task<MeshServerSession> Server()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    private static string? _home;

    private async Task CleanServer()
    {
        await using var s = await Server();
        var g = s.Groups.FirstOrDefault(x => x.Name == _name);
        if (g is null) return;
        var me = (await s.GetDevicesAsync()).FirstOrDefault(d => d.Name == Environment.MachineName);
        if (me is not null && me.GroupId == g.Id && _home is not null && s.GroupById(_home) is { } home) await s.MoveDevicesAsync([me], home);
        if ((await s.GetDevicesAsync()).All(d => d.GroupId != g.Id)) await s.DeleteGroupAsync(g);
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what) =>
        Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result ?? throw new Exception(what + " did not appear");

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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"groups-{name}.png"));
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

    // A ListBox row is a ListItem; a row of a ListView with columns (GridView) is a DataItem (seen on CI 08-11).
    // Una fila de ListBox es ListItem; una de ListView con columnas (GridView) es DataItem (visto en la CI el 08-11).
    private static AutomationElement Item(AutomationElement list, string name) =>
        WaitFor(() => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)).And(cf.ByName(name))), $"\"{name}\" in {list.AutomationId}");

    private void MoveMachineTo(AutomationElement win, string group)
    {
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device").RightClick();
        Anywhere("MoveToGroupItem").Click();
        var dlg = Anywhere("MoveDialog");
        Item(Id(dlg, "MoveTargetList"), group).Click();
        Id(dlg, "MoveOk").AsButton().Invoke();
        Until(() => Id(win, "ActionStatus").Name.StartsWith($"1 device(s) moved to \"{group}\""), "the move status", () => Id(win, "ActionStatus").Name);
    }

    [Fact]
    public async Task A_person_manages_a_device_group_and_its_devices()
    {
        await using (var s = await Server())
            _home = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName).GroupId;
        string homeName;
        await using (var s = await Server()) homeName = s.GroupById(_home)!.Name;

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

        // 1. A new group. / Un grupo nuevo.
        Id(win, "GroupsButton").AsButton().Invoke();
        var gw = Anywhere("GroupsWindow");
        Id(gw, "NewGroupButton").Click();
        var form = Anywhere("GroupForm");
        Replace(Id(form, "GroupNameBox"), _name);
        Replace(Id(form, "GroupDescBox"), "Made by the UI test");
        Id(form, "GroupFormOk").AsButton().Invoke();
        Until(() => Id(gw, "GroupNameText").Name == _name, "the new group shown", () => Id(gw, "GroupNameText").Name);
        Item(Id(gw, "GroupList"), _name);
        Assert.Equal("Made by the UI test", Id(gw, "GroupDescText").Name);
        Shot("created");

        // 2. Another account with two rights, then one more. / Otra cuenta con dos permisos y luego uno más.
        Id(gw, "AddUsersButton").Click();
        var rd = Anywhere("RightsDialog");
        Replace(Id(rd, "UserNamesBox"), "user");
        Id(rd, "Wake").AsCheckBox().IsChecked = true;
        Id(rd, "DeviceDetails").AsCheckBox().IsChecked = true;
        Id(rd, "RightsOk").AsButton().Invoke();
        var members = Id(gw, "MemberList");
        Until(() => Text(members).Contains("Wake devices, See device details"), "the user with her rights", () => Text(members));
        Item(members, "User").Click();
        Id(gw, "ChangeRightsButton").Click();
        rd = Anywhere("RightsDialog");
        Assert.True(Id(rd, "Wake").AsCheckBox().IsChecked); // the dialog opens with what she has / el diálogo abre con lo que tiene
        Id(rd, "RemoteControl").AsCheckBox().IsChecked = true;
        Id(rd, "RightsOk").AsButton().Invoke();
        Until(() => Text(members).Contains("Remote control, Wake devices, See device details"), "the changed rights", () => Text(members));
        Shot("users");
        // The server says the same. / El servidor dice lo mismo.
        await using (var s = await Server())
        {
            var m = s.Groups.Single(g => g.Name == _name).Members.Single(x => x.Name == "User");
            Assert.Equal((long)(MeshRights.RemoteControl | MeshRights.Wake | MeshRights.DeviceDetails), m.Rights);
        }

        // 3. Invitation link. / Enlace de invitación.
        Id(gw, "CreateInviteButton").AsButton().Invoke();
        Until(() => Id(gw, "InviteLinkBox").AsTextBox().Text.Contains("/agentinvite?c="), "the invitation link", () => Id(gw, "InviteLinkBox").AsTextBox().Text);
        Assert.Contains("meshinstall.sh", Id(gw, "LinuxCommandBox").AsTextBox().Text, StringComparison.Ordinal);
        Shot("invite");

        // 4. The Windows agent, saved through the Windows dialog, with this group inside. / El agente de Windows, guardado por el diálogo de Windows, con este grupo dentro.
        var file = Path.Combine(_dir.FullName, "agent-ui.exe");
        Id(gw, "DownloadAgentButton").Click();
        var save = WaitFor(() => gw.AsWindow().ModalWindows.FirstOrDefault(), "the save dialog");
        Shot("save-dialog");
        var nameBox = WaitFor(() => save.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByControlType(ControlType.Edit))), "the file name box");
        Replace(nameBox, file);
        Keyboard.Type(VirtualKeyShort.ENTER);
        Until(() => Id(gw, "GroupsStatus").Name.StartsWith("Agent saved in", StringComparison.Ordinal), "the agent saved", () => Id(gw, "GroupsStatus").Name);
        var bytes = File.ReadAllBytes(file);
        Assert.Equal("MZ", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
        await using (var s = await Server())
        {
            var id = s.Groups.Single(g => g.Name == _name).Id;
            var hex = Convert.ToHexString(Convert.FromBase64String(MeshServerSession.ShortGroupId(id).Replace('@', '+').Replace('$', '/')));
            Assert.Contains("MeshID=0x" + hex, System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }
        Assert.False(File.Exists(file + ".part"));
        Shot("agent-saved");

        // 5. This machine into the group and back: the list moves it under the new header, the server agrees, and it stays online.
        //    Esta máquina al grupo y de vuelta: la lista la pasa bajo la cabecera nueva, el servidor lo confirma y sigue conectada.
        win.SetForeground();
        MoveMachineTo(win, _name);
        WaitFor(() => Id(win, "DeviceList").FindFirstDescendant(cf => cf.ByName(_name)), "the new group header in the device list");
        await using (var s = await Server())
            Assert.Equal(s.Groups.Single(g => g.Name == _name).Id, (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName).GroupId);
        Until(() => Id(win, "CountText").Name == "Devices: 1 · online: 1", "the machine online after the move", () => Id(win, "CountText").Name);
        Shot("moved");
        MoveMachineTo(win, homeName);
        await using (var s = await Server())
            Assert.Equal(_home, (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName).GroupId);

        // 6. Delete the group: asked first, then gone here and on the server. / Borrar el grupo: pregunta antes y luego desaparece aquí y en el servidor.
        gw.SetForeground();
        Item(Id(gw, "GroupList"), _name).Click();
        Id(gw, "DeleteGroupButton").Click();
        var yes = WaitFor(() => gw.AsWindow().ModalWindows.FirstOrDefault()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes"))), "the delete confirmation");
        Assert.Contains("0 device(s)", Text(yes.Parent), StringComparison.Ordinal);
        Shot("delete-asks");
        yes.AsButton().Invoke();
        Until(() => Id(gw, "GroupsStatus").Name == $"Group \"{_name}\" deleted.", "the delete status", () => Id(gw, "GroupsStatus").Name);
        Until(() => Id(gw, "GroupList").FindFirstDescendant(cf => cf.ByName(_name)) is null, "the group leaving the list");
        await using (var s = await Server()) Assert.DoesNotContain(s.Groups, g => g.Name == _name);
        Shot("deleted");
    }
}
