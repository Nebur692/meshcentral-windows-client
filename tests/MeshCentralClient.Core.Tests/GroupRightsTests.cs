using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// What a person can do with a device group, as the website works it out: editing, users, deleting (only with every
/// right), adding devices (agent groups, manage devices, and not forbidden server-wide) and moving devices.
/// Lo que puede hacer una persona con un grupo de equipos, como lo calcula la web: editar, usuarios, borrar (solo con
/// todos los permisos), añadir equipos (grupos de agentes, gestionar equipos y sin prohibición del servidor) y mover.
/// </summary>
public class GroupRightsTests
{
    private const long Full = 0xFFFFFFFF;
    private static readonly ServerInfo Server = new("s", "", "1.3.1");

    private static UserInfo User(long site = 0) => new("user//ana", "Ana", site == Full) { SiteRights = site };

    private static DeviceGroup Group(string id, long rights, DeviceGroupKind kind = DeviceGroupKind.Agents) =>
        new(id, id, null, kind) { Links = new Dictionary<string, long> { ["user//ana"] = rights } };

    private static Device Dev(string group) => new("node//d", group, "PC", null, null, 1, DeviceConnectivity.Agent, 1, AgentCapabilities.None, []);

    [Fact]
    public void Every_right_allows_everything()
        => Assert.Equal(new GroupAbilities(true, true, true, true, true), MeshServerSession.GroupAbilitiesFor(User(), Server, Group("mesh//a", Full)));

    [Fact]
    public void Deleting_needs_every_right_not_just_many()
    {
        var almost = (long)(MeshRights.EditGroup | MeshRights.ManageUsers | MeshRights.ManageDevices | MeshRights.RemoteControl);
        Assert.Equal(new GroupAbilities(true, true, false, true, true), MeshServerSession.GroupAbilitiesFor(User(), Server, Group("mesh//a", almost)));
    }

    [Fact]
    public void Without_a_link_nothing() => Assert.Equal(new GroupAbilities(false, false, false, false, false),
        MeshServerSession.GroupAbilitiesFor(User(), Server, new("mesh//a", "a", null, DeviceGroupKind.Agents)));

    [Fact]
    public void Only_agent_groups_take_agents_and_the_server_can_forbid_it()
    {
        Assert.False(MeshServerSession.GroupAbilitiesFor(User(), Server, Group("mesh//a", Full, DeviceGroupKind.IntelAmtOnly)).AddDevices);
        Assert.False(MeshServerSession.GroupAbilitiesFor(User(4096), Server, Group("mesh//a", Full)).AddDevices); // siteadmin bit 4096
        Assert.True(MeshServerSession.GroupAbilitiesFor(User(Full), Server, Group("mesh//a", Full)).AddDevices);  // a full administrator is never limited
    }

    [Fact]
    public void Moving_needs_edit_rights_on_the_device_and_a_target_of_the_same_kind_that_takes_devices()
    {
        var take = (long)(MeshRights.EditGroup | MeshRights.ManageDevices);
        var from = Group("mesh//from", (long)MeshRights.EditGroup);
        var d = Dev("mesh//from");
        Assert.True(MeshServerSession.CanMove(User(), Server, from, d, Group("mesh//to", take)));
        // Into its own group, or without knowing its group: no. / A su propio grupo, o sin conocer su grupo: no.
        Assert.False(MeshServerSession.CanMove(User(), Server, from, d, from));
        Assert.False(MeshServerSession.CanMove(User(), Server, null, d, Group("mesh//to", take)));
        // The server drops a move between kinds without answering. / El servidor tira sin contestar un cambio entre tipos.
        Assert.False(MeshServerSession.CanMove(User(), Server, from, d, Group("mesh//to", take, DeviceGroupKind.LocalDevices)));
        // The target must take devices (edit + manage devices). / El destino tiene que aceptar equipos.
        Assert.False(MeshServerSession.CanMove(User(), Server, from, d, Group("mesh//to", (long)MeshRights.EditGroup)));
        // The device's own group must let her edit it. / El grupo del propio equipo tiene que dejarla editarlo.
        Assert.False(MeshServerSession.CanMove(User(), Server, Group("mesh//from", (long)MeshRights.ManageDevices), d, Group("mesh//to", take)));
    }
}
