using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// The agent's console answers a command; the device's icon, own ports and place on the map are kept by the server and
/// the usual port takes the own one away. / La consola del agente contesta; el icono, los puertos propios y el sitio en
/// el mapa los guarda el servidor, y el puerto normal quita el propio.
/// </summary>
[Trait("Category", "Integration")]
public class Phase18IntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<(MeshServerSession, Device)> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        Device? d = null;
        for (var i = 0; i < 60 && d is null; i++) { d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.HasAgent); if (d is null) await Task.Delay(500); }
        Assert.NotNull(d);
        return (s, d!);
    }

    [Fact]
    public async Task The_agent_console_answers_a_command()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        Assert.True(s.CanUseAgentConsole(d));
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        s.ConsoleOutput += (_, t) => { if (t.DeviceId == d.Id) said.Enqueue(t.Text); };
        await s.SendConsoleCommandAsync(d, "help");
        for (var i = 0; i < 80 && !string.Concat(said).Contains("info", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(250);
        Assert.Contains("info", string.Concat(said), StringComparison.OrdinalIgnoreCase);   // help lists the commands / help lista las órdenes
        var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.SendConsoleCommandAsync(d, "  "));
        Assert.Equal("ficha/consola/dato-invalido", bad.BurburitoKey);
    }

    [Fact]
    public async Task Icon_own_ports_and_place_are_kept_and_the_usual_port_removes_the_own()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        Assert.True(s.ServerKeepsLocations, "the bench's server has geolocation on");
        var before = DeviceSettings.Of(d);
        async Task<Device> Reread(Func<Device, bool> done)
        {
            for (var i = 0; i < 40; i++)
            {
                var x = (await s.GetDevicesAsync()).First(y => y.Id == d.Id);
                if (done(x)) return x;
                await Task.Delay(250);
            }
            return (await s.GetDevicesAsync()).First(y => y.Id == d.Id);
        }
        try
        {
            await s.SaveDeviceSettingsAsync(d, before with { Icon = DeviceIcon.Server, SshPort = 2222, RdpPort = 3390, Location = new(40.4168, -3.7038) });
            var now = await Reread(x => x.SshPort == 2222 && x.Location is not null);
            Assert.Equal((int)DeviceIcon.Server, now.Icon);
            Assert.Equal(2222, now.SshPort);
            Assert.Equal(3390, now.RdpPort);
            Assert.Null(now.VncPort);
            Assert.Equal(40.4168, now.Location!.Latitude, 4);
            Assert.Equal(-3.7038, now.Location.Longitude, 4);

            // Back to the usual: the server drops the own ports and the place. / A lo normal: el servidor quita los propios.
            await s.SaveDeviceSettingsAsync(now, DeviceSettings.Of(now) with { SshPort = null, RdpPort = null, Location = null });
            var back = await Reread(x => x.SshPort is null && x.Location is null);
            Assert.Null(back.SshPort);
            Assert.Null(back.RdpPort);
            Assert.Null(back.Location);

            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.SaveDeviceSettingsAsync(back, DeviceSettings.Of(back) with { SshPort = 70000 }));
            Assert.Equal("ficha/cambiar-ajustes/dato-invalido", bad.BurburitoKey);
        }
        finally
        {
            var x = (await s.GetDevicesAsync()).First(y => y.Id == d.Id);
            await s.SaveDeviceSettingsAsync(x, before);
        }
    }
}
