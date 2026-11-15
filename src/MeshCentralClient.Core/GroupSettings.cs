using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>
/// What a device group asks of the person at the device and how it keeps its devices, as the website's "User Consent"
/// and "Features" dialogs edit them (measured on MeshCentral 1.3.1: default.handlebars p20editmeshconsent and
/// p20editmeshfeatures, meshuser.js "editmesh"). <see cref="Consent"/>: bits of <see cref="GroupConsent"/>;
/// <see cref="Flags"/>: bits of <see cref="GroupFeature"/>; <see cref="RemoveInactiveDays"/>: 0 = never, 1 to 2000.
/// Lo que un grupo pide a la persona del equipo y cómo guarda sus equipos, como los editan los diálogos «Consentimiento»
/// y «Funciones» de la web (medido en 1.3.1). 0 días = nunca, de 1 a 2000.
/// </summary>
public sealed record GroupSettings(int Consent = 0, int Flags = 0, int RemoveInactiveDays = 0)
{
    public const int MaxDays = 2000;

    public bool HasConsent(GroupConsent c) => (Consent & (int)c) != 0;
    public bool HasFeature(GroupFeature f) => (Flags & (int)f) != 0;

    /// <summary>
    /// What the website would save from these choices: the name sub-options fall without name sync, removing inactive
    /// devices does not go with removing them on disconnect, and only kinds that have them keep those bits.
    /// Lo que guardaría la web: las opciones del nombre caen sin sincronizar, borrar inactivos no va con borrar al
    /// desconectarse, y solo los tipos que las tienen conservan esas marcas.
    /// </summary>
    public static GroupSettings Normalized(GroupSettings s, DeviceGroupKind kind, ServerInfo server)
    {
        var f = s.Flags;
        var agentLike = kind is DeviceGroupKind.Agents or DeviceGroupKind.IpKvm;
        if (!agentLike) f &= ~(int)GroupFeature.RemoveOnDisconnect;
        if ((f & (int)GroupFeature.SyncName) == 0) f &= ~(int)(GroupFeature.PreferAgentName | GroupFeature.AllowOverride);
        if (!server.GroupsChooseRecording) f &= ~(int)GroupFeature.Record;
        var days = (f & (int)GroupFeature.RemoveOnDisconnect) != 0 ? 0 : s.RemoveInactiveDays;
        return new GroupSettings(s.Consent | server.ForcedConsent, f, days);
    }
}

/// <summary>Consent bits of a group (meshuser.js "consent"). / Marcas de consentimiento de un grupo.</summary>
[Flags]
public enum GroupConsent
{
    None = 0,
    DesktopNotify = 0x1, TerminalNotify = 0x2, FilesNotify = 0x4,
    DesktopPrompt = 0x8, TerminalPrompt = 0x10, FilesPrompt = 0x20,
    DesktopToolbar = 0x40, RegistryNotify = 0x80, RegistryPrompt = 0x100,
    All = 0x1FF,
}

/// <summary>Feature bits of a group (meshuser.js "flags"). / Marcas de funciones de un grupo.</summary>
[Flags]
public enum GroupFeature
{
    None = 0,
    /// <summary>Remove the device when it disconnects. / Borrar el equipo al desconectarse.</summary>
    RemoveOnDisconnect = 0x1,
    /// <summary>Keep the device's name equal to its host name. / Igualar el nombre al del host.</summary>
    SyncName = 0x2,
    /// <summary>Record sessions (when the server lets groups choose). / Grabar sesiones (si el servidor deja elegir).</summary>
    Record = 0x4,
    PreferAgentName = 0x8,
    AllowOverride = 0x10,
}

public sealed partial class MeshServerSession
{
    /// <summary>
    /// Saves a group's consent and features, as the website does; the server changes only what differs and answers
    /// "ok" either way. Returns what was sent (consent the server forces stays on).
    /// Guarda el consentimiento y las funciones de un grupo, como la web. Devuelve lo que se mandó.
    /// </summary>
    public async Task<GroupSettings> SaveGroupSettingsAsync(DeviceGroup group, GroupSettings wanted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(wanted);
        if (!GroupAbilitiesOn(group).Edit) throw GroupDenied("grupos/gestionar-grupos/no-autorizado", "edit this device group");
        if (wanted.RemoveInactiveDays is < 0 or > GroupSettings.MaxDays || wanted.Consent is < 0 or > (int)GroupConsent.All)
            throw new MeshClientException(MeshFailure.Rejected, "grupos/gestionar-grupos/dato-invalido", $"Inactive devices are removed after 1 to {GroupSettings.MaxDays} days.");
        var s = GroupSettings.Normalized(wanted, group.Kind, ServerInfo);
        var cmd = new JsonObject
        {
            ["action"] = "editmesh", ["meshid"] = group.Id, ["consent"] = s.Consent, ["flags"] = s.Flags, ["expireDevs"] = s.RemoveInactiveDays,
        };
        var r = await GroupRequest(cmd, "grupos/gestionar-grupos/tiempo-agotado", "grupos/gestionar-grupos/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw GroupRefused("grupos/gestionar-grupos/rechazo-remoto", r.Str("result"));
        return s;
    }
}
