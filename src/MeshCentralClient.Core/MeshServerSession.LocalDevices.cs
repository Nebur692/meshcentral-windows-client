using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>The system of a device without an agent (MeshCentral's agent type). / El sistema de un equipo sin agente.</summary>
public enum LocalDeviceOs { Windows = 4, Linux = 6, Mac = 29 }

/// <summary>
/// Devices without an agent: a group of "local devices" keeps machines the server reaches by their address (or through
/// the agent of another device, the group's relay), for Windows Remote Desktop, SSH or any port. What MeshCentral Router
/// does with them. Measured on MeshCentral 1.3.1 (meshuser.js, meshrelay.js): createmesh with meshtype 3 (and relayid),
/// addlocaldevice with type 4, 6 or 29 (the refusal comes as changeDeviceMesh), getcookie for the node and port, then
/// localrelay.ashx?auth=, which answers "c" when the server is connected. The same as the Android app's LocalDevices.kt.
/// Equipos sin agente: un grupo de «equipos locales» guarda máquinas a las que el servidor llega por su dirección (o por
/// el agente de otro equipo, el relé del grupo). Lo que hace MeshCentral Router. Igual que LocalDevices.kt de Android.
/// </summary>
public sealed partial class MeshServerSession
{
    /// <summary>The devices last listed, by id: where a tunnel goes without asking again. / Los últimos equipos listados.</summary>
    private Dictionary<string, Device> _knownDevices = new();

    /// <summary>True for a device without an agent, by its own mark or its group's kind. / Si es un equipo sin agente.</summary>
    public bool IsLocal(Device device) => device.IsLocal || _groups.GetValueOrDefault(device.GroupId)?.Kind == DeviceGroupKind.LocalDevices;

    /// <summary>The group's relay device, if the server reaches its local devices through an agent. / El equipo relé.</summary>
    public string? RelayOf(Device device) => IsLocal(device) ? _groups.GetValueOrDefault(device.GroupId)?.RelayId : null;

    /// <summary>Who may add devices without an agent: manage devices in a group of local devices. / Quién puede añadirlos.</summary>
    public bool CanAddLocalDevice(DeviceGroup group) =>
        group.Kind == DeviceGroupKind.LocalDevices && Has(GroupRights(User, ServerInfo, group), MeshRights.ManageDevices);

    /// <summary>A name or address the server takes: 1 to 256 characters, no spaces. / Un nombre o dirección que admite el servidor.</summary>
    public static bool ValidLocalHost(string host) => host.Trim() is { Length: >= 1 and <= 256 } h && !h.Any(char.IsWhiteSpace);

    /// <summary>Creates a group of local devices and returns its id. <paramref name="relayId"/>: the device with the agent
    /// through which the server reaches them, or null for the server itself.
    /// Crea un grupo de equipos locales. relayId: el equipo con agente por el que llega el servidor, o null.</summary>
    public async Task<string> CreateLocalGroupAsync(string name, string? description = null, string? relayId = null, CancellationToken ct = default)
    {
        CheckGroupText(name, description);
        if (!CanCreateGroups) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "create device groups");
        var cmd = new JsonObject { ["action"] = "createmesh", ["meshname"] = name.Trim(), ["meshtype"] = (int)DeviceGroupKind.LocalDevices };
        if (!string.IsNullOrEmpty(description)) cmd["desc"] = description;
        if (relayId is not null) cmd["relayid"] = relayId;
        var r = await GroupRequest(cmd, "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok" || r.Str("meshid") is not { } id) throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
        await GetDeviceGroupsAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Adds a device without an agent to a group of local devices; it shows in the list at once (addnode).
    /// Añade un equipo sin agente a un grupo de equipos locales; sale en la lista al momento.</summary>
    public async Task AddLocalDeviceAsync(DeviceGroup group, string name, string host, LocalDeviceOs os, CancellationToken ct = default)
    {
        if (group.Kind != DeviceGroupKind.LocalDevices)
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-grupos/dato-invalido", "Devices without an agent only go in a group of local devices.");
        if (name.Trim().Length is 0 or > 256)
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-grupos/dato-invalido", "The name must have 1 to 256 characters.");
        if (!ValidLocalHost(host))
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-grupos/dato-invalido", $"\"{host}\" is not a name or address.");
        if (!CanAddLocalDevice(group)) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "add devices to this device group");
        var id = NewId();
        // The server answers addlocaldevice when it worked, and changeDeviceMesh with the reason when not.
        // El servidor contesta addlocaldevice si funcionó, y changeDeviceMesh con el motivo si no.
        var r = await RequestAsync(new JsonObject
        {
            ["action"] = "addlocaldevice", ["responseid"] = id, ["meshid"] = group.Id,
            ["devicename"] = name.Trim(), ["hostname"] = host.Trim(), ["type"] = (int)os,
        }, o => (o.Str("action") is "addlocaldevice" or "changeDeviceMesh") && o.Str("responseid") == id,
            "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
    }

    /// <summary>The cookie localrelay.ashx takes for one device and port, as the website asks it (getcookie). The server
    /// only answers with remote control rights; without them it says nothing, and the wait ends as "no rights".
    /// La cookie de localrelay.ashx para un equipo y un puerto. Sin control remoto el servidor calla: «sin permiso».</summary>
    internal async Task<string> LocalCookieAsync(string nodeId, int port, CancellationToken ct)
    {
        var tag = NewId();
        JsonObject r;
        try
        {
            r = await RequestAsync(new JsonObject { ["action"] = "getcookie", ["nodeid"] = nodeId, ["tcpport"] = port, ["tag"] = tag },
                o => o.Str("action") == "getcookie" && o.Str("tag") == tag, "redireccion/llevar-conexion/no-autorizado", "redireccion/llevar-conexion/conexion-cortada", ct).ConfigureAwait(false);
        }
        catch (MeshClientException e) when (e.Failure == MeshFailure.Timeout)
        {
            throw new MeshClientException(MeshFailure.Rejected, "redireccion/llevar-conexion/no-autorizado",
                "The server did not give a connection to this device: this account has no remote control on it.", e);
        }
        return r.Str("cookie") ?? throw new MeshClientException(MeshFailure.UnexpectedResponse, "redireccion/llevar-conexion/respuesta-ilegible",
            "The server answered getcookie without a cookie.");
    }

    /// <summary>The device by id, from the last list or asking again. / El equipo por su id, de la última lista o preguntando.</summary>
    internal async Task<Device?> KnownDeviceAsync(string nodeId, CancellationToken ct) =>
        _knownDevices.GetValueOrDefault(nodeId) ?? (await GetDevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Id == nodeId);
}
