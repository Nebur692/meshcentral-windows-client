using System.Collections.Concurrent;
using System.Net.Http;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Device groups against the real server: each change is checked where it lands — the group as the server lists it
/// afterwards, the live event, what another account sees, and the agent the server hands out for the group.
/// Grupos de equipos contra el servidor real: cada cambio se comprueba donde llega — el grupo tal como lo lista luego el
/// servidor, el aviso en vivo, lo que ve otra cuenta y el agente que el servidor da para el grupo.
/// </summary>
[Trait("Category", "Integration")]
public class GroupsIntegrationTests(ITestOutputHelper output)
{
    private const long Full = 0xFFFFFFFF;

    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    internal static async Task<MeshServerSession> As(string user, string pass)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new(user, pass), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    internal static async Task<T> Until<T>(Func<T?> probe, string what) where T : class
    {
        for (var i = 0; i < 80; i++) { if (probe() is { } v) return v; await Task.Delay(125); }
        throw new Exception(what + " never happened");
    }

    private static async Task Until(Func<Task<bool>> probe, string what)
    {
        for (var i = 0; i < 80; i++) { if (await probe()) return; await Task.Delay(125); }
        throw new Exception(what + " never happened");
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..6];

    private static async Task<DeviceGroup> Listed(MeshServerSession s, string id) => (await s.GetDeviceGroupsAsync()).Single(g => g.Id == id);

    internal static async Task DeleteIfThere(MeshServerSession s, string id)
    {
        if ((await s.GetDeviceGroupsAsync()).FirstOrDefault(g => g.Id == id) is { } g) await s.DeleteGroupAsync(g);
    }

    [Fact]
    public async Task A_group_is_created_edited_and_deleted_and_the_live_events_say_so()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var seen = new ConcurrentQueue<GroupChangedEventArgs>();
        s.GroupChanged += (_, e) => seen.Enqueue(e);
        var name = "Grupo " + Tag();
        var id = await s.CreateGroupAsync(name, "made by a test");
        try
        {
            var added = await Until(() => seen.FirstOrDefault(e => e.Kind == GroupChangeKind.Added && e.GroupId == id), "the createmesh event");
            Assert.Equal(name, added.Group!.Name);
            var g = await Listed(s, id);
            Assert.Equal(("made by a test", DeviceGroupKind.Agents), (g.Description, g.Kind));
            // Whoever creates it gets every right, and the server keeps their name in the link. / Quien lo crea tiene todos los permisos.
            var me = Assert.Single(g.Members);
            Assert.Equal((s.User.Id, s.User.Name, Full), (me.Id, me.Name, me.Rights));
            Assert.Equal(new GroupAbilities(true, true, true, true, true), s.GroupAbilitiesOn(g));

            await s.EditGroupAsync(g, name + " bis", "changed");
            var changed = await Until(() => seen.FirstOrDefault(e => e.Kind == GroupChangeKind.Changed && e.Group?.Name == name + " bis"), "the meshchange event");
            Assert.Equal("changed", changed.Group!.Description);
            Assert.Equal(name + " bis", s.GroupById(id)!.Name); // the session's copy follows the event / la copia de la sesión sigue al aviso
            g = await Listed(s, id);
            Assert.Equal((name + " bis", "changed"), (g.Name, g.Description));

            // A name the server would refuse is stopped before it goes out. / Un nombre que el servidor rechazaría no llega a salir.
            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.EditGroupAsync(g, " ", null));
            Assert.Equal("grupos/gestionar-grupos/dato-invalido", bad.BurburitoKey);

