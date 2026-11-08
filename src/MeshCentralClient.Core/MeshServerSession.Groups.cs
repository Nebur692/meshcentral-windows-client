using System.Net.Http;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

public enum GroupChangeKind { Added, Changed, Removed }

public sealed class GroupChangedEventArgs(GroupChangeKind kind, string groupId, DeviceGroup? group) : EventArgs
{
    public GroupChangeKind Kind { get; } = kind;
    public string GroupId { get; } = groupId;
    /// <summary>Null when deleted. / Nulo cuando se ha borrado.</summary>
    public DeviceGroup? Group { get; } = group;
}

/// <summary>What a person can do with a device group, worked out from the rights. / Lo que puede hacer con un grupo, sacado de los permisos.</summary>
/// <param name="Edit">Name and description (editmesh). / Nombre y descripción.</param>
/// <param name="ManageUsers">Add, change and remove its users (addmeshuser / removemeshuser). / Añadir, cambiar y quitar sus usuarios.</param>
/// <param name="Delete">Only with every right on the group (deletemesh). / Solo con todos los permisos del grupo.</param>
/// <param name="AddDevices">Invitation links and agent downloads. / Enlaces de invitación y descargas del agente.</param>
/// <param name="ReceiveDevices">Devices can be moved into it (changeDeviceMesh). / Se le pueden mover equipos.</param>
public sealed record GroupAbilities(bool Edit, bool ManageUsers, bool Delete, bool AddDevices, bool ReceiveDevices);

/// <summary>Which agent to download (meshagents ids of agenttypes.js). / Qué agente descargar.</summary>
public enum AgentPlatform { Windows64 = 4, Windows32 = 3, WindowsArm64 = 43 }

/// <summary>How the agent installs (installflags / invitation flags). / Cómo se instala el agente.</summary>
public enum AgentInstallMode { BackgroundAndInteractive = 0, InteractiveOnly = 1, BackgroundOnly = 2 }

/// <summary>
/// Device groups: create, edit, delete, their users and rights, moving devices, and the ways to add new devices
/// (invitation link, agent download, Linux command). Every request is checked against the rights first, as the website
/// does: editmesh, removemeshuser and changeDeviceMesh between groups of different kinds do not answer at all when
/// refused, so sending them would end in a timeout.
/// Grupos de equipos: crear, editar, borrar, sus usuarios y permisos, mover equipos, y las formas de añadir equipos
/// nuevos (enlace de invitación, descarga del agente, orden de Linux). Cada petición se comprueba antes contra los
/// permisos, como hace la web: editmesh, removemeshuser y changeDeviceMesh entre grupos de distinto tipo no contestan
/// nada cuando se rechazan, así que mandarlos acabaría en tiempo agotado.
/// </summary>
public sealed partial class MeshServerSession
{
    private const long FullRights = 0xFFFFFFFF;

    /// <summary>The groups seen so far (after GetDeviceGroupsAsync, kept live by the events). / Los grupos vistos, al día con los avisos.</summary>
    public IReadOnlyCollection<DeviceGroup> Groups => _groups.Values;

    public DeviceGroup? GroupById(string id) => _groups.GetValueOrDefault(id);

    /// <summary>The server lets this account create device groups (siteadmin bit 64 forbids it). / El servidor deja crear grupos a esta cuenta.</summary>
    public bool CanCreateGroups => User.SiteRights == FullRights || (User.SiteRights & 64) == 0;

    public GroupAbilities GroupAbilitiesOn(DeviceGroup group) => GroupAbilitiesFor(User, ServerInfo, group);

    public static GroupAbilities GroupAbilitiesFor(UserInfo user, ServerInfo server, DeviceGroup group)
    {
        var r = GroupRights(user, server, group);
        return new(
            Edit: Has(r, MeshRights.EditGroup),
            ManageUsers: Has(r, MeshRights.ManageUsers),
            Delete: r == FullRights,
            // getMeshActions of the website: agent groups, manage devices, and siteadmin without bit 4096 ("no agent installs").
            // getMeshActions de la web: grupos de agentes, gestionar equipos y siteadmin sin el bit 4096 («sin instalar agentes»).
            AddDevices: group.Kind == DeviceGroupKind.Agents && Has(r, MeshRights.ManageDevices)
                        && (user.SiteRights == FullRights || (user.SiteRights & 4096) == 0),
            ReceiveDevices: Has(r, MeshRights.EditGroup) && Has(r, MeshRights.ManageDevices));
    }

