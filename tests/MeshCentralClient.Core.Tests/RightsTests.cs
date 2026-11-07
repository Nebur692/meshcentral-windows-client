using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Rights worked out as the website's GetNodeRights does: group link, user group links, links on the device itself
/// and the rights taken away from the account.
/// Permisos calculados como GetNodeRights de la web: enlace del grupo, enlaces por grupos de usuarios, enlaces en el
/// propio equipo y los permisos quitados a la cuenta.
/// </summary>
public class RightsTests
{
    private const long Full = 0xFFFFFFFF;
    private static readonly ServerInfo Server = new("s", "", "1.3.1");

    private static UserInfo User(long? remove = null, params string[] links) =>
        new("user//ana", "Ana", false) { Links = links.ToDictionary(l => l, _ => 0L), RemoveRights = remove };

    private static DeviceGroup Group(params (string Id, long Rights)[] links) =>
        new("mesh//g", "G", null, DeviceGroupKind.Agents) { Links = links.ToDictionary(l => l.Id, l => l.Rights) };

    private static Device Dev(params (string Id, long Rights)[] links) =>
        new("node//d", "mesh//g", "PC", null, null, 1, DeviceConnectivity.Agent, 1, AgentCapabilities.None, []) { Links = links.ToDictionary(l => l.Id, l => l.Rights) };

    [Fact]
    public void No_link_no_rights() => Assert.Equal(0, MeshServerSession.DeviceRights(User(), Server, Group(("user//otro", Full)), Dev()));

    [Fact]
    public void A_missing_group_gives_nothing() => Assert.Equal(0, MeshServerSession.DeviceRights(User(), Server, null, Dev()));

    [Fact]
    public void Group_link_and_user_group_links_add_up()
    {
        var g = Group(("user//ana", (long)MeshRights.Wake), ("ugrp//tecnicos", (long)MeshRights.RemoteCommand), ("ugrp//otros", (long)MeshRights.ResetOff));
        var r = MeshServerSession.DeviceRights(User(null, "ugrp//tecnicos"), Server, g, Dev());
        Assert.Equal((long)(MeshRights.Wake | MeshRights.RemoteCommand), r); // not "otros": she is not in it / «otros» no: no está en él
    }

    [Fact]
    public void Links_on_the_device_itself_add_up()
    {
        var r = MeshServerSession.DeviceRights(User(null, "ugrp//tecnicos"), Server, Group(),
            Dev(("user//ana", (long)MeshRights.ChatNotify), ("ugrp//tecnicos", (long)MeshRights.ResetOff), ("ugrp//otros", (long)MeshRights.Wake)));
        Assert.Equal((long)(MeshRights.ChatNotify | MeshRights.ResetOff), r);
    }

    [Fact]
    public void Full_rights_through_a_user_group()
    {
        Assert.Equal(Full, MeshServerSession.DeviceRights(User(null, "ugrp//admins"), Server, Group(("ugrp//admins", Full)), Dev()));
    }

    [Fact]
    public void Manage_all_groups_gives_full_rights_without_a_link()
    {
        Assert.Equal(Full, MeshServerSession.DeviceRights(User(), Server with { ManageAllDeviceGroups = true }, Group(), Dev()));
    }

    [Fact]
    public void Removed_rights_are_taken_away_even_from_an_administrator()
    {
        var remove = (long)(MeshRights.RemoteCommand | MeshRights.ResetOff | MeshRights.RemoteControl);
        var r = MeshServerSession.DeviceRights(User(remove), Server, Group(("user//ana", Full)), Dev());
        Assert.Equal(0, r & (long)MeshRights.RemoteCommand);
        Assert.Equal(0, r & (long)MeshRights.ResetOff);
        Assert.Equal(0, r & (long)MeshRights.RemoteControl);
        Assert.NotEqual(0, r & (long)MeshRights.Wake);
        Assert.NotEqual(0, r & (long)MeshRights.ChatNotify);
    }

    [Fact]
    public void A_removed_view_becomes_a_restriction_flag()
    {
        // "No terminal" is a flag that is added, not a right that is removed. / «Sin terminal» es una marca que se añade.
        var r = MeshServerSession.DeviceRights(User((long)MeshRights.NoTerminal), Server, Group(("user//ana", (long)MeshRights.RemoteControl)), Dev());
        Assert.Equal((long)(MeshRights.RemoteControl | MeshRights.NoTerminal), r);
    }
}
