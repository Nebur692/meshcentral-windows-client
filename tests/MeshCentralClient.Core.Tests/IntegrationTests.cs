using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Against a real, throwaway MeshCentral started by tests/server/run-integration.sh. Without its variables these
/// tests FAIL (they do not skip): a check that does not run must not look green.
/// Contra un MeshCentral real y desechable que arranca tests/server/run-integration.sh. Sin sus variables estas
/// pruebas FALLAN (no se saltan): una comprobación que no corre no puede parecer verde.
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests(ITestOutputHelper output)
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{name} is not set: run tests/server/run-integration.sh / {name} no está: pasa tests/server/run-integration.sh");

    private static ServerAddress Server
    {
        get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; }
    }

    private static CertificateTrust Pinned => new(Env("MESHTEST_CERT_SHA256"));

    private static async Task<MeshClientException> Fails(PasswordCredentials c, CertificateTrust? t = null) =>
        await Assert.ThrowsAsync<MeshClientException>(() => MeshServerSession.ConnectAsync(Server, c, t ?? Pinned));

    [Fact]
    public async Task Untrusted_certificate_is_refused_and_reports_its_fingerprint()
    {
        var e = await Fails(new("admin", "Admin-Pass-1234"), new CertificateTrust());
        Assert.Equal(MeshFailure.UntrustedCertificate, e.Failure);
        Assert.Equal(Env("MESHTEST_CERT_SHA256"), e.CertificateSha256);
        Assert.Contains("mesh.test", e.CertificateSubject);
    }

    [Fact]
    public async Task Lie_a_different_pin_is_refused()
    {
        var wrong = new string(Env("MESHTEST_CERT_SHA256").Reverse().ToArray());
        var e = await Fails(new("admin", "Admin-Pass-1234"), new CertificateTrust(wrong));
        Assert.Equal(MeshFailure.UntrustedCertificate, e.Failure);
    }

    [Fact]
    public async Task Logs_in_and_reads_who_it_is()
    {
        await using var s = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        Assert.Equal("user//admin", s.User.Id);
        Assert.True(s.User.IsFullAdministrator);
        Assert.Equal("mesh.test", s.ServerInfo.Name);
        Assert.True(s.IsOpen);
    }

    [Fact]
    public async Task Wrong_password_is_invalid_credentials()
    {
        var e = await Fails(new("admin", "wrong"));
        Assert.Equal(MeshFailure.InvalidCredentials, e.Failure);
        Assert.Equal("035-001-001-041", e.BurburitoCode);
    }

    [Fact]
    public async Task Unknown_user_is_invalid_credentials() =>
        Assert.Equal(MeshFailure.InvalidCredentials, (await Fails(new("nobody", "x"))).Failure);

    [Fact]
    public async Task Two_factor_flow()
    {
        var asked = await Fails(new("otp", "Otp-Pass-1234"));
        Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);

        var wrong = await Fails(new("otp", "Otp-Pass-1234", "000000"));
        Assert.Equal(MeshFailure.InvalidTwoFactorCode, wrong.Failure);

        await using var s = await MeshServerSession.ConnectAsync(Server, new("otp", "Otp-Pass-1234", Totp.Code(Env("MESHTEST_OTP_SECRET"))), Pinned);
        Assert.Equal("user//otp", s.User.Id);
        Assert.False(s.User.IsFullAdministrator);
    }

    [Fact]
    public async Task Lists_groups_and_the_test_device()
    {
        await using var s = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        var groups = await s.GetDeviceGroupsAsync();
        var group = Assert.Single(groups, g => g.Name == "Test group");
        Assert.Equal(Env("MESHTEST_GROUP_ID"), group.Id);
        Assert.Equal(DeviceGroupKind.Agents, group.Kind);

        // The agent may still be connecting: poll the condition, not a fixed sleep.
        // El agente puede estar conectándose aún: se espera a la condición, no un tiempo fijo.
        Device? dev = null;
        for (var i = 0; i < 60 && dev is not { IsOnline: true }; i++)
        {
            dev = (await s.GetDevicesAsync()).FirstOrDefault(d => d.GroupId == group.Id);
            if (dev is not { IsOnline: true }) await Task.Delay(500);
        }
        Assert.NotNull(dev);
        output.WriteLine($"{dev!.Name} {dev.OsDescription} {dev.Connectivity} {dev.Capabilities}");
        Assert.True(dev.IsOnline);
        Assert.True(dev.Connectivity.HasFlag(DeviceConnectivity.Agent));
        Assert.True(dev.Capabilities.HasFlag(AgentCapabilities.Terminal));
        Assert.Contains("Debian", dev.OsDescription);
    }

    [Fact]
    public async Task A_user_without_rights_sees_no_devices()
    {
        await using var s = await MeshServerSession.ConnectAsync(Server, new("user", "User-Pass-1234"), Pinned);
        Assert.Empty(await s.GetDeviceGroupsAsync());
        Assert.Empty(await s.GetDevicesAsync());
    }

    [Fact]
    public async Task Live_changes_arrive_as_events()
    {
        await using var watcher = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        var dev = (await watcher.GetDevicesAsync()).First(d => d.GroupId == Env("MESHTEST_GROUP_ID"));
        var newName = "renamed-" + Guid.NewGuid().ToString("N")[..6];
        var got = new TaskCompletionSource<DeviceChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.DeviceChanged += (_, e) => { if (e.Kind == DeviceChangeKind.Changed && e.Device?.Name == newName) got.TrySetResult(e); };

        await using var actor = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        await actor.SendAsync(new JsonObject { ["action"] = "changedevice", ["nodeid"] = dev.Id, ["name"] = newName }, default);

        var e = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(dev.Id, e.DeviceId);
        Assert.Equal(dev.GroupId, e.Device!.GroupId);
    }

    [Fact]
    public async Task Relay_cookies_are_issued()
    {
        await using var s = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        var c = await s.GetRelayCookiesAsync();
        Assert.False(string.IsNullOrEmpty(c.Cookie));
        Assert.False(string.IsNullOrEmpty(c.RelayCookie));
    }

    [Fact]
    public async Task Closed_fires_when_the_server_drops_the_session()
    {
        // MeshCentral closes every session of an account it deletes: a real server-side drop.
        // MeshCentral cierra todas las sesiones de una cuenta que borra: un corte de verdad desde el servidor.
        var name = "temp" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        await admin.SendAsync(new JsonObject { ["action"] = "adduser", ["username"] = name, ["pass"] = "Temp-Pass-1234" }, default);

        MeshServerSession? temp = null;
        for (var i = 0; i < 20 && temp is null; i++)
        {
            try { temp = await MeshServerSession.ConnectAsync(Server, new(name, "Temp-Pass-1234"), Pinned); }
            catch (MeshClientException e) when (e.Failure == MeshFailure.InvalidCredentials) { await Task.Delay(250); }
        }
        Assert.NotNull(temp);
        await using var _ = temp;
        var closed = new TaskCompletionSource<MeshClientException>(TaskCreationOptions.RunContinuationsAsynchronously);
        temp!.Closed += (_, e) => closed.TrySetResult(e);

        await admin.SendAsync(new JsonObject { ["action"] = "deleteuser", ["userid"] = "user//" + name }, default);

        var reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(MeshFailure.ConnectionLost, reason.Failure);
        Assert.False(temp.IsOpen);
        var late = await Assert.ThrowsAsync<MeshClientException>(() => temp.GetDevicesAsync());
        Assert.Equal(MeshFailure.ConnectionLost, late.Failure);
    }
}
