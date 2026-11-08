using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MeshCentralClient.Core;
using Xunit;

/// <summary>Temporary measurement: which way of closing a relay brings the real Windows agent down. / Medición temporal.</summary>
[Trait("Category", "Measure")]
public sealed class RelayCloseMeasure
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n)!;
    private readonly StringBuilder _log = new();
    private static readonly byte[] X224 = { 3, 0, 0, 0x13, 0x0E, 0xE0, 0, 0, 0, 0, 0, 1, 0, 8, 0, 3, 0, 0, 0 };
    private Process _agent = Process.GetProcessById(int.Parse(Env("MESHUI_AGENT_PID")));
    private string? _exe;

    private async Task<(MeshServerSession, Device)> Online()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var a));
        for (var i = 0; i < 60; i++)
        {
            var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
            await s.GetDeviceGroupsAsync();
            var me = (await s.GetDevicesAsync()).FirstOrDefault(d => d.Name == Environment.MachineName && d.IsOnline);
            if (me is not null) return (s, me);
            await s.DisposeAsync();
            await Task.Delay(1000);
        }
        throw new Exception("agent not online");
    }

    private async Task Scenario(string name, int port, Func<TcpClient, Task> use)
    {
        if (_agent.HasExited)
        {
            _agent = Process.Start(new ProcessStartInfo(_exe!, "connect") { WorkingDirectory = Path.GetDirectoryName(_exe)!, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            await Task.Delay(3000);
        }
        var (s, me) = await Online();
        var w = Stopwatch.StartNew();
        await using (s)
        {
            await using var map = PortMapping.Start(s, new PortTarget(me.Id, me.Name, port));
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", map.LocalPort);
            try { await use(tcp); } catch (Exception ex) { _log.AppendLine($"  ({name}: {ex.GetType().Name} {ex.Message})"); }
            tcp.Dispose();
            for (var i = 0; i < 40 && map.ActiveConnections > 0; i++) await Task.Delay(100);
            _log.AppendLine($"{name}: up {map.BytesUp} down {map.BytesDown} last={map.LastError?.BurburitoKey}");
        }
        w.Restart();
        while (w.Elapsed < TimeSpan.FromSeconds(45) && !_agent.HasExited) await Task.Delay(250);
        _log.AppendLine($"  => {(_agent.HasExited ? $"AGENT DOWN after {w.ElapsedMilliseconds} ms" : "agent alive after 45 s")}");
        File.WriteAllText(Path.Combine(Env("MESHUI_SHOTS"), "relay-measure.txt"), _log.ToString());
    }

    private static async Task Read(TcpClient c) { var b = new byte[4096]; using var t = new CancellationTokenSource(5000); await c.GetStream().ReadAsync(b, t.Token); }

    [Fact]
    public async Task Measure()
    {
        Directory.CreateDirectory(Env("MESHUI_SHOTS"));
        _exe = _agent.MainModule!.FileName;
        await Scenario("A rdp, closed at once, nothing sent", 3389, _ => Task.CompletedTask);
        await Scenario("B rdp, X.224 and its answer, then closed", 3389, async c => { await c.GetStream().WriteAsync(X224); await Read(c); });
        await Scenario("C rdp, X.224, then reset without reading", 3389, async c => { await c.GetStream().WriteAsync(X224); await Task.Delay(300); c.LingerState = new LingerOption(true, 0); });
        await Scenario("D ssh, banner read, then closed", 22, Read);
        await Scenario("E https, whole request, server closes first", 4443, async c =>
        {
            await using var tls = new SslStream(c.GetStream(), true, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("mesh.test");
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: mesh.test\r\nConnection: close\r\n\r\n"));
            using var r = new StreamReader(tls);
            await r.ReadToEndAsync();
        });
        await Scenario("F nobody listens", 1, Read);
    }
}
