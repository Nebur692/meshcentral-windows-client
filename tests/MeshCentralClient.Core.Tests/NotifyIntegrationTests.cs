using MeshCentralClient.Core;
using Xunit;

/// <summary>The account's notifications about a group and a device are set, read back from the server and cleared.
/// Los avisos de la cuenta sobre un grupo y un equipo se ponen, se leen del servidor y se quitan.</summary>
[Trait("Category", "Integration")]
public class NotifyIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    [Fact]
    public async Task Group_and_device_notifications_are_kept_by_the_server()
    {
        await using var s = await Admin();
        var group = (await s.GetDeviceGroupsAsync()).First(g => g.Id == Env("MESHTEST_GROUP_ID"));
        var device = (await s.GetDevicesAsync()).First(d => d.GroupId == group.Id);
        try
        {
            await s.SetGroupNotifyAsync(group, DeviceNotify.EmailDisconnect | DeviceNotify.MessageConnect);
            await s.SetDeviceNotifyAsync(device, DeviceNotify.MessageDisconnect);
            Assert.Equal(DeviceNotify.EmailDisconnect | DeviceNotify.MessageConnect, s.NotifyOn(group.Id));

            // A new session reads them from the server. / Una sesión nueva los lee del servidor.
            await using (var again = await Admin())
            {
                Assert.Equal(DeviceNotify.EmailDisconnect | DeviceNotify.MessageConnect, again.NotifyOn(group.Id));
                Assert.Equal(DeviceNotify.MessageDisconnect, again.NotifyOn(device.Id));
            }
            await s.SetDeviceNotifyAsync(device, DeviceNotify.MessageDisconnect); // the same again: nothing to do / lo mismo: nada que hacer
        }
        finally
        {
            await s.SetGroupNotifyAsync(group, DeviceNotify.None);
            await s.SetDeviceNotifyAsync(device, DeviceNotify.None);
        }
        await using var after = await Admin();
        Assert.Equal(DeviceNotify.None, after.NotifyOn(group.Id));
        Assert.Equal(DeviceNotify.None, after.NotifyOn(device.Id));
    }
}
