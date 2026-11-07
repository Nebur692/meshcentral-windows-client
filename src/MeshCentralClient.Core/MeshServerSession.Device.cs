using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>One network interface of a device (getnetworkinfo "netif2"). / Una interfaz de red de un equipo.</summary>
public sealed record NetworkInterface(string Name, string? Mac, string? Status, long? Speed, IReadOnlyList<NetworkAddress> Addresses);

public sealed record NetworkAddress(string Address, string? Family, string? Netmask, string? Gateway);

/// <summary>An entry of a device's history (events). / Una entrada del historial de un equipo.</summary>
public sealed record DeviceEvent(DateTimeOffset? Time, string? User, string? Action, string Message);

/// <summary>The hardware and agent inventory as the agent sends it; its shape changes with the OS, so it is kept whole.
/// El inventario tal como lo manda el agente; su forma cambia con el sistema, así que se guarda entero.</summary>
public sealed record SystemInfo(JsonObject Hardware, DateTimeOffset? Time)
{
    /// <summary>Flat "section › key" pairs, for a summary or a search. / Pares «sección › clave» planos.</summary>
    public IEnumerable<(string Path, string Value)> Flatten() => Walk(Hardware, "");

    private static IEnumerable<(string, string)> Walk(JsonNode? n, string path)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o) foreach (var x in Walk(v, path.Length == 0 ? k : path + " › " + k)) yield return x;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) foreach (var x in Walk(a[i], $"{path} [{i + 1}]")) yield return x;
                break;
            case JsonValue v:
                yield return (path, v.ToString());
                break;
        }
    }

    /// <summary>
    /// The processor: "cpu_name" if the agent sent it, else the Name inside a "cpu" section (Windows). Never just any
    /// "Name": the first one found was the memory's (seen on a real Windows agent).
    /// El procesador: «cpu_name» si lo mandó el agente; si no, el Name dentro de una sección «cpu» (Windows). Nunca un
    /// «Name» cualquiera: el primero que salía era el de la memoria (visto en un agente de Windows real).
    /// </summary>
    public string? Cpu => Find("cpu_name")
        ?? Flatten().Where(p => p.Path.Split(" › ").Any(s => s.Equals("cpu", StringComparison.OrdinalIgnoreCase) || s.StartsWith("cpu [", StringComparison.OrdinalIgnoreCase)) && p.Path.EndsWith(" › Name", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value).FirstOrDefault(v => v.Length > 0);

    /// <summary>The first value whose key is one of <paramref name="keys"/>, anywhere. / El primer valor cuya clave sea una de estas, en cualquier sitio.</summary>
    public string? Find(params string[] keys) => Flatten()
        .Where(p => keys.Any(k => p.Path.EndsWith(" › " + k, StringComparison.OrdinalIgnoreCase) || p.Path.Equals(k, StringComparison.OrdinalIgnoreCase)))
        .Select(p => p.Value).FirstOrDefault(v => v.Length > 0);
}

public sealed partial class MeshServerSession
{
    private static MeshClientException NoAccess(string what) =>
        new(MeshFailure.NotFound, "ficha/abrir-detalle/no-encontrado", $"The {what} of this device is not available: it does not exist, the account has no rights, or the agent has not sent it yet.");

