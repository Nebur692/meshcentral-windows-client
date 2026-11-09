using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// The page of the real Windows agent, as a person uses it; what is saved is checked by closing and reopening the page,
/// so it really is on the server. / La ficha del agente de Windows real, como la usa una persona; lo guardado se
/// comprueba cerrando y volviendo a abrir la ficha, así que está de verdad en el servidor.
/// </summary>
[Trait("Category", "UI")]
public sealed class DeviceTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-device-ui-");
    private Application? _app;
    private Window? _win;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what) =>
        Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result ?? throw new Exception(what + " did not appear");

    private static AutomationElement Id(AutomationElement root, string id) => WaitFor(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void Until(Func<bool> ok, string what, Func<string>? seen = null)
    {
        if (Retry.WhileFalse(ok, Timeout, TimeSpan.FromMilliseconds(300)).Result) return;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + (seen is null ? "" : " — seen: " + seen()));
    }

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"device-{name}.png"));
    }

    private static string Value(AutomationElement e) { try { return e.Patterns.Value.Pattern.Value.Value ?? ""; } catch (Exception) { return ""; } }

    private static string Name(AutomationElement e) { try { return e.Name ?? ""; } catch (Exception) { return ""; } }

    private static void TypeInto(AutomationElement box, string text)
    {
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
    }

    private AutomationElement OpenDetails()
    {
        var device = WaitFor(() => _win!.FindFirstDescendant(cf => cf.ByAutomationId("DeviceList"))?.FindFirstDescendant(cf => cf.ByName(Environment.MachineName)), "the device");
        device.RightClick();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DetailsItem")), "Device details").Click();
        return WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DeviceWindow")), "the device page");
    }

    private static void ShowTab(AutomationElement page, string id) => Id(page, id).Patterns.SelectionItem.Pattern.Select();

    [Fact]
    public void A_person_reads_and_edits_the_page_of_a_device()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        _win = _app.GetMainWindow(_automation, Timeout)!;
        _win.SetForeground();
        Id(_win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Id(_win, "UserBox").Click(); Keyboard.Type("admin");
        Id(_win, "PasswordBox").Click(); Keyboard.Type("Admin-Pass-1234");
        Id(_win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();

        var page = OpenDetails();
        // General: the real OS of this machine. / General: el sistema real de esta máquina.
        Until(() => page.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Any(e => Value(e).Contains("Windows Server")), "the OS is not shown",
            () => string.Join(" | ", page.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Select(Value)));
        // The CPU is the processor, not the first "Name" of the inventory (it showed "Physical Memory" once).
        // La CPU es el procesador, no el primer «Name» del inventario (una vez salió «Physical Memory»).
        Assert.DoesNotContain(page.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)), e => Value(e) == "Physical Memory");
        Shot("general");

        // What a person looks for without opening the inventory, from a real Windows agent. / Lo que una persona busca sin abrir el inventario, de un agente de Windows real.
        var memory = Id(page, "DeviceMemory").Patterns.Value.Pattern.Value.Value;
        Assert.Contains("GB", memory, StringComparison.Ordinal);
        Assert.Contains("GB", Id(page, "DeviceDisks").Patterns.Value.Pattern.Value.Value, StringComparison.Ordinal);
        Assert.Contains("C:", Id(page, "DeviceVolumes").Patterns.Value.Pattern.Value.Value, StringComparison.Ordinal);

        ShowTab(page, "TabNetwork");
        Until(() => Id(page, "NetworkList").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 0, "no network interface");
        Shot("network");

        ShowTab(page, "TabInventory");
        TypeInto(Id(page, "InventoryFilter"), "cpu");
        Until(() => Id(page, "InventoryTree").FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Any(t => Name(t).Contains("cpu", StringComparison.OrdinalIgnoreCase) && Name(t).Contains(':')),
            "the inventory filter found no cpu value", () => string.Join(" | ", Id(page, "InventoryTree").FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Select(Name).Take(20)));
        Shot("inventory");

        // Edit description and notes. / Editar descripción y notas.
        var desc = "Equipo de pruebas " + Guid.NewGuid().ToString("N")[..6];
        var notes = "nota ñ " + Guid.NewGuid().ToString("N")[..6];
        ShowTab(page, "TabGeneral");
        TypeInto(Id(page, "DescBox"), desc);
        Id(page, "SaveDeviceButton").AsButton().Invoke();
        Until(() => Name(Id(page, "GeneralNotice")) == "Saved.", "the description was not saved", () => Name(Id(page, "GeneralNotice")));
        ShowTab(page, "TabNotes");
        TypeInto(Id(page, "NotesBox"), notes);
        Id(page, "SaveNotesButton").AsButton().Invoke();
        Until(() => Name(Id(page, "NotesNotice")) == "Saved.", "the notes were not saved", () => Name(Id(page, "NotesNotice")));
        page.AsWindow().Close();

        // Reopened: it all comes from the server. / Reabierta: todo viene del servidor.
        page = OpenDetails();
        Until(() => Value(Id(page, "DescBox")) == desc, "the description did not come back", () => Value(Id(page, "DescBox")));
        ShowTab(page, "TabNotes");
        Until(() => Value(Id(page, "NotesBox")) == notes, "the notes did not come back", () => Value(Id(page, "NotesBox")));
        ShowTab(page, "TabHistory");
        Until(() => Id(page, "EventsList").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Any(r => Name(r).Contains("description", StringComparison.OrdinalIgnoreCase)),
            "the change is not in the history", () => string.Join(" | ", Id(page, "EventsList").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Select(Name).Take(5)));
        Shot("history");
        page.AsWindow().Close();
    }
}
