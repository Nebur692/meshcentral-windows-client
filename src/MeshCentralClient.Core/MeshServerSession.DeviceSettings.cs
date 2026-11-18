using System.Globalization;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>The device's icon, as the website's eight (1 desktop … 8 virtual machine). / El icono del equipo, los ocho de la web.</summary>
public enum DeviceIcon { Desktop = 1, Laptop = 2, Phone = 3, Server = 4, Storage = 5, Network = 6, Board = 7, VirtualMachine = 8 }

/// <summary>What the website keeps on a device's page besides its name: icon, own ports and place on the map. Ports null
/// = the usual one. / Lo que guarda la web en la ficha además del nombre: icono, puertos propios y sitio en el mapa.</summary>
public sealed record DeviceSettings(DeviceIcon Icon, int? RdpPort, int? SshPort, int? VncPort, int? HttpPort, int? HttpsPort, DeviceLocation? Location)
{
    public static DeviceSettings Of(Device d) =>
        new(Enum.IsDefined((DeviceIcon)d.Icon) ? (DeviceIcon)d.Icon : DeviceIcon.Desktop, d.RdpPort, d.SshPort, d.VncPort, d.HttpPort, d.HttpsPort, d.Location);
}

/// <summary>
/// The device's icon, own ports and place on the map, as the website's device page (meshuser.js changedevice). Sending
/// the usual port (3389, 22, 5900, 80, 443) removes the own one, as the server does. The place is kept only on servers
/// with geolocation on (feature 0x8000); elsewhere the server drops it without a word, so the app does not offer it.
/// El icono, los puertos propios y el sitio en el mapa, como la ficha de la web. Mandar el puerto normal quita el propio.
/// El sitio solo se guarda en servidores con la geolocalización encendida; si no, el servidor lo tira sin decir nada.
/// </summary>
public sealed partial class MeshServerSession
{
    public bool CanChangeDeviceSettings(Device device) => Has(RightsOn(device), MeshRights.ManageDevices);

    /// <summary>The server keeps places on the map (geolocation on). / El servidor guarda sitios en el mapa.</summary>
    public bool ServerKeepsLocations => (ServerInfo.Features & 0x8000) != 0;

    public async Task SaveDeviceSettingsAsync(Device device, DeviceSettings wanted, CancellationToken ct = default)
    {
        if (!CanChangeDeviceSettings(device)) throw Denied("ficha/cambiar-ajustes/sin-permiso", "change it");
        int?[] ports = [wanted.RdpPort, wanted.SshPort, wanted.VncPort, wanted.HttpPort, wanted.HttpsPort];
        if (ports.Any(p => p is not null and (< 1 or > 65535)) || !Enum.IsDefined(wanted.Icon) ||
            wanted.Location is { } l && (l.Latitude is < -90 or > 90 || l.Longitude is < -180 or > 180))
            throw new MeshClientException(MeshFailure.Rejected, "ficha/cambiar-ajustes/dato-invalido", "Ports go from 1 to 65535, latitude from -90 to 90 and longitude from -180 to 180.");
        var now = DeviceSettings.Of(device);
        var id = NewId();
        var cmd = new JsonObject { ["action"] = "changedevice", ["nodeid"] = device.Id, ["responseid"] = id };
        if (wanted.Icon != now.Icon) cmd["icon"] = (int)wanted.Icon;
        void Port(string field, int? want, int? have, int usual) { if (want != have) cmd[field] = want ?? usual; }
        Port("rdpport", wanted.RdpPort, now.RdpPort, 3389);
        Port("sshport", wanted.SshPort, now.SshPort, 22);
        Port("rfbport", wanted.VncPort, now.VncPort, 5900);
        Port("httpport", wanted.HttpPort, now.HttpPort, 80);
        Port("httpsport", wanted.HttpsPort, now.HttpsPort, 443);
        if (ServerKeepsLocations && wanted.Location != now.Location)
            cmd["userloc"] = wanted.Location is { } w
                ? new JsonArray(w.Latitude.ToString("0.######", CultureInfo.InvariantCulture), w.Longitude.ToString("0.######", CultureInfo.InvariantCulture))
                : new JsonArray();
        if (cmd.Count == 3) return; // nothing changes / no cambia nada
        var r = await RequestAsync(cmd, o => o.Str("action") == "changedevice" && o.Str("responseid") == id,
            "ficha/cambiar-ajustes/tiempo-agotado", "ficha/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") is { } res && res != "ok")
            throw new MeshClientException(MeshFailure.Rejected, "ficha/cambiar-ajustes/rechazo-remoto", $"The server refused the change ({res}).") { ServerMessage = res };
    }
}
