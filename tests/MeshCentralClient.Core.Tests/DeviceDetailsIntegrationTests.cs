using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>The device page against the real Linux agent. / La ficha del equipo contra el agente Linux real.</summary>
[Trait("Category", "Integration")]
public class DeviceDetailsIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> As(string user, string pass)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new(user, pass), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
    }

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

    private static string Fake() => "node//" + Convert.ToBase64String(new byte[48]).Replace('+', '@').Replace('/', '$');

    [Fact]
    public async Task Inventory_network_and_last_connection()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);

        // The agent sends its inventory a moment after connecting. / El agente manda su inventario poco después de conectar.
        SystemInfo? info = null;
        for (var i = 0; i < 40 && info is null; i++) { info = await s.GetSystemInfoAsync(d.Id); if (info is null) await Task.Delay(500); }
        Assert.NotNull(info);
        output.WriteLine(string.Join("\n", info!.Flatten().Take(12).Select(p => $"{p.Path} = {p.Value}")));
        Assert.False(string.IsNullOrEmpty(info.Find("cpu_name")));
        Assert.Contains(info.Flatten(), p => p.Path.StartsWith("agentvers"));

        var nics = await s.GetNetworkInfoAsync(d.Id);
        var eth = Assert.Single(nics, n => n.Name == "eth0");
        Assert.Contains(eth.Addresses, a => a.Family == "IPv4" && a.Address == d.Address);

        var (time, addr) = await s.GetLastConnectAsync(d.Id);
        Assert.NotNull(time);
        Assert.True(time > DateTimeOffset.UtcNow.AddHours(-1));
        Assert.StartsWith(d.Address! + ":", addr);
        Assert.NotNull(d.LastBoot);
        Assert.False(string.IsNullOrEmpty(d.AgentCore));
    }

    [Fact]
    public async Task Description_and_tags_are_changed_and_show_up_in_the_history()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var desc = "desc-" + Guid.NewGuid().ToString("N")[..8];
        await s.ChangeDeviceAsync(d.Id, description: desc, tags: new[] { "zeta", "alfa" });
        var after = (await s.GetDevicesAsync()).Single(x => x.Id == d.Id);
        Assert.Equal(desc, after.Description);
        Assert.Equal(new[] { "alfa", "zeta" }, after.Tags.OrderBy(t => t));
        var events = await s.GetEventsAsync(d.Id, 20);
        Assert.Contains(events, e => e.Action == "changenode" && e.User == "Admin" && e.Time > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Notes_are_saved_read_back_and_cleared()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var text = "línea 1\nlínea 2 ñ€ " + Guid.NewGuid().ToString("N")[..6];
        await s.SetNotesAsync(d.Id, text);
        string? back = null;
        for (var i = 0; i < 20 && back != text; i++) { back = await s.GetNotesAsync(d.Id); if (back != text) await Task.Delay(150); }
        Assert.Equal(text, back);
        await s.SetNotesAsync(d.Id, "");
        for (var i = 0; i < 20 && back is not null; i++) { back = await s.GetNotesAsync(d.Id); if (back is not null) await Task.Delay(150); }
        Assert.Null(back);
    }

    /// <summary>MENTIRA: a device that does not exist cannot look like one with an empty page.
    /// Un equipo que no existe no puede parecer uno con la ficha vacía.</summary>
    [Fact]
    public async Task Lie_a_missing_device_says_so()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var fake = Fake();
        Assert.Equal("ficha/abrir-detalle/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => s.GetSystemInfoAsync(fake))).BurburitoKey);
        Assert.Equal("ficha/abrir-detalle/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => s.GetNetworkInfoAsync(fake))).BurburitoKey);
        Assert.Equal((null, null), await s.GetLastConnectAsync(fake));
        Assert.Empty(await s.GetEventsAsync(fake));
        Assert.Equal("ficha/cambiar-ajustes/sin-permiso", (await Assert.ThrowsAsync<MeshClientException>(() => s.ChangeDeviceAsync(fake, description: "x"))).BurburitoKey);
    }

    [Fact]
    public async Task An_account_without_rights_gets_no_inventory_and_cannot_change_it()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(admin);
        await using var user = await As("user", "User-Pass-1234");
        Assert.Equal("ficha/abrir-detalle/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => user.GetSystemInfoAsync(d.Id))).BurburitoKey);
        Assert.Equal("ficha/abrir-detalle/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => user.GetNetworkInfoAsync(d.Id))).BurburitoKey);
        Assert.Equal("ficha/cambiar-ajustes/sin-permiso", (await Assert.ThrowsAsync<MeshClientException>(() => user.ChangeDeviceAsync(d.Id, description: "x"))).BurburitoKey);
        // Without access the server does not answer getNotes at all. / Sin acceso el servidor no contesta a getNotes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => user.GetNotesAsync(d.Id, cts.Token));
    }
}
