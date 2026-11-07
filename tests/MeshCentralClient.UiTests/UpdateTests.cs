using System.Diagnostics;
using System.Security.Cryptography;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The app updates itself like a person would see it, from a local release feed that serves a real "version 99"
/// build. Set up by tests/ui/run-ui-test.ps1.
/// La app se actualiza sola tal como lo vería una persona, desde una fuente de versiones local que sirve una
/// compilación real «versión 99». Lo prepara tests/ui/run-ui-test.ps1.
/// </summary>
[Trait("Category", "UI")]
public sealed class UpdateTests(ITestOutputHelper output) : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly UIA3Automation _automation = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-upd-ui-");
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (var p in Process.GetProcessesByName("MeshCentralClient"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(_dir.FullName, StringComparison.OrdinalIgnoreCase) == true) p.Kill(); } catch (Exception) { }
        }
        _automation.Dispose();
        for (var i = 0; i < 20; i++) { try { _dir.Delete(true); break; } catch (Exception) { Thread.Sleep(500); } }
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private (Application App, Window Win, string Exe) Start(string feed)
    {
        var exe = Path.Combine(_dir.FullName, "MeshCentralClient.exe");
        File.Copy(Env("MESHUI_EXE"), exe, overwrite: true);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["APPDATA"] = Path.Combine(_dir.FullName, "appdata");
        psi.Environment["LOCALAPPDATA"] = Path.Combine(_dir.FullName, "localappdata");
        psi.Environment["MESHCLIENT_UPDATE_FEED"] = feed;
        var app = Application.Launch(psi);
        var win = app.GetMainWindow(_automation, Timeout) ?? throw new Exception("main window did not appear");
        return (app, win, exe);
    }

    private static AutomationElement Find(AutomationElement root, string id, TimeSpan? t = null) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), t ?? Timeout, TimeSpan.FromMilliseconds(250)).Result
        ?? throw new Exception($"'{id}' did not appear / no apareció '{id}'");

    private void Shot(string name)
    {
        FlaUI.Core.Input.Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        var dir = Env("MESHUI_SHOTS");
        Directory.CreateDirectory(dir);
        Capture.Screen().ToFile(Path.Combine(dir, $"upd-{name}.png"));
    }

    [Fact]
    public void Updates_itself_and_reopens_as_the_new_version()
    {
        var (app, win, exe) = Start(Env("MESHUI_UPDATE_FEED"));
        var before = Sha(exe);
        Assert.Contains("Version 99.0.0 is available", Find(win, "UpdateText").Name);
        Shot("available");
        Find(win, "UpdateNowButton").AsButton().Invoke();

        // The old process ends and a new one opens, which says it is 99.0.0.
        // El proceso viejo termina y se abre uno nuevo, que dice ser la 99.0.0.
        Assert.True(Retry.WhileFalse(() => app.HasExited, Timeout).Result, "the old version did not close");
        var reopened = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("VersionText").And(cf.ByName("Version 99.0.0"))),
            Timeout, TimeSpan.FromMilliseconds(500)).Result;
        Shot("reopened");
        Assert.NotNull(reopened);
        Assert.NotEqual(before, Sha(exe));
        Assert.Equal(Env("MESHUI_EXE99_SHA").ToUpperInvariant(), Sha(exe));
        // Nothing left behind once the old one is gone. / No queda nada en cuanto se va el viejo.
        Assert.True(Retry.WhileTrue(() => File.Exists(Path.Combine(_dir.FullName, "MeshCentralClient.old.exe")), Timeout).Result, "old exe was not removed");
        Assert.False(File.Exists(exe + ".new"));
        // The new version finds nothing newer: no banner. / La nueva no encuentra nada más nuevo: sin aviso.
        var win2 = reopened!.Parent;
        while (win2 is not null && win2.ControlType != FlaUI.Core.Definitions.ControlType.Window) win2 = win2.Parent;
        Thread.Sleep(3000);
        Assert.Null(win2!.FindFirstDescendant(cf => cf.ByAutomationId("UpdateNowButton")));
        output.WriteLine("updated " + before[..12] + " -> " + Sha(exe)[..12]);
    }

    /// <summary>MENTIRA: a version whose fingerprint does not match is never installed.
    /// Una versión cuya huella no coincide no se instala nunca.</summary>
    [Fact]
    public void Lie_a_wrong_fingerprint_is_not_installed()
    {
        var (app, win, exe) = Start(Env("MESHUI_UPDATE_FEED_BAD"));
        var before = Sha(exe);
        Find(win, "UpdateNowButton").AsButton().Invoke();
        var error = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("ErrorWindow")), Timeout).Result;
        Shot("wrong-fingerprint");
        Assert.NotNull(error);
        Assert.NotNull(FindFooter(error!));
        Find(error, "ErrorClose").AsButton().Invoke();
        Assert.False(app.HasExited);
        Assert.Equal(before, Sha(exe));
        Assert.False(File.Exists(exe + ".new"));
        app.Close();
    }

    private static AutomationElement? FindFooter(AutomationElement root) =>
        root.FindAllDescendants().FirstOrDefault(e => { try { return e.Name?.EndsWith("Burburito code: 035-006-141-065") == true; } catch (Exception) { return false; } });
}
