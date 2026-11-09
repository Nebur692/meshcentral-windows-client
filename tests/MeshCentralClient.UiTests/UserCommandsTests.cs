using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Commands as the signed-in user against this Windows' real agent, a connect-mode one: the agent's own runAsUser never
/// answers here, so getting an answer at all is what this proves. Who it runs as on a service agent is measured elsewhere
/// (on a real Windows 11 PC): here both are the same account.
/// Órdenes como el usuario con sesión contra el agente real de este Windows, en modo connect: el runAsUser del agente no
/// contesta nunca aquí, así que contestar ya es lo que se prueba. Como quién corre en un agente de servicio se mide aparte
/// (en un PC real con Windows 11): aquí ambos son la misma cuenta.
/// </summary>
[Trait("Category", "UI")]
public sealed class UserCommandsTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    private static async Task<(MeshServerSession, Device)> Open()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return (s, (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName));
    }

    [Fact]
    public async Task Cmd_and_powershell_answer_as_the_user_and_an_exit_or_quotes_break_nothing()
    {
        var (s, me) = await Open();
        await using var _ = s;
        // Expanded there, so it cannot be the echo of what was typed. / Expandido allí, así que no es el eco de lo tecleado.
        var cmd = await s.RunCommandsAsync(me, "echo uno-%USERNAME%", CommandShell.Cmd, asUser: true);
        output.WriteLine(cmd);
        Assert.Contains("uno-" + Environment.UserName, cmd, StringComparison.OrdinalIgnoreCase);

        var ps = await s.RunCommandsAsync(me, "Write-Output (\"dos-\" + $env:USERNAME + '-' + (6*7))\nexit\nWrite-Output 'no-llega'", CommandShell.PowerShell, asUser: true);
        output.WriteLine(ps);
        Assert.Contains($"dos-{Environment.UserName}-42", ps, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no-llega\n", ps.Replace("\r", ""), StringComparison.Ordinal);

        var quotes = await s.RunCommandsAsync(me, "Write-Output \"tres'@\"\nWrite-Output ([string][char]0xF1 + 'ñandú')", CommandShell.PowerShell, asUser: true);
        File.WriteAllText(Path.Combine(Env("MESHUI_SHOTS"), "usercommands-output.txt"), string.Join("\n---\n", cmd, ps, quotes), System.Text.Encoding.UTF8);
        Assert.Contains("tres'@", quotes, StringComparison.Ordinal);

        // And the agent's own way still works afterwards: nothing left it busy. / Y la vía del agente sigue yendo: nada lo dejó ocupado.
        Assert.Contains("cuatro", await s.RunCommandsAsync(me, "echo cuatro", CommandShell.Cmd), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_that_never_end_are_stopped_there_and_say_so()
    {
        var (s, me) = await Open();
        await using var _ = s;
        var mark = "mcc" + Guid.NewGuid().ToString("N")[..8];
        var e = await Assert.ThrowsAsync<MeshClientException>(() => s.RunCommandsAsync(me,
            $"title {mark}\nping -n 300 127.0.0.1", CommandShell.Cmd, asUser: true, timeout: TimeSpan.FromSeconds(8)));
        Assert.Equal("acciones/ejecutar-ordenes/tiempo-agotado", e.BurburitoKey);
        // The ping, a grandchild of the shell, did not stay behind. / El ping, nieto del intérprete, no se quedó.
        var left = await s.RunCommandsAsync(me, "(Get-CimInstance Win32_Process -Filter \"Name='ping.exe'\" | Where-Object CommandLine -match '-n 300').Count", CommandShell.PowerShell);
        output.WriteLine("left: " + left);
        Assert.Equal("0", left.Trim());
    }
}
