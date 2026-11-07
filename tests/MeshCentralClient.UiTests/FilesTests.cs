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
/// The files of the real Windows agent, which runs on this same machine: every change made from the app is checked on
/// the real disk. Uses the Windows open/save dialogs as a person does.
/// Los ficheros del agente de Windows real, que corre en esta misma máquina: cada cambio hecho desde la app se comprueba
/// en el disco de verdad. Usa los diálogos de abrir y guardar de Windows como una persona.
/// </summary>
[Trait("Category", "UI")]
public sealed class FilesTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-files-ui-");
    private Application? _app;

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

    private AutomationElement Anywhere(string id) => WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void Until(Func<bool> condition, string what, Func<string>? seen = null)
    {
        if (Retry.WhileFalse(condition, Timeout, TimeSpan.FromMilliseconds(250)).Result) return;
        // On failure: a screenshot and what was on screen, so the cause is seen, not guessed.
        // Al fallar: una captura y lo que había en pantalla, para ver la causa en vez de suponerla.
        Shot("FAILED-" + string.Concat(what.Where(char.IsLetterOrDigit)));
        throw new Exception(what + (seen is null ? "" : " — seen: " + seen()));
    }

    private static string Items(AutomationElement list) =>
        string.Join(" | ", list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(i => i.Name));

    private static void Shot(string name)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        Capture.Screen().ToFile(Path.Combine(Env("MESHUI_SHOTS"), $"files-{name}.png"));
    }

    private static AutomationElement? Row(AutomationElement list, string name) => list.FindFirstDescendant(cf => cf.ByName(name));

    /// <summary>A Windows open or save dialog: its file name box has the focus; type the full path and press Enter.
    /// Un diálogo de abrir o guardar de Windows: su casilla de nombre tiene el foco; se escribe la ruta entera y Enter.</summary>
    private void FileDialog(string title, string path)
    {
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title))), "the dialog " + title);
        Thread.Sleep(700);
        Keyboard.Type(path);
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    private void Input(string text)
    {
        var box = Anywhere("InputBox");
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Anywhere("InputOk").AsButton().Invoke();
    }

    [Fact]
    public void A_person_manages_the_files_of_a_device()
    {
        // The folder on the device (= this machine) and a local file to upload. / La carpeta en el equipo (= esta máquina) y un fichero local que subir.
        var remote = Directory.CreateDirectory(Path.Combine(_dir.FullName, "device")).FullName;
        File.WriteAllText(Path.Combine(remote, "hello.txt"), "hola desde el equipo");
        var local = Path.Combine(_dir.FullName, "to-upload.bin");
        var payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        payload[0] = (byte)'{'; // the escape case, through the real app / el caso del escape, por la app de verdad
        File.WriteAllBytes(local, payload);

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
        device.RightClick();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("FilesItem")), "Open files").Click();
        var files = Anywhere("FilesWindow");
        var list = Id(files, "FileList");

        // Starts at the drives of the Windows device. / Empieza en las unidades del equipo Windows.
        Until(() => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Any(i => i.Name.EndsWith(':')), "no drive listed",
            () => Items(list) + " · status: " + (files.FindFirstDescendant(cf => cf.ByAutomationId("FilesStatus"))?.Name ?? "?"));
        Shot("drives");

        var path = Id(files, "PathBox");
        path.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(remote);
        Keyboard.Type(VirtualKeyShort.ENTER);
        WaitFor(() => Row(list, "hello.txt"), "hello.txt");

        // New folder. / Carpeta nueva.
        Id(files, "NewFolderButton").AsButton().Invoke();
        Input("nueva");
        Until(() => Directory.Exists(Path.Combine(remote, "nueva")), "the folder was not created on the disk");
        WaitFor(() => Row(list, "nueva"), "nueva in the list");

        // Rename. / Renombrar.
        Row(list, "hello.txt")!.Click();
        Id(files, "RenameButton").AsButton().Invoke();
        Input("renamed.txt");
        Until(() => File.Exists(Path.Combine(remote, "renamed.txt")) && !File.Exists(Path.Combine(remote, "hello.txt")), "not renamed on the disk");
        WaitFor(() => Row(list, "renamed.txt"), "renamed.txt in the list");

        // Upload with the Windows dialog. / Subir con el diálogo de Windows.
        Id(files, "UploadButton").AsButton().Invoke();
        FileDialog("Upload…", local);
        Until(() => File.Exists(Path.Combine(remote, "to-upload.bin")) && new FileInfo(Path.Combine(remote, "to-upload.bin")).Length == payload.Length, "not uploaded");
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(remote, "to-upload.bin")));
        WaitFor(() => Row(list, "to-upload.bin"), "the upload in the list");
        Shot("after-upload");

        // Download with the Windows dialog. / Bajar con el diálogo de Windows.
        var saved = Path.Combine(_dir.FullName, "downloaded.txt");
        Row(list, "renamed.txt")!.Click();
        Id(files, "DownloadButton").AsButton().Invoke();
        FileDialog("Download…", saved);
        Until(() => File.Exists(saved), "not downloaded");
        Assert.Equal("hola desde el equipo", File.ReadAllText(saved));
        Assert.False(File.Exists(saved + ".part"));

        // Delete, confirming. / Borrar, confirmando.
        Row(list, "nueva")!.Click();
        Id(files, "DeleteButton").AsButton().Invoke();
        WaitFor(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes"))), "the confirmation").AsButton().Invoke();
        Until(() => !Directory.Exists(Path.Combine(remote, "nueva")), "not deleted on the disk");
        Until(() => Row(list, "nueva") is null, "still in the list");
        Shot("done");
        files.AsWindow().Close();
    }
}
