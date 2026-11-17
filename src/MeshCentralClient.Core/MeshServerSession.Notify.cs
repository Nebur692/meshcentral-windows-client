using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>When to be told about a device: by email or by messaging (ntfy, Telegram…), as it connects or disconnects.
/// Cuándo avisar de un equipo: por correo o por mensajería, al conectarse o desconectarse.</summary>
[Flags]
public enum DeviceNotify
{
    None = 0, EmailConnect = 16, EmailDisconnect = 32, MessageConnect = 128, MessageDisconnect = 256,
    /// <summary>The bits the apps change; the rest (the website's, help requests) are kept. / Las que cambian las apps.</summary>
    Ours = EmailConnect | EmailDisconnect | MessageConnect | MessageDisconnect,
}

/// <summary>
/// The account's notifications about a device group or a device, as the website's bell (meshuser.js changemeshnotify and
/// changeusernotify), confirmed by the account's event (73 and 130). The server sends nothing when nothing changes.
/// Los avisos de la cuenta sobre un grupo o un equipo, como la campana de la web; los confirma el aviso de la cuenta.
/// </summary>
public sealed partial class MeshServerSession
{
    public DeviceNotify NotifyOn(string groupOrDeviceId) => (DeviceNotify)User.Notify.GetValueOrDefault(groupOrDeviceId);

    public Task SetGroupNotifyAsync(DeviceGroup group, DeviceNotify wanted, CancellationToken ct = default) =>
        SetNotifyAsync("changemeshnotify", "meshid", group.Id, 73, wanted, ct);

    public Task SetDeviceNotifyAsync(Device device, DeviceNotify wanted, CancellationToken ct = default) =>
        SetNotifyAsync("changeusernotify", "nodeid", device.Id, 130, wanted, ct);

    private async Task SetNotifyAsync(string action, string field, string id, long msgid, DeviceNotify wanted, CancellationToken ct)
    {
        CheckOwnSettings();
        var flags = ((int)NotifyOn(id) & ~(int)DeviceNotify.Ours) | ((int)wanted & (int)DeviceNotify.Ours);
        if (flags == (int)NotifyOn(id)) return; // nothing changes: the server would not answer / no cambia nada: no contestaría
        var m = await ConfirmedByEvent(new JsonObject { ["action"] = action, [field] = id, ["notify"] = flags }, e => OwnAccountEvent(e, msgid),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if ((m["event"] as JsonObject)?["account"] is JsonObject account) User = User with { Notify = UserInfo.From(account).Notify };
        else User = User with { Notify = new Dictionary<string, int>(User.Notify) { [id] = flags } };
    }
}
