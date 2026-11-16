using System.Net;
using System.Net.Sockets;
using System.Text;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// A UDP port mapping through the test agent to the bench's UDP echo (the mailbox container, port 7777): what goes to the
/// local port comes back, and two local senders each get their own answers. The same as the Android app's.
/// Una redirección UDP por el agente de prueba hasta el eco UDP del banco: lo que va al puerto local vuelve, y dos
/// remitentes locales reciben cada uno sus respuestas.
/// </summary>
[Trait("Category", "Integration")]
public class UdpPortsIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<Device> OnlineDevice(MeshServerSession s)
    {
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return d;
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online");
    }

    private static async Task<string?> AskEcho(UdpClient c, int port, string text)
    {
        for (var attempt = 0; attempt < 10; attempt++) // the first datagrams wait while the relay opens / los primeros esperan a que se abra el relé
        {
            await c.SendAsync(Encoding.UTF8.GetBytes(text), new IPEndPoint(IPAddress.Loopback, port));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { var r = await c.ReceiveAsync(cts.Token); return Encoding.UTF8.GetString(r.Buffer); }
            catch (OperationCanceledException) { }
        }
        return null;
    }

    [Fact]
    public async Task Datagrams_go_to_the_device_s_network_and_answers_come_back_to_each_sender()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        var device = await OnlineDevice(s);
        var echoHost = new Uri(Env("MESHTEST_SINK")).Host;   // the mailbox's name, which the agent reaches / el nombre del buzón, al que llega el agente
        await using var map = PortMapping.Start(s, new PortTarget(device.Id, device.Name, 7777, echoHost, Udp: true));
        Assert.True(map.LocalPort > 0);

        using var one = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var two = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.Equal("hola desde uno", await AskEcho(one, map.LocalPort, "hola desde uno"));
        Assert.Equal("y desde dos", await AskEcho(two, map.LocalPort, "y desde dos"));
        Assert.Equal("otra vez uno", await AskEcho(one, map.LocalPort, "otra vez uno"));
        Assert.Equal(2, map.TotalConnections);
        Assert.Null(map.LastError);
    }
}