    /// <summary>
    /// The device can be moved into <paramref name="target"/>: its own rights allow editing the group, the target takes
    /// devices, and both groups are of the same kind (otherwise the server drops the request without answering).
    /// El equipo se puede mover a <paramref name="target"/>: sus permisos dejan editar el grupo, el destino acepta equipos y
    /// los dos grupos son del mismo tipo (si no, el servidor tira la petición sin contestar).
    /// </summary>
    public bool CanMove(Device device, DeviceGroup target) => CanMove(User, ServerInfo, _groups.GetValueOrDefault(device.GroupId), device, target);

    public static bool CanMove(UserInfo user, ServerInfo server, DeviceGroup? from, Device device, DeviceGroup target) =>
        from is not null && device.GroupId == from.Id && device.GroupId != target.Id && from.Kind == target.Kind
        && Has(DeviceRights(user, server, from, device), MeshRights.EditGroup)
        && GroupAbilitiesFor(user, server, target).ReceiveDevices;

    // Keys written whole, so BurburitoTests checks every one against the catalogue. / Claves enteras, para que BurburitoTests las compruebe todas.
    private static MeshClientException GroupDenied(string key, string what) =>
        new(MeshFailure.Rejected, key, $"This account has no rights to {what}.");

    private static MeshClientException GroupRefused(string key, string? said) =>
        new(MeshFailure.Rejected, key, $"The server said: {said}.") { ServerMessage = said };

    private Task<JsonObject> GroupRequest(JsonObject cmd, string timeoutKey, string lostKey, CancellationToken ct)
    {
        var id = NewId();
        cmd["responseid"] = id;
        var verb = cmd.Str("action");
        return RequestAsync(cmd, o => o.Str("action") == verb && o.Str("responseid") == id, timeoutKey, lostKey, ct);
    }

    private static void CheckGroupText(string name, string? description)
    {
        if (name.Trim().Length is 0 or > 128 || description is { Length: > 1024 })
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-grupos/dato-invalido", "The name must have 1 to 128 characters and the description up to 1024.");
    }

    // ------------------------------------------------------------------ groups / grupos

