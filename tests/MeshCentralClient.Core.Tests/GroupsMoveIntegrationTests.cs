using System.Collections.Concurrent;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Moving the real Linux agent to another group and back. It runs in its own pass (run-integration.sh): while it is
/// away, the other tests would not find their device.
/// Mover el agente Linux real a otro grupo y devolverlo. Va en su propia pasada (run-integration.sh): mientras está
/// fuera, las demás pruebas no encontrarían su equipo.
/// </summary>
[Trait("Category", "Integration")]
public class GroupsMoveIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<Device> OnlineIn(MeshServerSession s, string groupId, string? deviceId = null)
    {
        for (var i = 0; i < 80; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == groupId && x.HasAgent && (deviceId is null || x.Id == deviceId));
            if (d is not null) return d;
            await Task.Delay(250);
        }
        throw new Exception($"no online device in {groupId}");
    }

    [Fact]
    public async Task A_device_moves_to_another_group_keeps_working_there_and_comes_back()
    {
        await using var s = await GroupsIntegrationTests.As("admin", "Admin-Pass-1234");
        var home = s.GroupById(Env("MESHTEST_GROUP_ID"))!;
        var d = await OnlineIn(s, home.Id);
        var moves = new ConcurrentQueue<DeviceChangedEventArgs>();
        s.DeviceChanged += (_, e) => { if (e.DeviceId == d.Id && e.Kind == DeviceChangeKind.Changed) moves.Enqueue(e); };
        var id = await s.CreateGroupAsync("Destino " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var target = await GroupsIntegrationTests.Until(() => s.GroupById(id), "the new group");
            Assert.True(s.CanMove(d, target));
            Assert.False(s.CanMove(d, home)); // already there / ya está ahí

            await s.MoveDevicesAsync([d], target);
            // The live event carries the new group, the server lists it there, and the agent follows it.
            // El aviso en vivo trae el grupo nuevo, el servidor lo lista ahí y el agente lo sigue.
            await GroupsIntegrationTests.Until(() => moves.FirstOrDefault(e => e.Device?.GroupId == id), "the nodemeshchange event");
            var moved = await OnlineIn(s, id, d.Id);
            var said = await s.RunCommandsAsync(moved, "echo still-here", CommandShell.Shell);
            output.WriteLine(said);
            Assert.Contains("still-here", said, StringComparison.Ordinal);

            await s.MoveDevicesAsync([moved], home);
            await OnlineIn(s, home.Id, d.Id);
        }
        finally
        {
            // Never delete the group with the agent inside: that would delete the device too. / Nunca borrar el grupo con el agente dentro.
            if ((await s.GetDevicesAsync()).FirstOrDefault(x => x.Id == d.Id) is { } now && now.GroupId == id) await s.MoveDevicesAsync([now], home);
            if ((await s.GetDevicesAsync()).All(x => x.GroupId != id)) await GroupsIntegrationTests.DeleteIfThere(s, id);
        }
    }
}