    /// <summary>Null when the agent never sent it. / Nulo si el agente no lo ha mandado nunca.</summary>
    public async Task<SystemInfo?> GetSystemInfoAsync(string nodeId, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "getsysinfo", ["nodeid"] = nodeId, ["nodeinfo"] = true },
            o => o.Str("action") == "getsysinfo" && o.Str("nodeid") == nodeId, "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Bool("noinfo") && r.Str("result") is "Invalid device id") throw NoAccess("inventory");
        if (r["hardware"] is not JsonObject hw) return null;
        return new SystemInfo((JsonObject)hw.DeepClone(), FromMs(r.Long("time")));
    }

    public async Task<(DateTimeOffset? Time, string? Address)> GetLastConnectAsync(string nodeId, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "lastconnect", ["nodeid"] = nodeId },
            o => o.Str("action") == "lastconnect" && o.Str("nodeid") == nodeId, "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Bool("noinfo")) return (null, null);
        return (FromMs(r.Long("time")), r.Str("addr"));
    }

    public async Task<IReadOnlyList<NetworkInterface>> GetNetworkInfoAsync(string nodeId, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "getnetworkinfo", ["nodeid"] = nodeId },
            o => o.Str("action") == "getnetworkinfo" && o.Str("nodeid") == nodeId, "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Bool("noinfo") && r.Str("result") is "Invalid device id") throw NoAccess("network information");
        if (r["netif2"] is not JsonObject nics) return Array.Empty<NetworkInterface>();
        var list = new List<NetworkInterface>();
        foreach (var (name, entries) in nics)
        {
            var e = (entries as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            list.Add(new NetworkInterface(name,
                e.Select(x => x.Str("mac")).FirstOrDefault(m => m is { Length: > 0 }),
                e.Select(x => x.Str("status")).FirstOrDefault(s => s is not null),
                e.Select(x => x.Long("speed")).FirstOrDefault(s => s is not null),
                e.Where(x => x.Str("address") is { Length: > 0 })
                 .Select(x => new NetworkAddress(x.Str("address")!, x.Str("family"), x.Str("netmask"), x.Str("gateway"))).ToList()));
        }
        return list;
    }

    /// <summary>Null when there are no notes. The server does not answer at all without access, so that also ends in a timeout.
    /// Nulo si no hay notas. Sin acceso el servidor no contesta nada, así que eso también acaba en tiempo agotado.</summary>
    public async Task<string?> GetNotesAsync(string nodeId, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "getNotes", ["id"] = nodeId },
            o => o.Str("action") == "getNotes" && o.Str("id") == nodeId, "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        return r.Str("notes");
    }

    /// <summary>Empty clears them. The server does not answer: read them back to be sure. / Vacío las borra. El servidor no contesta: se releen para estar seguros.</summary>
    public Task SetNotesAsync(string nodeId, string notes, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["action"] = "setNotes", ["id"] = nodeId, ["notes"] = notes }, ct);

    public async Task<IReadOnlyList<DeviceEvent>> GetEventsAsync(string nodeId, int limit = 200, CancellationToken ct = default)
    {
        var id = NewId();
        var r = await RequestAsync(new JsonObject { ["action"] = "events", ["nodeid"] = nodeId, ["limit"] = limit, ["tag"] = id },
            o => o.Str("action") == "events" && o.Str("tag") == id, "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        return (r["events"] as JsonArray)?.OfType<JsonObject>().Select(e => new DeviceEvent(
            e.Str("time") is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null,
            e.Str("username") ?? e.Str("userid"),
            e.Str("action"),
            e.Str("msg") ?? "")).ToList() ?? new List<DeviceEvent>();
    }

    /// <summary>Changes the name, description and/or tags (null = unchanged). / Cambia nombre, descripción o etiquetas (null = igual).</summary>
    public async Task ChangeDeviceAsync(string nodeId, string? name = null, string? description = null, IReadOnlyList<string>? tags = null, CancellationToken ct = default)
    {
        var id = NewId();
        var cmd = new JsonObject { ["action"] = "changedevice", ["nodeid"] = nodeId, ["responseid"] = id };
        if (name is not null) cmd["name"] = name;
        if (description is not null) cmd["desc"] = description;
        if (tags is not null) cmd["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
        var r = await RequestAsync(cmd, o => o.Str("action") == "changedevice" && o.Str("responseid") == id,
            "ficha/cambiar-ajustes/tiempo-agotado", "ficha/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") is { } res && res != "ok")
            throw new MeshClientException(MeshFailure.Rejected, "ficha/cambiar-ajustes/sin-permiso", $"The server refused the change ({res}).") { ServerMessage = res };
    }

    private static DateTimeOffset? FromMs(long? ms) => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;
}
