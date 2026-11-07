using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

public sealed record ServerInfo(string Name, string Domain, string? Version)
{
    internal static ServerInfo From(JsonObject o) => new(
        o.Str("name") ?? "", o.Str("domain") ?? "", o.Str("serverVersion") ?? o.Str("version"));
}

public sealed record UserInfo(string Id, string Name, bool IsFullAdministrator)
{
    internal static UserInfo From(JsonObject o) => new(
        o.Str("_id") ?? "", o.Str("name") ?? o.Str("_id") ?? "", o.Long("siteadmin") == 0xFFFFFFFF);
}

/// <summary>Kind of device group (MeshCentral "mtype"). / Tipo de grupo de equipos («mtype» de MeshCentral).</summary>
public enum DeviceGroupKind { Unknown = 0, IntelAmtOnly = 1, Agents = 2, LocalDevices = 3, IpKvm = 4 }

public sealed record DeviceGroup(string Id, string Name, string? Description, DeviceGroupKind Kind)
{
    internal static DeviceGroup From(JsonObject o) => new(
        o.Str("_id") ?? "", o.Str("name") ?? "", o.Str("desc"), (DeviceGroupKind)(o.Long("mtype") ?? 0));
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
    int AgentId = 0)
{
    public bool IsOnline => Connectivity != DeviceConnectivity.None;

    /// <summary>Windows agents (agenttypes.js, platform win32). / Agentes de Windows (agenttypes.js, plataforma win32).</summary>
    public bool IsWindows => AgentId is 1 or 2 or 3 or 4 or 21 or 22 or 42 or 43 or 10003 or 10004;

    public bool CanOpenTerminal => IsOnline && Connectivity.HasFlag(DeviceConnectivity.Agent) && Capabilities.HasFlag(AgentCapabilities.Terminal);

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
            (int)(agent?.Long("id") ?? 0));
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
}
