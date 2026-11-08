using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

public sealed record ServerInfo(string Name, string Domain, string? Version)
{
    internal static ServerInfo From(JsonObject o) => new(
        o.Str("name") ?? "", o.Str("domain") ?? "", o.Str("serverVersion") ?? o.Str("version")) { ManageAllDeviceGroups = o.Bool("manageAllDeviceGroups"), Features = o.Long("features") ?? 0 };

    /// <summary>The server's feature bits (serverinfo.features), as the website reads them. / Los bits de funciones del servidor.</summary>
    public long Features { get; init; }

    /// <summary>The server lets this account manage every device group (managealldevicegroups). / El servidor deja a esta cuenta gestionar todos los grupos.</summary>
    public bool ManageAllDeviceGroups { get; init; }
}

public sealed record UserInfo(string Id, string Name, bool IsFullAdministrator)
{
    internal static UserInfo From(JsonObject o) => new(
        o.Str("_id") ?? "", o.Str("name") ?? o.Str("_id") ?? "", o.Long("siteadmin") == 0xFFFFFFFF)
    {
        Links = JsonExtensions.Links(o), RemoveRights = o.Long("removeRights"), SiteRights = o.Long("siteadmin") ?? 0,
        Email = o.Str("email"), RealName = o.Str("realname"),
        Created = Seconds(o.Long("creation")), LastLogin = Seconds(o.Long("login")),
        MustChangePassword = o.Long("passchange") == -1,
        // The server sends a flag or a count instead of each secret (CloneSafeUser). / El servidor manda una marca o un número en vez de cada secreto.
        HasAuthenticator = o.Long("otpsecret") is > 0, HasBackupCodes = o.Long("otpkeys") is > 0, SecurityKeys = (int)(o.Long("otphkeys") ?? 0),
        HasOtherTwoFactor = o.Long("otpekey") is > 0 || o.Long("otpduo") is > 0 || o.Long("otpdev") is > 0 || o.Str("phone") is { Length: > 0 },
    };

    private static DateTimeOffset? Seconds(long? s) => s is > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.Value) : null;

    public string? Email { get; init; }
    public string? RealName { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? LastLogin { get; init; }
    /// <summary>The account must choose a new password at its next sign-in. / La cuenta tiene que poner contraseña nueva al entrar.</summary>
    public bool MustChangePassword { get; init; }
    public bool HasAuthenticator { get; init; }
    /// <summary>Some backup code is still unused. The server sends only that, not how many (CloneSafeUser sets otpkeys to 1).
    /// Queda algún código de respaldo sin usar. El servidor manda solo eso, no cuántos (CloneSafeUser pone otpkeys a 1).</summary>
    public bool HasBackupCodes { get; init; }
    public int SecurityKeys { get; init; }
    /// <summary>Email, SMS, push or Duo codes. / Códigos por correo, SMS, aviso o Duo.</summary>
    public bool HasOtherTwoFactor { get; init; }
    public bool HasTwoFactor => HasAuthenticator || SecurityKeys > 0 || HasOtherTwoFactor;
    /// <summary>Locked accounts cannot sign in (siteadmin bit 32; never a full administrator). / Las cuentas bloqueadas no pueden entrar.</summary>
    public bool IsLocked => !IsFullAdministrator && (SiteRights & (long)SiteRight.Locked) != 0;
    /// <summary>The user groups this account belongs to. / Los grupos de usuarios a los que pertenece.</summary>
    public IEnumerable<string> UserGroupIds => Links.Keys.Where(k => k.StartsWith("ugrp/", StringComparison.Ordinal));

    /// <summary>Server-wide rights (siteadmin): 0xFFFFFFFF is a full administrator; bit 64 forbids new device groups, bit 4096 forbids installing agents.
    /// Permisos en todo el servidor (siteadmin): 0xFFFFFFFF es administrador completo; el bit 64 prohíbe grupos nuevos y el 4096 instalar agentes.</summary>
    public long SiteRights { get; init; }

    /// <summary>What the account is linked to (groups, devices, user groups "ugrp/…") and with which rights. / A qué está enlazada la cuenta y con qué permisos.</summary>
    public IReadOnlyDictionary<string, long> Links { get; init; } = JsonExtensions.NoLinks;

    /// <summary>Rights taken away from this account everywhere (removeRights). / Permisos quitados a la cuenta en todas partes.</summary>
    public long? RemoveRights { get; init; }
}

/// <summary>Kind of device group (MeshCentral "mtype"). / Tipo de grupo de equipos («mtype» de MeshCentral).</summary>
public enum DeviceGroupKind { Unknown = 0, IntelAmtOnly = 1, Agents = 2, LocalDevices = 3, IpKvm = 4 }

public sealed record DeviceGroup(string Id, string Name, string? Description, DeviceGroupKind Kind)
{
    internal static DeviceGroup From(JsonObject o) => new(
        o.Str("_id") ?? o.Str("meshid") ?? "", o.Str("name") ?? "", o.Str("desc"), (DeviceGroupKind)(o.Long("mtype") ?? 0))
    { Links = JsonExtensions.Links(o), Members = GroupMember.From(o) };

    /// <summary>Who has rights on the group: accounts and user groups. / Quién tiene permisos en el grupo: cuentas y grupos de usuarios.</summary>
    public IReadOnlyDictionary<string, long> Links { get; init; } = JsonExtensions.NoLinks;

    /// <summary>The same links with the names the server keeps in them. / Los mismos enlaces con los nombres que guarda el servidor.</summary>
    public IReadOnlyList<GroupMember> Members { get; init; } = Array.Empty<GroupMember>();
}

