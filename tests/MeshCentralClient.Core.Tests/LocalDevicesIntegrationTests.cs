using System.Net.Sockets;
using System.Text;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Devices without an agent against the bench: a group of local devices, a device added by its address (the bench's SSH
/// server), the SSH banner and a port mapping to it through the server alone; and through the group's relay agent to a
/// name only that agent resolves (solo-agente, written in the agent's /etc/hosts by run-integration.sh). The same as the
/// Android app's LocalDevicesIntegrationTest.
/// Equipos sin agente contra el banco: un grupo de equipos locales, un equipo añadido por su dirección (el servidor SSH
/// del banco), el saludo SSH y una redirección solo por el servidor; y por el agente relé del grupo hasta un nombre que solo
/// resuelve ese agente. Igual que LocalDevicesIntegrationTest de Android.
/// </summary>
[Trait("Category", "Integration")]
public class LocalDevicesIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }

    private static Task<MeshServerSession> Admin() =>
        MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));

    private static async Task<Device> OnlineAgent(MeshServerSession s)
    {
        await s.GetDeviceGroupsAsync();
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.HasAgent);
            if (d is not null) return d;
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online");
    }

    private static async Task<Device> Appear(MeshServerSession s, string name)
    {
        for (var i = 0; i < 20; i++)
        {
            if ((await s.GetDevicesAsync()).FirstOrDefault(d => d.Name == name) is { } d) return d;
            await Task.Delay(300);
        }
        throw new Exception($"{name} did not appear");
    }

    /// <summary>The first line port 22 sends, through a mapping on 127.0.0.1. / La primera línea del puerto 22, por una redirección.</summary>
    private static async Task<string?> Banner(MeshServerSession s, Device d)
    {
        await using var map = PortMapping.Start(s, new PortTarget(d.Id, d.Name, 22));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", map.LocalPort);
        using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await reader.ReadLineAsync(cts.Token);
    }

    [Fact]
    public async Task A_device_without_an_agent_is_reached_by_the_server_itself_and_through_the_group_s_relay_agent()
    {
        await using var s = await Admin();
        var made = new List<string>();
        try
        {
            var agent = await OnlineAgent(s);

            // No relay: the server connects itself. / Sin relé: conecta el propio servidor.
            var direct = await s.CreateLocalGroupAsync($"Locales {Guid.NewGuid():N}");
            made.Add(direct);
            var g = (await s.GetDeviceGroupsAsync()).First(x => x.Id == direct);
            Assert.Equal(DeviceGroupKind.LocalDevices, g.Kind);
            Assert.Null(g.RelayId);
            await s.AddLocalDeviceAsync(g, "caja-ssh", Env("MESHTEST_SSH_HOST"), LocalDeviceOs.Linux);
            var box = await Appear(s, "caja-ssh");
            Assert.True(box.IsLocal && s.IsLocal(box) && !box.HasAgent);
            Assert.Equal(Env("MESHTEST_SSH_HOST"), box.Address);
            Assert.StartsWith("SSH-2.0-", await Banner(s, box));

            // Its address changes; where nothing answers, the person is told so. / Su dirección cambia; donde no contesta nadie, se dice.
            await s.ChangeDeviceAsync(box.Id, host: "nadie-contesta.invalid");
            Assert.Equal("nadie-contesta.invalid", (await s.GetDevicesAsync()).First(d => d.Id == box.Id).Address);
            var gone = await Assert.ThrowsAsync<MeshClientException>(() => TcpRelay.OpenAsync(s, box.Id, 22));
            Assert.Equal(TcpRelay.UnreachableKey, gone.BurburitoKey);

            // Through the agent, to a name only the agent resolves. / Por el agente, a un nombre que solo resuelve él.
            var relayed = await s.CreateLocalGroupAsync($"Locales relé {Guid.NewGuid():N}", relayId: agent.Id);
            made.Add(relayed);
            var rg = (await s.GetDeviceGroupsAsync()).First(x => x.Id == relayed);
            Assert.Equal(agent.Id, rg.RelayId);
            await s.AddLocalDeviceAsync(rg, "detras-del-agente", Env("MESHTEST_AGENT_ONLY_HOST"), LocalDeviceOs.Linux);
            var behind = await Appear(s, "detras-del-agente");
            Assert.Equal(agent.Id, s.RelayOf(behind));
            Assert.StartsWith("SSH-2.0-", await Banner(s, behind));

            // What the server would refuse is refused before sending. / Lo que rechazaría el servidor, antes de mandarlo.
            var agents = (await s.GetDeviceGroupsAsync()).First(x => x.Id == agent.GroupId);
            var wrong = await Assert.ThrowsAsync<MeshClientException>(() => s.AddLocalDeviceAsync(agents, "x", "10.0.0.1", LocalDeviceOs.Windows));
            Assert.Equal("grupos/gestionar-grupos/dato-invalido", wrong.BurburitoKey);
            var spaces = await Assert.ThrowsAsync<MeshClientException>(() => s.AddLocalDeviceAsync(g, "x", "con espacios", LocalDeviceOs.Windows));
            Assert.Equal("grupos/gestionar-grupos/dato-invalido", spaces.BurburitoKey);
        }
        finally
        {
            foreach (var id in made)
                if ((await s.GetDeviceGroupsAsync()).FirstOrDefault(x => x.Id == id) is { } g)
                    try { await s.DeleteGroupAsync(g); } catch (MeshClientException) { }
        }
    }
}