            await s.DeleteGroupAsync(g);
            await Until(() => seen.FirstOrDefault(e => e.Kind == GroupChangeKind.Removed && e.GroupId == id), "the deletemesh event");
            Assert.Null(s.GroupById(id));
            Assert.DoesNotContain(await s.GetDeviceGroupsAsync(), x => x.Id == id);
        }
        finally { await DeleteIfThere(s, id); }
    }

    [Fact]
    public async Task Another_account_gets_exactly_the_rights_given_and_loses_the_group_when_taken_out()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var id = await s.CreateGroupAsync("Usuarios " + Tag());
        try
        {
            await using var u = await As("user", "User-Pass-1234");
            Assert.Null(u.GroupById(id));
            var g = await Listed(s, id);
            var some = (long)(MeshRights.Wake | MeshRights.DeviceDetails);
            var said = await s.AddMembersAsync(g, ["user"], some);
            Assert.Contains("Added user user", said, StringComparison.Ordinal);

            // The other account sees it live, with those rights and nothing it can manage. / La otra cuenta lo ve en vivo, con esos permisos.
            var hers = await Until(() => u.GroupById(id), "the group reaching the other account");
            Assert.Equal(some, MeshServerSession.GroupRights(u.User, u.ServerInfo, hers));
            Assert.Equal(new GroupAbilities(false, false, false, false, false), u.GroupAbilitiesOn(hers));
            Assert.Contains(await u.GetDeviceGroupsAsync(), x => x.Id == id);
            var member = (await Listed(s, id)).Members.Single(m => m.Id == u.User.Id);
            Assert.Equal((u.User.Name, some), (member.Name, member.Rights));

            // Without rights the app does not send what the server would ignore without answering. / Sin permiso no se manda lo que el servidor ignoraría sin contestar.
            var denied = await Assert.ThrowsAsync<MeshClientException>(() => u.EditGroupAsync(hers, "mine now", null));
            Assert.Equal("grupos/gestionar-grupos/no-autorizado", denied.BurburitoKey);
            denied = await Assert.ThrowsAsync<MeshClientException>(() => u.CreateInviteLinkAsync(hers, 1));
            Assert.Equal("grupos/invitar-agente/no-autorizado", denied.BurburitoKey);

            // More rights: now she can add devices and receive moved ones. / Más permisos: ahora puede añadir equipos y recibir movidos.
            var more = some | (long)(MeshRights.EditGroup | MeshRights.ManageDevices);
            g = await Listed(s, id);
            await s.ChangeMemberRightsAsync(g, u.User.Id, more);
            await Until(() => u.GroupById(id) is { } x && MeshServerSession.GroupRights(u.User, u.ServerInfo, x) == more ? x : null, "the new rights reaching the other account");
            Assert.Equal(new GroupAbilities(true, false, false, true, true), u.GroupAbilitiesOn(u.GroupById(id)!));
            Assert.Contains("agentinvite?c=", await u.CreateInviteLinkAsync(u.GroupById(id)!, 1), StringComparison.Ordinal);

            // Names the server does not know, and oneself, are refused. / Nombres que el servidor no conoce, y uno mismo, se rechazan.
            var unknown = await Assert.ThrowsAsync<MeshClientException>(() => s.AddMembersAsync(g, ["nobody-" + Tag()], some));
            Assert.Equal("grupos/gestionar-usuarios-grupo/no-encontrado", unknown.BurburitoKey);
            // Next to a known name the server just leaves the unknown one out: still told, and the known one was added.
            // Junto a uno conocido el servidor simplemente deja fuera el desconocido: se dice igual, y el conocido sí se añadió.
            var mixed = await Assert.ThrowsAsync<MeshClientException>(() => s.AddMembersAsync(g, ["user", "nobody-" + Tag()], more));
            Assert.Equal("grupos/gestionar-usuarios-grupo/no-encontrado", mixed.BurburitoKey);
            Assert.Contains("added: user", mixed.Message, StringComparison.Ordinal);
            var self = await Assert.ThrowsAsync<MeshClientException>(() => s.ChangeMemberRightsAsync(g, s.User.Id, some));
            Assert.Equal("grupos/gestionar-usuarios-grupo/conflicto-estado", self.BurburitoKey);

            await s.RemoveMemberAsync(await Listed(s, id), u.User.Id);
            await Until(async () => u.GroupById(id) is null && !(await u.GetDeviceGroupsAsync()).Any(x => x.Id == id), "the group leaving the other account");
            Assert.DoesNotContain((await Listed(s, id)).Members, m => m.Id == u.User.Id);
        }
        finally { await DeleteIfThere(s, id); }
    }

    [Fact]
    public async Task The_invitation_link_and_the_agent_download_belong_to_the_group()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var g = s.GroupById(Env("MESHTEST_GROUP_ID"))!;
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => new CertificateTrust(Env("MESHTEST_CERT_SHA256")).Validate(m, c, ch, e) };
        using var http = new HttpClient(handler);

        // A public page: it opens without any account and names the group. / Una página pública: se abre sin cuenta y nombra al grupo.
        var link = await s.CreateInviteLinkAsync(g, 24);
        output.WriteLine(link);
        Assert.Contains("/agentinvite?c=", link, StringComparison.Ordinal);
        var page = await http.GetStringAsync(link);
        Assert.Contains(Uri.EscapeDataString(g.Name), page, StringComparison.Ordinal);

        // The Windows agent the server gives carries THIS group inside (MeshID in its embedded settings).
        // El agente de Windows que da el servidor lleva ESTE grupo dentro (MeshID en sus ajustes incrustados).
        using var exe = new MemoryStream();
        var bytes = await s.DownloadAgentAsync(g, AgentPlatform.Windows64, AgentInstallMode.BackgroundAndInteractive, exe);
        output.WriteLine($"agent: {bytes} bytes");
        Assert.Equal(exe.Length, bytes);
        Assert.True(bytes > 1_000_000, $"only {bytes} bytes");
        var hex = Convert.ToHexString(Convert.FromBase64String(MeshServerSession.ShortGroupId(g.Id).Replace('@', '+').Replace('$', '/')));
        Assert.Contains("MeshID=0x" + hex, System.Text.Encoding.Latin1.GetString(exe.ToArray()), StringComparison.Ordinal);

        // The Linux command points at this server and group, and its script is really there. / La orden de Linux apunta a este servidor y grupo.
        var cmd = s.LinuxInstallCommand(g);
        output.WriteLine(cmd);
        Assert.Contains($"./meshinstall.sh {s.Server.BaseUri.ToString().TrimEnd('/')} '{MeshServerSession.ShortGroupId(g.Id)}'", cmd, StringComparison.Ordinal);
        Assert.Contains("#!/", await http.GetStringAsync(new Uri(s.Server.BaseUri, "meshagents?script=1")), StringComparison.Ordinal);

        // An account without rights on the group gets neither. / Una cuenta sin permisos en el grupo no recibe ninguno.
        await using var u = await As("user", "User-Pass-1234");
        Assert.False(u.GroupAbilitiesOn(g).AddDevices);
        var e = await Assert.ThrowsAsync<MeshClientException>(() => u.DownloadAgentAsync(g, AgentPlatform.Windows64, AgentInstallMode.BackgroundOnly, Stream.Null));
        Assert.Equal("grupos/descargar/no-autorizado", e.BurburitoKey);
    }
}
