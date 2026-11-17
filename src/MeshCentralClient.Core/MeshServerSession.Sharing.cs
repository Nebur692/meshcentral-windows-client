using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>What a guest may use through a share link (MeshCentral's "p" bits). / Lo que puede usar un invitado.</summary>
[Flags]
public enum ShareKinds { None = 0, Terminal = 1, Desktop = 2, Files = 4 }

/// <summary>One link that lets a guest without an account reach a device. / Un enlace que deja a un invitado sin cuenta llegar a un equipo.</summary>
/// <param name="Url">Only for the account that made it, or a server administrator (the server hides it from others). / Solo para quien lo hizo o un administrador.</param>
public sealed record DeviceShare(string PublicId, string GuestName, ShareKinds Kinds, DateTimeOffset? Start, DateTimeOffset? Expires,
    bool ViewOnly, bool AsksConsent, string? Url, string? UserId)
{
    internal static DeviceShare From(JsonObject o) => new(
        o.Str("publicid") ?? "", o.Str("guestName") ?? o.Str("guestname") ?? "", (ShareKinds)(o.Long("p") ?? 0),
        o.Long("startTime") is long s and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s) : null,
        o.Long("expireTime") is long e and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e) : null,
        o.Bool("viewOnly"), ((o.Long("consent") ?? 0) & 0x38) != 0, o.Str("url"), o.Str("userid"));
}

/// <summary>
/// Sharing a device with a guest, as the website's "Share" (meshuser.js createDeviceShareLink, deviceShares and
/// removeDeviceShare): a link to its desktop, terminal or files, for a time, optionally asking the person at the device.
/// It needs remote control and guest sharing rights on the device, without limited input, as the server checks.
/// Compartir un equipo con un invitado, como «Compartir» de la web: un enlace a su escritorio, terminal o ficheros, por un
/// tiempo, pidiendo o no permiso a quien está en el equipo. Pide control remoto y compartir con invitados, sin entrada limitada.
/// </summary>
public sealed partial class MeshServerSession
{
    public bool CanShare(Device device)
    {
        var r = RightsOn(device);
        return device.HasAgent && (r == (long)MeshRights.Full ||
            (Has(r, MeshRights.RemoteControl) && Has(r, MeshRights.GuestSharing) && !Has(r, MeshRights.LimitedInput)));
    }

    /// <summary>Makes a link for <paramref name="guest"/>, valid <paramref name="duration"/> from now.
    /// <paramref name="askConsent"/> asks the person at the device before each use. / Hace un enlace para el invitado.</summary>
    public async Task<DeviceShare> ShareDeviceAsync(Device device, string guest, ShareKinds kinds, TimeSpan duration, bool askConsent, CancellationToken ct = default)
    {
        if (!CanShare(device)) throw Denied("ficha/compartir/no-autorizado", "share it");
        var name = guest.Trim();
        if (name.Length is 0 or > 64 || kinds == ShareKinds.None || duration <= TimeSpan.Zero)
            throw new MeshClientException(MeshFailure.Rejected, "ficha/compartir/dato-invalido", "A share needs the guest's name, what it may use and a time.");
        // Consent bits: 8 desktop, 16 terminal, 32 files ask; the website also notifies (1, 2, 4). / Pedir y avisar por cada cosa.
        var consent = 0;
        if (askConsent)
        {
            if (kinds.HasFlag(ShareKinds.Desktop)) consent |= 8 | 1;
            if (kinds.HasFlag(ShareKinds.Terminal)) consent |= 16 | 2;
            if (kinds.HasFlag(ShareKinds.Files)) consent |= 32 | 4;
        }
        var id = NewId();
        var r = await RequestAsync(new JsonObject
            {
                ["action"] = "createDeviceShareLink", ["nodeid"] = device.Id, ["guestname"] = name, ["p"] = (int)kinds,
                ["expire"] = (int)Math.Ceiling(duration.TotalMinutes), ["consent"] = consent, ["responseid"] = id,
            }, o => (o.Str("action") is "createDeviceShareLink" or "deviceShares") && o.Str("responseid") == id,
            "ficha/compartir/tiempo-agotado", "ficha/compartir/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "OK" || r.Str("url") is not { Length: > 0 } url)
            throw new MeshClientException(MeshFailure.Rejected, "ficha/compartir/rechazo-remoto", $"The server did not make the link: {r.Str("result")}.") { ServerMessage = r.Str("result") };
        return new DeviceShare(r.Str("publicid") ?? "", name, kinds, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + duration, r.Bool("viewOnly"), askConsent, url, User.Id);
    }

    /// <summary>The device's links still valid (the server drops the expired). / Los enlaces del equipo que siguen valiendo.</summary>
    public async Task<IReadOnlyList<DeviceShare>> GetDeviceSharesAsync(Device device, CancellationToken ct = default)
    {
        if (!CanShare(device)) throw Denied("ficha/compartir/no-autorizado", "see its shares");
        // The answer carries no responseid: it is told by its device. / La respuesta no trae responseid: se reconoce por su equipo.
        var r = await RequestAsync(new JsonObject { ["action"] = "deviceShares", ["nodeid"] = device.Id },
            o => o.Str("action") == "deviceShares" && o.Str("nodeid") == device.Id && o["deviceShares"] is JsonArray,
            "ficha/compartir/tiempo-agotado", "ficha/compartir/conexion-cortada", ct).ConfigureAwait(false);
        return ((JsonArray)r["deviceShares"]!).OfType<JsonObject>().Select(DeviceShare.From).ToList();
    }

    /// <summary>Ends a link: the guest can no longer use it. / Acaba un enlace: el invitado ya no puede usarlo.</summary>
    public async Task RemoveDeviceShareAsync(Device device, DeviceShare share, CancellationToken ct = default)
    {
        if (!CanShare(device)) throw Denied("ficha/compartir/no-autorizado", "remove its shares");
        var id = NewId();
        var r = await RequestAsync(new JsonObject { ["action"] = "removeDeviceShare", ["nodeid"] = device.Id, ["publicid"] = share.PublicId, ["responseid"] = id },
            o => o.Str("action") == "removeDeviceShare" && o.Str("responseid") == id,
            "ficha/compartir/tiempo-agotado", "ficha/compartir/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") is { } res && res != "OK")
            throw new MeshClientException(MeshFailure.Rejected, "ficha/compartir/rechazo-remoto", $"The server did not remove the link: {res}.") { ServerMessage = res };
    }
}