    /// <summary>Creates a group for devices with the agent and returns its id; its creator gets every right.
    /// Crea un grupo de equipos con agente y devuelve su id; quien lo crea tiene todos los permisos.</summary>
    public async Task<string> CreateGroupAsync(string name, string? description = null, CancellationToken ct = default)
    {
        CheckGroupText(name, description);
        if (!CanCreateGroups) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "create device groups");
        var cmd = new JsonObject { ["action"] = "createmesh", ["meshname"] = name.Trim(), ["meshtype"] = (int)DeviceGroupKind.Agents };
        if (!string.IsNullOrEmpty(description)) cmd["desc"] = description;
        var r = await GroupRequest(cmd, "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok" || r.Str("meshid") is not { } id) throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
        return id;
    }

    /// <summary>Changes the name and description (null = unchanged). / Cambia nombre y descripción (null = igual).</summary>
    public async Task EditGroupAsync(DeviceGroup group, string? name, string? description, CancellationToken ct = default)
    {
        CheckGroupText(name ?? group.Name, description);
        if (!GroupAbilitiesOn(group).Edit) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "edit this device group");
        var cmd = new JsonObject { ["action"] = "editmesh", ["meshid"] = group.Id };
        if (name is not null) cmd["meshname"] = name.Trim();
        if (description is not null) cmd["desc"] = description;
        var r = await GroupRequest(cmd, "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
    }

    /// <summary>Deletes the group and, with it, its devices from the server. / Borra el grupo y, con él, sus equipos del servidor.</summary>
    public async Task DeleteGroupAsync(DeviceGroup group, CancellationToken ct = default)
    {
        if (!GroupAbilitiesOn(group).Delete) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "delete this device group");
        var r = await GroupRequest(new JsonObject { ["action"] = "deletemesh", ["meshid"] = group.Id }, "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
    }

    // ------------------------------------------------------------------ users of a group / usuarios de un grupo

    private void CheckMemberChange(DeviceGroup group, string? memberId, long? newRights)
    {
        var mine = GroupRights(User, ServerInfo, group);
        if (!Has(mine, MeshRights.ManageUsers)) throw GroupDenied("grupos/gestionar-usuarios-grupo/no-autorizado", "manage the users of this device group");
        if (memberId == User.Id) throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-usuarios-grupo/conflicto-estado", "An account cannot change its own rights.");
        // Only a group administrator touches another administrator or makes one (the server ignores it otherwise).
        // Solo un administrador del grupo toca a otro administrador o nombra uno (si no, el servidor lo ignora).
        var current = memberId is null ? 0 : group.Links.GetValueOrDefault(memberId);
        if (mine != FullRights && (current == FullRights || newRights == FullRights))
            throw GroupDenied("grupos/gestionar-usuarios-grupo/no-autorizado", "change a group administrator");
    }

    /// <summary>Gives accounts, by their user name, these rights on the group. Returns what the server says.
    /// Da a unas cuentas, por su nombre de usuario, estos permisos en el grupo. Devuelve lo que dice el servidor.</summary>
    public Task<string> AddMembersAsync(DeviceGroup group, IReadOnlyList<string> userNames, long rights, CancellationToken ct = default)
    {
        var names = userNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-usuarios-grupo/falta-dato", "No user name was given.");
        if (names.Any(n => n.Equals(User.Name, StringComparison.OrdinalIgnoreCase) || ("user/" + ServerInfo.Domain + "/" + n.ToLowerInvariant()) == User.Id))
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-usuarios-grupo/conflicto-estado", "An account cannot change its own rights.");
        CheckMemberChange(group, null, rights);
        return AddMeshUser(group, new JsonObject { ["usernames"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) }, rights, names, ct);
    }

    /// <summary>Changes the rights of an account or user group already in the group. / Cambia los permisos de una cuenta o grupo de usuarios del grupo.</summary>
    public Task<string> ChangeMemberRightsAsync(DeviceGroup group, string memberId, long rights, CancellationToken ct = default)
    {
        CheckMemberChange(group, memberId, rights);
        return AddMeshUser(group, new JsonObject { ["userids"] = new JsonArray(memberId) }, rights, null, ct);
    }

    private async Task<string> AddMeshUser(DeviceGroup group, JsonObject who, long rights, IReadOnlyList<string>? names, CancellationToken ct)
    {
        who["action"] = "addmeshuser";
        who["meshid"] = group.Id;
        who["meshadmin"] = rights;
        var r = await GroupRequest(who, "grupos/gestionar-usuarios-grupo/tiempo-agotado", "grupos/gestionar-usuarios-grupo/conexion-cortada", ct).ConfigureAwait(false);
        var said = r.Str("result") ?? "";
        // By user name, the server silently drops the names it does not know (measured on 1.3.1: one unknown name alone
        // answers "Nothing done"; next to a known one, it is just left out). So what was added is compared with what was asked.
        // Por nombre, el servidor tira sin decir nada los que no conoce (medido en 1.3.1: uno desconocido solo contesta
        // «Nothing done»; junto a uno conocido, simplemente no sale). Por eso se compara lo añadido con lo pedido.
        if (names is not null && r.Long("failed") is null or 0)
        {
            var added = said.Split(", ").Where(m => m.StartsWith("Added user ", StringComparison.Ordinal)).Select(m => m["Added user ".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = names.Where(n => !added.Contains(n)).ToList();
            if (missing.Count > 0)
                throw new MeshClientException(MeshFailure.NotFound, "grupos/gestionar-usuarios-grupo/no-encontrado",
                    $"The server does not know {string.Join(", ", missing)}" + (added.Count > 0 ? $"; added: {string.Join(", ", added)}." : ".")) { ServerMessage = said };
        }
        // Refusals come alone ("Permission denied", "Can't change self"…). / Los rechazos llegan solos.
        if (r.Long("failed") is > 0 || r.Long("success") is null or 0) throw GroupRefused("grupos/gestionar-usuarios-grupo/rechazo-remoto", said);
        return said;
    }

    /// <summary>Takes an account or user group out of the group. / Saca del grupo una cuenta o grupo de usuarios.</summary>
    public async Task RemoveMemberAsync(DeviceGroup group, string memberId, CancellationToken ct = default)
    {
        CheckMemberChange(group, memberId, null);
        var r = await GroupRequest(new JsonObject { ["action"] = "removemeshuser", ["meshid"] = group.Id, ["userid"] = memberId }, "grupos/gestionar-usuarios-grupo/tiempo-agotado", "grupos/gestionar-usuarios-grupo/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw GroupRefused("grupos/gestionar-usuarios-grupo/rechazo-remoto", r.Str("result"));
    }

    // ------------------------------------------------------------------ moving devices / mover equipos

    /// <summary>Moves devices to another group, one request each: the server answers once per device.
    /// Mueve equipos a otro grupo, una petición por equipo: el servidor contesta una vez por equipo.</summary>
    public async Task MoveDevicesAsync(IReadOnlyList<Device> devices, DeviceGroup target, CancellationToken ct = default)
    {
        foreach (var d in devices)
            if (!CanMove(d, target))
                throw _groups.GetValueOrDefault(d.GroupId) is { } from && from.Kind != target.Kind
                    ? new MeshClientException(MeshFailure.Rejected, "grupos/mover-equipos/conflicto-estado", "Devices only move between groups of the same kind.")
                    : GroupDenied("grupos/mover-equipos/no-autorizado", "move this device to that group");
        foreach (var d in devices)
        {
            var r = await GroupRequest(new JsonObject { ["action"] = "changeDeviceMesh", ["nodeids"] = new JsonArray(d.Id), ["meshid"] = target.Id }, "grupos/mover-equipos/tiempo-agotado", "grupos/mover-equipos/conexion-cortada", ct).ConfigureAwait(false);
            if (r.Str("result") is not ("ok" or "Device already in correct group")) throw GroupRefused("grupos/mover-equipos/rechazo-remoto", r.Str("result"));
        }
    }

    // ------------------------------------------------------------------ adding devices / añadir equipos

    /// <summary>The group id the agent links use: the last part of "mesh/domain/id". / El id que usan los enlaces del agente.</summary>
    public static string ShortGroupId(string groupId) => groupId.Split('/').Last();

    /// <summary>
    /// A public link to the installation page of this group, valid for <paramref name="hours"/> (0 = never expires). Whoever
    /// opens it needs no account.
    /// Un enlace público a la página de instalación de este grupo, válido <paramref name="hours"/> horas (0 = no caduca).
    /// Quien lo abre no necesita cuenta.
    /// </summary>
    public async Task<string> CreateInviteLinkAsync(DeviceGroup group, int hours, AgentInstallMode mode = AgentInstallMode.BackgroundAndInteractive, CancellationToken ct = default)
    {
        if (hours is < 0 or > 99999) throw new MeshClientException(MeshFailure.Rejected, "grupos/invitar-agente/dato-invalido", "The expiry must be 0 to 99999 hours.");
        if (!GroupAbilitiesOn(group).AddDevices) throw GroupDenied("grupos/invitar-agente/no-autorizado", "add devices to this device group");
        var r = await GroupRequest(new JsonObject
        {
            ["action"] = "createInviteLink", ["meshid"] = group.Id, ["expire"] = hours, ["flags"] = (int)mode, ["agents"] = 0,
        }, "grupos/invitar-agente/tiempo-agotado", "grupos/invitar-agente/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("url") is not { Length: > 0 } url) throw GroupRefused("grupos/invitar-agente/rechazo-remoto", r.Str("result"));
        // On a server without a domain name the link comes as a path. / En un servidor sin nombre de dominio llega como ruta.
        return url.StartsWith('/') ? new Uri(Server.BaseUri, url.TrimStart('/')).ToString() : url;
    }

    /// <summary>The download of the Windows agent with this group built in (what the website's "Add Agent" gives).
    /// La descarga del agente de Windows con este grupo dentro (lo que da «Add Agent» en la web).</summary>
    public Uri AgentDownloadUri(DeviceGroup group, AgentPlatform platform, AgentInstallMode mode = AgentInstallMode.BackgroundAndInteractive) =>
        new(Server.BaseUri, $"meshagents?id={(int)platform}&meshid={ShortGroupId(group.Id)}&installflags={(int)mode}");

    /// <summary>The website's install command for Linux, BSD and macOS (meshinstall.sh). / La orden de instalación de la web para Linux, BSD y macOS.</summary>
    public string LinuxInstallCommand(DeviceGroup group)
    {
        var server = Server.BaseUri.ToString().TrimEnd('/');
        var script = new Uri(Server.BaseUri, "meshagents?script=1").ToString();
        // Feature 0x80000000: the server's certificate is not a public one. / El certificado del servidor no es público.
        var noCheck = (ServerInfo.Features & 0x80000000) != 0 ? " --no-check-certificate" : "";
        var id = ShortGroupId(group.Id);
        return $"(wget \"{script}\"{noCheck} -O ./meshinstall.sh || wget \"{script}\" --no-proxy{noCheck} -O ./meshinstall.sh) && chmod 755 ./meshinstall.sh && sudo -E ./meshinstall.sh {server} '{id}' || ./meshinstall.sh {server} '{id}'";
    }

    /// <summary>Downloads the Windows agent of the group into <paramref name="destination"/>. Returns the bytes written.
    /// Descarga el agente de Windows del grupo en <paramref name="destination"/>. Devuelve los bytes escritos.</summary>
    public async Task<long> DownloadAgentAsync(DeviceGroup group, AgentPlatform platform, AgentInstallMode mode, Stream destination, CancellationToken ct = default)
    {
        if (!GroupAbilitiesOn(group).AddDevices) throw GroupDenied("grupos/descargar/no-autorizado", "add devices to this device group");
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => Trust.Validate(m, c, ch, e) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCentralWindowsClient/" + typeof(MeshServerSession).Assembly.GetName().Version?.ToString(3));
        HttpResponseMessage res;
        try { res = await http.GetAsync(AgentDownloadUri(group, platform, mode), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MeshClientException(MeshFailure.ServerUnreachable, "grupos/descargar/sin-conexion", "The agent could not be downloaded from the server.", ex);
        }
        using (res)
        {
            // 401: the server only lets signed-in browsers download agents (lockagentdownload). / Solo deja bajarlo a navegadores con sesión.
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new MeshClientException(MeshFailure.HttpError, "grupos/descargar/no-autorizado", "The server only gives agents to signed-in browsers.") { HttpStatus = 401 };
            if (!res.IsSuccessStatusCode)
                throw new MeshClientException(MeshFailure.HttpError, "grupos/descargar/error-remoto", $"The server answered HTTP {(int)res.StatusCode}.") { HttpStatus = (int)res.StatusCode };
            await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var head = new byte[2];
            var got = await body.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            // A Windows program starts with "MZ"; anything else is an error page. / Un programa de Windows empieza por «MZ».
            if (got < 2 || head[0] != 'M' || head[1] != 'Z')
                throw new MeshClientException(MeshFailure.UnexpectedResponse, "grupos/descargar/respuesta-ilegible", "The server did not send a Windows program.");
            await destination.WriteAsync(head, ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 2;
            int n;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
            }
            return total;
        }
    }

    // ------------------------------------------------------------------ live events / avisos en vivo

    /// <summary>Group and own-account events: keeps the groups and the rights up to date. True when handled.
    /// Avisos de grupos y de la propia cuenta: mantiene al día grupos y permisos. True si lo ha tratado.</summary>
    private bool OnGroupOrAccountEvent(JsonObject e)
    {
        GroupChangedEventArgs? args = null;
        switch (e.Str("action"))
        {
            case "createmesh" when e["mesh"] is JsonObject m:
                var created = DeviceGroup.From(m);
                _groups = new Dictionary<string, DeviceGroup>(_groups) { [created.Id] = created };
                args = new(GroupChangeKind.Added, created.Id, created);
                break;
            case "meshchange" when e.Str("meshid") is { } id:
                // It carries name, desc, mtype and links, not the whole group. / Trae nombre, descripción, tipo y enlaces, no el grupo entero.
                var changed = DeviceGroup.From(e);
                if (_groups.GetValueOrDefault(id) is { } old && e["links"] is null) changed = changed with { Links = old.Links, Members = old.Members };
                // Losing every right on it hides it, as on the website. / Si se pierden todos los permisos, desaparece, como en la web.
                var visible = GroupRights(User, ServerInfo, changed) != 0 || ServerInfo.ManageAllDeviceGroups;
                var groups = new Dictionary<string, DeviceGroup>(_groups);
                if (visible) groups[id] = changed; else groups.Remove(id);
                _groups = groups;
                args = new(visible ? GroupChangeKind.Changed : GroupChangeKind.Removed, id, visible ? changed : null);
                break;
            case "deletemesh" when e.Str("meshid") is { } id:
                var rest = new Dictionary<string, DeviceGroup>(_groups);
                rest.Remove(id);
                _groups = rest;
                args = new(GroupChangeKind.Removed, id, null);
                break;
            case "accountchange" when e["account"] is JsonObject a && a.Str("_id") == User.Id:
                User = UserInfo.From(a);
                return true;
            default:
                return false;
        }
        try { GroupChanged?.Invoke(this, args); } catch (Exception) { /* a subscriber must not kill the session / un suscriptor no puede tumbar la sesión */ }
        return true;
    }
}
