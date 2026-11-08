using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Port mapping through the real Linux agent of run-integration.sh: the agent reaches the server's own website
/// (mesh.test:443, which it knows by name), so what comes back can only have crossed the relay.
/// Redirección de puertos por el agente Linux real: el agente llega a la web del propio servidor (mesh.test:443), así que
/// lo que vuelve solo puede haber cruzado el relé.
/// </summary>
[Trait("Category", "Integration")]
public class PortsIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> SignIn(string user, string password)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new(user, password), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
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

    /// <summary>The website's title, asked directly to the server. / El título de la web, pedido directamente al servidor.</summary>
    private static async Task<string> DirectTitle()
    {
        var (_, body) = await Get("mesh.test", 443);
        return Title(body);
    }

    private static string Title(string body)
    {
        var i = body.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        Assert.True(i >= 0, "no <title> in: " + body[..Math.Min(300, body.Length)]);
        return body[i..body.IndexOf("</title>", i, StringComparison.OrdinalIgnoreCase)];
    }

    /// <summary>An HTTPS request through 127.0.0.1:<paramref name="port"/>; returns the status line, having checked the page is
    /// the same one the server gives directly. / Una petición HTTPS por el puerto local; devuelve la línea de estado,
    /// comprobado que la página es la misma que da el servidor directamente.</summary>
    private static async Task<string> GetThrough(int port)
    {
        var (status, body) = await Get("127.0.0.1", port);
        Assert.Equal(await DirectTitle(), Title(body));
        return status;
    }

    private static async Task<(string Status, string Body)> Get(string host, int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("mesh.test");
        await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: mesh.test\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(tls, Encoding.UTF8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var all = await reader.ReadToEndAsync(cts.Token);
        return (all[..all.IndexOf('\r')], all);
    }

    [Fact]
    public async Task A_mapped_port_reaches_the_server_website_through_the_agent()
    {
        await using var s = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(s);
        Assert.True(s.CanRelayThrough(dev));
        await using var map = PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 443, "mesh.test"));
        Assert.NotEqual(0, map.LocalPort);
        Assert.StartsWith("HTTP/1.1 200", await GetThrough(map.LocalPort), StringComparison.Ordinal);
        Assert.Equal(1, map.TotalConnections);
        Assert.True(map.BytesUp > 0 && map.BytesDown > 1000, $"up {map.BytesUp}, down {map.BytesDown}");
        Assert.Null(map.LastError);
    }

    [Fact]
    public async Task Several_connections_at_once_each_get_their_own_relay()
    {
        await using var s = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(s);
        await using var map = PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 443, "mesh.test"));
        var lines = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GetThrough(map.LocalPort)));
        Assert.All(lines, l => Assert.StartsWith("HTTP/1.1 200", l, StringComparison.Ordinal));
        Assert.Equal(4, map.TotalConnections);
        for (var i = 0; i < 40 && map.ActiveConnections > 0; i++) await Task.Delay(250);
        Assert.Equal(0, map.ActiveConnections);
    }

    /// <summary>Measured: what a connection to a port nobody listens on does. / Medido: qué hace una conexión a un puerto donde no escucha nadie.</summary>
    [Fact]
    public async Task A_port_nobody_listens_on_closes_the_local_connection()
    {
        await using var s = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(s);
        await using var map = PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 1, "mesh.test"));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", map.LocalPort);
        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var n = await tcp.GetStream().ReadAsync(new byte[16], cts.Token);
        output.WriteLine($"closed after {watch.ElapsedMilliseconds} ms, read {n}, last error: {map.LastError?.BurburitoKey ?? "none"}");
        Assert.Equal(0, n);
        Assert.Equal(0, map.BytesDown);
    }

    [Fact]
    public async Task A_local_port_already_in_use_fails_before_reaching_the_server()
    {
        await using var s = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(s);
        await using var first = PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 443, "mesh.test"));
        var e = Assert.Throws<MeshClientException>(() => PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 443, "mesh.test", first.LocalPort)));
        Assert.Equal("redireccion/redirigir-puerto/conflicto-estado", e.BurburitoKey);
    }

    [Fact]
    public async Task A_user_without_rights_is_not_offered_it_and_the_server_refuses_it_anyway()
    {
        await using var admin = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(admin);
        await using var user = await SignIn("user", "User-Pass-1234");
        Assert.False(user.CanRelayThrough(dev));
        // Lie: sent anyway, the server closes it. / Mentira: mandada igualmente, el servidor la cierra.
        var e = await Assert.ThrowsAsync<MeshClientException>(() => TcpRelay.OpenAsync(user, dev.Id, 443, "mesh.test"));
        output.WriteLine($"{e.BurburitoKey}: {e.Message}");
        Assert.StartsWith("redireccion/llevar-conexion/", e.BurburitoKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_outside_the_tcp_range_is_refused_here()
    {
        await using var s = await SignIn("admin", "Admin-Pass-1234");
        var dev = await OnlineDevice(s);
        Assert.Equal("redireccion/redirigir-puerto/dato-invalido",
            Assert.Throws<MeshClientException>(() => PortMapping.Start(s, new PortTarget(dev.Id, dev.Name, 70000))).BurburitoKey);
        Assert.Equal("redireccion/llevar-conexion/dato-invalido",
            (await Assert.ThrowsAsync<MeshClientException>(() => TcpRelay.OpenAsync(s, dev.Id, 0))).BurburitoKey);
    }
}
