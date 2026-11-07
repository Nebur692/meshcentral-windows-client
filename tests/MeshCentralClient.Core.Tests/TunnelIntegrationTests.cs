using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>Relay tunnels against the real Linux agent of run-integration.sh. / Túneles de relé contra el agente Linux real de run-integration.sh.</summary>
[Trait("Category", "Integration")]
public class TunnelIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
    }

    private static async Task<Device> OnlineDevice(MeshServerSession s)
    {
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return d;
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online / el agente de prueba no llegó a conectarse");
    }

    [Fact]
    public async Task Terminal_runs_a_command_and_shows_its_output()
    {
        await using var s = await Admin();
        var dev = await OnlineDevice(s);
        await using var term = await TerminalSession.OpenAsync(s, dev.Id, TunnelProtocol.Terminal, 100, 30);
        Assert.True(term.Tunnel.IsOpen);

        var seen = new StringBuilder();
        var marker = "mc" + Guid.NewGuid().ToString("N")[..10];
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Add(string t) { lock (seen) { seen.Append(t); if (seen.ToString().Contains(marker + "-ok")) got.TrySetResult(); } }
        term.Tunnel.DataReceived += (_, d) => Add(Encoding.UTF8.GetString(d.Span));
        term.Tunnel.TextReceived += (_, t) => Add(t);

        await term.ResizeAsync(120, 40);
        // The shell echoes the line; only the command's own output has "-ok" right after the marker.
        // La shell repite la línea; solo la salida de la orden lleva «-ok» justo detrás de la marca.
        await term.WriteAsync($"echo {marker}-$((40+2)) && echo {marker}-o\"k\"\n");
        await got.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (seen)
        {
            output.WriteLine(seen.ToString());
            Assert.Contains(marker + "-42", seen.ToString()); // the shell really evaluated it / la shell lo evaluó de verdad
        }
    }

    [Fact]
    public async Task Terminal_reports_when_the_shell_exits()
    {
        await using var s = await Admin();
        var dev = await OnlineDevice(s);
        await using var term = await TerminalSession.OpenAsync(s, dev.Id, TunnelProtocol.Terminal, 80, 25);
        var closed = new TaskCompletionSource<MeshClientException>(TaskCreationOptions.RunContinuationsAsynchronously);
        term.Tunnel.Closed += (_, e) => closed.TrySetResult(e);
        await term.WriteAsync("exit\n");
        var e = await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("tunel/ver-pantalla/conexion-cortada", e.BurburitoKey);
        Assert.False(term.Tunnel.IsOpen);
        var late = await Assert.ThrowsAsync<MeshClientException>(() => term.WriteAsync("x"));
        Assert.Equal(MeshFailure.ConnectionLost, late.Failure);
    }

    /// <summary>MENTIRA: a device that does not exist cannot look like an open terminal.
    /// Un equipo que no existe no puede parecer una terminal abierta.</summary>
    [Fact]
    public async Task Lie_a_missing_device_fails_with_its_code()
    {
        await using var s = await Admin();
        var fake = "node//" + Convert.ToBase64String(new byte[48]).Replace('+', '@').Replace('/', '$');
        var e = await Assert.ThrowsAsync<MeshClientException>(() => TerminalSession.OpenAsync(s, fake, TunnelProtocol.Terminal, 80, 25));
        output.WriteLine($"{e.Failure} {e.BurburitoKey} {e.Message}");
        Assert.StartsWith("tunel/abrir-detalle/", e.BurburitoKey);
        Assert.True(Burburito.IsKnown(e.BurburitoKey));
    }

    [Fact]
    public async Task A_user_without_rights_cannot_open_a_terminal()
    {
        await using var admin = await Admin();
        var dev = await OnlineDevice(admin);
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var user = await MeshServerSession.ConnectAsync(a!, new("user", "User-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        var e = await Assert.ThrowsAsync<MeshClientException>(() => TerminalSession.OpenAsync(user, dev.Id, TunnelProtocol.Terminal, 80, 25));
        output.WriteLine($"{e.Failure} {e.BurburitoKey} {e.Message}");
        Assert.StartsWith("tunel/abrir-detalle/", e.BurburitoKey);
    }
}
