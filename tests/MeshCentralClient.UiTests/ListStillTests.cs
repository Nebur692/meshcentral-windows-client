using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MeshCentralClient.Core;
using Xunit;
using Capture = FlaUI.Core.Capturing.Capture;
using Wait = FlaUI.Core.Input.Wait;

/// <summary>
/// The device list stays where it is under the mouse: a right click or a click on a row cut at the bottom edge, the menu
/// closing, or a device that arrives live, do not scroll it (it used to jump several rows). The keyboard still scrolls.
/// La lista de equipos se queda quieta bajo el ratón: el clic derecho o normal en una fila cortada abajo, el menú al cerrarse
/// o un equipo que llega en vivo no la mueven (antes saltaba varias filas). El teclado sí la mueve.
/// </summary>
[Trait("Category", "UI")]
public sealed class ListStillTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-still-ui-");
    // Two groups at the top of the list, as on a real server: a short one, then the long one the test clicks in.
    // Dos grupos arriba de la lista, como en un servidor de verdad: uno corto y luego el largo donde se pulsa.
    private static readonly string Tag = Guid.NewGuid().ToString("N")[..6];
    private readonly string _first = "aa-still-" + Tag + "-1";
    private readonly string _group = "aa-still-" + Tag + "-2";
    private Application? _app;

    public void Dispose()
    {
        try { _app?.Close(); } catch (Exception) { }
        try { _app?.Kill(); } catch (Exception) { }
        _app?.Dispose();
        _automation.Dispose();
        // The group made for the test goes, with its devices. / El grupo hecho para la prueba se va, con sus equipos.
        try { CleanServer().GetAwaiter().GetResult(); } catch (Exception) { }
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static async Task<MeshServerSession> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
    }

    private async Task CleanServer()
    {
        await using var s = await Admin();
        await s.GetDeviceGroupsAsync();
        foreach (var g in s.Groups.Where(x => x.Name == _first || x.Name == _group).ToList()) await s.DeleteGroupAsync(g);
    }

    private static AutomationElement WaitFor(Func<AutomationElement?> find, string what)
    {
        if (Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(250)).Result is { } found) return found;
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + " did not appear");
    }

    private static AutomationElement Id(AutomationElement root, string id) => WaitFor(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

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
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"still-{name}.png"));
    }

    private static void Type(AutomationElement box, string text)
    {
        box.Focus();
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
    }

    private AutomationElement SignIn()
    {
        var psi = new ProcessStartInfo(Env("MESHUI_EXE")) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = "off";
        _app = Application.Launch(psi);
        var win = _app.GetMainWindow(_automation, Timeout)!;
        win.SetForeground();
        Id(win, "ServerBox").AsComboBox().EditableText = Env("MESHUI_SERVER");
        Type(Id(win, "UserBox"), "admin");
        Id(win, "PasswordBox").Click(); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Keyboard.Type("Admin-Pass-1234");
        Id(win, "ConnectButton").AsButton().Invoke();
        if (Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CertTrust")), TimeSpan.FromSeconds(15)).Result is { } trust) trust.AsButton().Invoke();
        return win;
    }

    private static AutomationElement[] Rows(AutomationElement list) =>
        list.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Where(r => r.Name.StartsWith("row-", StringComparison.Ordinal)).ToArray();

    private static Rectangle Box(AutomationElement list, string name) =>
        list.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem).And(cf.ByName(name)))!.BoundingRectangle;

    /// <summary>Where every visible test row is, top to bottom. / Dónde está cada fila de prueba visible.</summary>
    private static string Where(AutomationElement list) =>
        string.Join(", ", Rows(list).Where(r => !r.IsOffscreen).Select(r => $"{r.Name}@{r.BoundingRectangle.Top}"));

    [Fact]
    public async Task The_list_stays_still_under_the_mouse_and_moves_with_the_keyboard()
    {
        await using (var admin = await Admin())
        {
            await admin.CreateLocalGroupAsync(_first);
            await admin.CreateLocalGroupAsync(_group);
            await admin.GetDeviceGroupsAsync();
            var first = admin.Groups.First(x => x.Name == _first);
            for (var i = 1; i <= 3; i++) await admin.AddLocalDeviceAsync(first, $"top-{i}", $"10.8.0.{i}", LocalDeviceOs.Linux);
            var g = admin.Groups.First(x => x.Name == _group);
            for (var i = 1; i <= 30; i++) await admin.AddLocalDeviceAsync(g, $"row-{i:00}", $"10.9.0.{i}", LocalDeviceOs.Linux);
        }
        var win = SignIn();
        var list = Id(win, "DeviceList");
        Until(() => Rows(list).Length == 30, "the 30 test rows", () => Rows(list).Length.ToString());

        // A short window, so the group goes past the bottom; then a height where the last visible row is cut by the edge.
        // Una ventana baja, para que el grupo pase del borde; luego una altura en la que la última fila visible queda cortada.
        AutomationElement? cut = null;
        for (var h = 520; h <= 640 && cut is null; h += 9)
        {
            win.Patterns.Transform.Pattern.Resize(1000, h);
            Wait.UntilInputIsProcessed(); Thread.Sleep(300);
            var bottom = list.BoundingRectangle.Bottom;
            cut = Rows(list).FirstOrDefault(r => r.BoundingRectangle.Top < bottom - 12 && r.BoundingRectangle.Bottom > bottom + 8);
        }
        Assert.NotNull(cut);
        var name = cut!.Name;
        var before = Where(list);
        var top = cut.BoundingRectangle.Top;
        Shot("before");

        // A right click on its visible part, then the menu closed with Escape: nothing moves.
        // Clic derecho en su parte visible y el menú cerrado con Escape: nada se mueve.
        Mouse.Click(new Point(cut.BoundingRectangle.Left + 60, (top + list.BoundingRectangle.Bottom) / 2), FlaUI.Core.Input.MouseButton.Right);
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DetailsItem")), "the device menu");
        Thread.Sleep(500);
        Shot("menu");
        Assert.True(Box(list, name).Top == top, $"the right click moved the list: {before}  →  {Where(list)}");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.UntilInputIsProcessed(); Thread.Sleep(500);
        Assert.True(Box(list, name).Top == top, $"closing the menu moved the list: {before}  →  {Where(list)}");
        Assert.True(cut.Patterns.SelectionItem.Pattern.IsSelected.Value, "the right click did not select the row");

        // A plain click on it either. / Un clic normal tampoco.
        Mouse.Click(new Point(cut.BoundingRectangle.Left + 60, (top + list.BoundingRectangle.Bottom) / 2));
        Wait.UntilInputIsProcessed(); Thread.Sleep(500);
        Assert.True(Box(list, name).Top == top, $"the click moved the list: {before}  →  {Where(list)}");

        // A device that arrives live, below everything seen, moves nothing either.
        // Un equipo que llega en vivo, por debajo de lo que se ve, tampoco mueve nada.
        await using (var admin = await Admin())
        {
            await admin.GetDeviceGroupsAsync();
            await admin.AddLocalDeviceAsync(admin.Groups.First(x => x.Name == _group), "row-99", "10.9.0.99", LocalDeviceOs.Linux);
        }
        Until(() => Rows(list).Length == 31, "the device that arrived live");
        Thread.Sleep(500);
        Assert.True(Box(list, name).Top == top, $"a device arriving moved the list: {before}  →  {Where(list)}");

        // The keyboard does move it: Down selects the next row, which comes whole into view.
        // El teclado sí la mueve: Abajo elige la fila siguiente, que se ve entera.
        Keyboard.Type(VirtualKeyShort.DOWN);
        Wait.UntilInputIsProcessed(); Thread.Sleep(500);
        var next = $"row-{int.Parse(name[4..]) + 1:00}";
        Until(() => Box(list, next).Bottom <= list.BoundingRectangle.Bottom + 1, "the next row whole in view with the keyboard", () => Where(list));
        Shot("keyboard");
    }
}
