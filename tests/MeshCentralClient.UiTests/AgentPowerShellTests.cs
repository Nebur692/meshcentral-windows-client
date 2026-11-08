using System.Diagnostics;
using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>Measures how the real Windows agent runs PowerShell as the user. / Mide cómo ejecuta PowerShell como el usuario el agente real de Windows.</summary>
[Trait("Category", "UI")]
public sealed class AgentPowerShellTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static string Show(string s) => new(s.Take(1500).Select(c => c < 32 && c != '\n' ? '¤' : c).ToArray());

    private async Task Through(MeshServerSession s, Device me, TunnelProtocol kind)
    {
        var watch = Stopwatch.StartNew();
        await using var t = await TerminalSession.OpenAsync(s, me.Id, kind, 100, 30);
        var seen = new StringBuilder();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Output += (_, d) => { lock (seen) { seen.Append(Encoding.UTF8.GetString(d.Span)); if (RemoteClipboard.ParseAnswer(seen.ToString()) is not null) got.TrySetResult(); } };
        await Task.Delay(2000); // the prompt / el indicador
        await t.WriteAsync(RemoteClipboard.OneLine(RemoteClipboard.ReadScript(1000)) + "\r");
        var answered = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(25))) == got.Task;
        lock (seen)
        {
            output.WriteLine($"== {kind}: answered={answered} in {watch.ElapsedMilliseconds} ms, {seen.Length} chars");
            output.WriteLine(Show(seen.ToString()));
            if (answered) output.WriteLine("answer: " + RemoteClipboard.ParseAnswer(seen.ToString())!.ToJsonString());
        }
    }

    private async Task Commands(MeshServerSession s, Device me, bool asUser, string text)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var r = await s.RunCommandsAsync(me, text, CommandShell.PowerShell, asUser, TimeSpan.FromSeconds(25));
            output.WriteLine($"== runcommands asUser={asUser}: {watch.ElapsedMilliseconds} ms\n{Show(r)}");
        }
        catch (MeshClientException e) { output.WriteLine($"== runcommands asUser={asUser}: {watch.ElapsedMilliseconds} ms FAILED {e.BurburitoKey}: {e.Message}"); }
    }

    [Fact]
    public async Task Measure()
    {
        ClipboardTool.SetText("medida");
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        var me = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName);
        await Through(s, me, TunnelProtocol.UserPowerShell);
        await Through(s, me, TunnelProtocol.PowerShell);
        await Commands(s, me, false, "Write-Output sin-usuario");
        await Commands(s, me, false, RemoteClipboard.OneLine(RemoteClipboard.ReadScript(1000)));
        await Commands(s, me, true, "Write-Output con-usuario");
    }
}