/// <summary>An account ("user/…") or user group ("ugrp/…") with rights on a device group. / Una cuenta o grupo de usuarios con permisos en un grupo de equipos.</summary>
public sealed record GroupMember(string Id, string Name, long Rights)
{
    public bool IsUserGroup => Id.StartsWith("ugrp/", StringComparison.Ordinal);

    internal static IReadOnlyList<GroupMember> From(JsonObject o)
    {
        if (o["links"] is not JsonObject links) return Array.Empty<GroupMember>();
        var list = new List<GroupMember>();
        foreach (var (id, v) in links)
            if (v is JsonObject l && l.Long("rights") is long r)
                list.Add(new GroupMember(id, l.Str("name") ?? id.Split('/').Last(), r));
        return list;
    }
}

/// <summary>Connection flags of a device (MeshCentral "conn"). / Formas en que está conectado un equipo («conn»).</summary>
[Flags]
public enum DeviceConnectivity { None = 0, Agent = 1, IntelAmtCira = 2, IntelAmtLocal = 4, Relay = 8, Mqtt = 16 }

/// <summary>What the agent can do (MeshCentral "agent.caps"). / Lo que sabe hacer el agente («agent.caps»).</summary>
[Flags]
public enum AgentCapabilities { None = 0, Desktop = 1, Terminal = 2, Files = 4, Console = 8, JavaScript = 16 }

public sealed record Device(
    string Id,
    string GroupId,
    string Name,
    string? OsDescription,
    string? Address,
    int Icon,
    DeviceConnectivity Connectivity,
    int PowerState,
    AgentCapabilities Capabilities,
    IReadOnlyList<string> Tags,
    int AgentId = 0,
    string? Description = null,
    DateTimeOffset? LastBoot = null,
    string? AgentCore = null)
{
    /// <summary>Rights given on this device alone. / Permisos dados solo en este equipo.</summary>
    public IReadOnlyDictionary<string, long> Links { get; init; } = JsonExtensions.NoLinks;

    public bool IsOnline => Connectivity != DeviceConnectivity.None;

    /// <summary>The agent is connected (power, messages and commands need it). / El agente está conectado (lo necesitan energía, avisos y órdenes).</summary>
    public bool HasAgent => Connectivity.HasFlag(DeviceConnectivity.Agent);

    /// <summary>Windows agents (agenttypes.js, platform win32). / Agentes de Windows (agenttypes.js, plataforma win32).</summary>
    public bool IsWindows => AgentId is 1 or 2 or 3 or 4 or 21 or 22 or 42 or 43 or 10003 or 10004;

    public bool CanOpenTerminal => IsOnline && Connectivity.HasFlag(DeviceConnectivity.Agent) && Capabilities.HasFlag(AgentCapabilities.Terminal);

    public bool CanOpenDesktop => IsOnline && Connectivity.HasFlag(DeviceConnectivity.Agent) && Capabilities.HasFlag(AgentCapabilities.Desktop);

    public bool CanOpenFiles => IsOnline && Connectivity.HasFlag(DeviceConnectivity.Agent) && Capabilities.HasFlag(AgentCapabilities.Files);

    internal static Device From(JsonObject o, string? groupId = null)
    {
        var tags = (o["tags"] as JsonArray)?.Select(t => t?.ToString()).Where(t => !string.IsNullOrEmpty(t)).Cast<string>().ToList()
                   ?? new List<string>();
        var agent = o["agent"] as JsonObject;
        return new Device(
            o.Str("_id") ?? "",
            groupId ?? o.Str("meshid") ?? "",
            o.Str("name") ?? o.Str("rname") ?? "",
            o.Str("osdesc"),
            o.Str("ip") ?? o.Str("host"),
            (int)(o.Long("icon") ?? 1),
            (DeviceConnectivity)(o.Long("conn") ?? 0),
            (int)(o.Long("pwr") ?? 0),
            (AgentCapabilities)(agent?.Long("caps") ?? 0),
            tags,
            (int)(agent?.Long("id") ?? 0),
            o.Str("desc"),
            o.Long("lastbootuptime") is long boot and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(boot) : null,
            agent?.Str("core")) { Links = JsonExtensions.Links(o) };
    }

    /// <summary>Applies a "changenode" update, which does not carry connection state.
    /// Aplica un cambio «changenode», que no trae el estado de conexión.</summary>
    public Device WithDetails(Device changed) => changed with { Connectivity = Connectivity, PowerState = PowerState };
}

public sealed record RelayCookies(string Cookie, string RelayCookie);

public enum DeviceChangeKind { Added, Changed, Removed, ConnectionChanged }

public sealed class DeviceChangedEventArgs(DeviceChangeKind kind, string deviceId, Device? device) : EventArgs
{
    public DeviceChangeKind Kind { get; } = kind;
    public string DeviceId { get; } = deviceId;
    /// <summary>Null when removed. / Nulo cuando se ha quitado.</summary>
    public Device? Device { get; } = device;
}

internal static class JsonExtensions
{
    public static string? Str(this JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Long(this JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    public static bool Bool(this JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static readonly IReadOnlyDictionary<string, long> NoLinks = new Dictionary<string, long>();

    /// <summary>"links": { id: { rights } } → id → rights. / «links»: { id: { rights } } → id → permisos.</summary>
    public static IReadOnlyDictionary<string, long> Links(JsonObject o)
    {
        if (o["links"] is not JsonObject links) return NoLinks;
        var map = new Dictionary<string, long>();
        foreach (var (id, v) in links) if (v is JsonObject l && l.Long("rights") is long r) map[id] = r;
        return map;
    }
}
