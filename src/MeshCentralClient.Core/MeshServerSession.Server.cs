using System.Globalization;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>One entry of the server's event log. / Una entrada del registro de sucesos del servidor.</summary>
public sealed record ServerEvent(DateTimeOffset? Time, string? Action, string Message, string? UserId, string? UserName, string? NodeId, string? MeshId)
{
    internal static ServerEvent From(JsonObject e) => new(
        e.Str("time") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d
            : e.Long("time") is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
        e.Str("action"), e.Str("msg") ?? "", e.Str("userid"), e.Str("username"), e.Str("nodeid"), e.Str("meshid"));
}

/// <summary>A notification for whoever uses this app (an administrator's broadcast or a message to this account).
/// Un aviso para quien usa esta app (un aviso general de un administrador o un mensaje a esta cuenta).</summary>
public sealed record ServerNotification(string Title, string Text);

public enum ReportKind { RemoteSessions = 1, Traffic = 2, Logins = 3, Database = 4 }

public enum ReportGroupBy { User = 1, Device = 2, Day = 3 }

/// <summary>A column of a report: <see cref="Format"/> says how its raw values read (datetime and time are milliseconds,
/// user/node/mesh are ids, bytes, seconds, msg is a message id). / Una columna de un informe y cómo se leen sus valores.</summary>
public sealed record ReportColumn(string Id, string Title, string? Format);

public sealed record ReportGroup(string Key, IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>> Rows);

/// <summary>A report as the server builds it for the website. / Un informe tal como lo hace el servidor para la web.</summary>
public sealed record Report(IReadOnlyList<ReportColumn> Columns, string? GroupFormat, IReadOnlyList<ReportGroup> Groups);

/// <summary>The server's own state, sent every few seconds while asked for. / El estado del propio servidor.</summary>
public sealed record ServerStats(long TotalMemory, long FreeMemory, long? AvailableMemory, IReadOnlyList<double> CpuAverage,
    IReadOnlyDictionary<string, long> State, IReadOnlyDictionary<string, long> AgentErrors);

/// <summary>
/// The server as a whole: its event log, the website's reports, its live state and error log, and notifications to
/// connected people. Measured on MeshCentral 1.3.1: "events" without rights to manage users falls back silently to the
/// account's own events, "report" and "serverstats" drop a request they do not like without answering, and "notifyuser"
/// never answers, so a notification goes through "userbroadcast", which does.
/// El servidor entero: su registro de sucesos, los informes de la web, su estado en vivo y su registro de errores, y avisos
/// a las personas conectadas. Medido en 1.3.1: «events» sin permiso de gestionar usuarios cae sin decir nada en los
/// sucesos de la propia cuenta, «report» y «serverstats» tiran sin contestar lo que no les gusta, y «notifyuser» nunca
/// contesta, así que un aviso va por «userbroadcast», que sí.
/// </summary>
public sealed partial class MeshServerSession
{
    /// <summary>Every new event the server sends this session (devices, accounts, groups…). / Cada suceso nuevo que llega.</summary>
    public event EventHandler<ServerEvent>? EventReceived;

    public event EventHandler<ServerNotification>? NotificationReceived;

    /// <summary>The live state, while <see cref="WatchServerStatsAsync"/> is on. / El estado en vivo, mientras se vigila.</summary>
    public event EventHandler<ServerStats>? ServerStatsReceived;

    /// <summary>Server backup, restore or update (siteadmin bits 1, 4 or 16), as the website asks for its server page.
    /// Copia, restauración o actualización del servidor, como pide la web para su página del servidor.</summary>
    public bool CanSeeServer => User.SiteRights == Full || (User.SiteRights & 21) != 0;

    public bool CanSeeErrorLog => User.SiteRights == Full || (User.SiteRights & (long)SiteRight.ServerUpdates) != 0;

    /// <summary>The events this account may see, newest first. / Los sucesos que esta cuenta puede ver, los más nuevos primero.</summary>
    public async Task<IReadOnlyList<ServerEvent>> GetServerEventsAsync(int limit = 500, CancellationToken ct = default)
    {
        // Without any link the server does not answer at all. / Sin ningún enlace el servidor no contesta nada.
        if (User.Links.Count == 0) return [];
        return await EventsAsync(new JsonObject { ["action"] = "events", ["limit"] = Math.Clamp(limit, 1, 100000) }, ct).ConfigureAwait(false);
    }

    /// <summary>What an account did, for whoever manages users. / Lo que hizo una cuenta, para quien gestiona usuarios.</summary>
    public async Task<IReadOnlyList<ServerEvent>> GetUserEventsAsync(UserInfo user, int limit = 500, CancellationToken ct = default)
    {
        // Without the right the server answers with this account's own events instead: refused here.
        // Sin el permiso el servidor contesta con los sucesos de la propia cuenta: se rechaza aquí.
        if (!CanManageUsers) throw UserError("servidor/abrir-detalle/no-autorizado", "Only an account that manages users sees another one's events.");
        return await EventsAsync(new JsonObject { ["action"] = "events", ["userid"] = user.Id, ["limit"] = Math.Clamp(limit, 1, 100000) }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ServerEvent>> EventsAsync(JsonObject cmd, CancellationToken ct)
    {
        var id = NewId();
        cmd["tag"] = id;
        var r = await RequestAsync(cmd, o => o.Str("action") == "events" && o.Str("tag") == id,
            "servidor/abrir-detalle/tiempo-agotado", "servidor/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        return (r["events"] as JsonArray)?.OfType<JsonObject>().Select(ServerEvent.From).OrderByDescending(e => e.Time).ToList() ?? [];
    }

    /// <summary>
    /// One of the website's reports, between two moments. Remote sessions and logins can be grouped by user or day
    /// (sessions also by device); traffic is per user; the database report is for full administrators only.
    /// Uno de los informes de la web, entre dos momentos.
    /// </summary>
    public async Task<Report> GetReportAsync(ReportKind kind, ReportGroupBy groupBy, DateTimeOffset from, DateTimeOffset to,
        string? deviceGroupId = null, bool showTraffic = false, CancellationToken ct = default)
    {
        // The server drops each of these without answering; the login report grouped by device even breaks it and the
        // whole connection closes (measured with the lie on 1.3.1). / El servidor tira cada una sin contestar; el de
        // entradas agrupado por equipo incluso lo rompe y se cierra la conexión entera (medido con la mentira).
        if (from >= to) throw UserError("servidor/informe/dato-invalido", "The start must be before the end.");
        if (kind == ReportKind.Database && !User.IsFullAdministrator) throw UserError("servidor/informe/no-autorizado", "Only a full administrator sees the database report.");
        if (kind == ReportKind.Logins && groupBy == ReportGroupBy.Device) throw UserError("servidor/informe/dato-invalido", "The login report groups by user or by day.");
        if (deviceGroupId is not null && !ServerInfo.ManageAllDeviceGroups && !User.Links.ContainsKey(deviceGroupId))
            throw UserError("servidor/informe/no-autorizado", "This account has no rights on that device group.");
        var cmd = new JsonObject
        {
            ["action"] = "report", ["type"] = (int)kind, ["groupBy"] = (int)groupBy,
            ["start"] = from.ToUnixTimeSeconds(), ["end"] = to.ToUnixTimeSeconds(), ["showTraffic"] = showTraffic,
            ["l"] = CultureInfo.CurrentCulture.Name is { Length: > 0 } l ? l : "en", ["tz"] = TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id : IanaZone(),
        };
        if (deviceGroupId is not null) cmd["devGroup"] = deviceGroupId;
        // "report" comes back without tag or responseid: one at a time. / Vuelve sin tag ni responseid: de uno en uno.
        await _reportLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await RequestAsync(cmd, o => o.Str("action") == "report", "servidor/informe/tiempo-agotado", "servidor/informe/conexion-cortada", ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (r["data"] is not JsonObject data) throw UserError("servidor/informe/respuesta-ilegible", "The report came back empty.");
            var columns = (data["columns"] as JsonArray)?.OfType<JsonObject>().Select(c => new ReportColumn(c.Str("id") ?? "", c.Str("title") ?? "", c.Str("format"))).ToList() ?? [];
            var groups = (data["groups"] as JsonObject)?.Select(g => new ReportGroup(g.Key,
                ((g.Value as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>()
                    .Select(e => (IReadOnlyDictionary<string, JsonNode?>)e.ToDictionary(p => p.Key, p => p.Value?.DeepClone())).ToList() ?? [])).ToList() ?? [];
            return new(columns, data.Str("groupFormat"), groups);
        }
        finally { _reportLock.Release(); }
    }

    private readonly SemaphoreSlim _reportLock = new(1, 1);

    private static string IanaZone() => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";

    /// <summary>Starts (or with null stops) the server's live state every <paramref name="every"/>. / Empieza (o con null para) el estado en vivo.</summary>
    public Task WatchServerStatsAsync(TimeSpan? every, CancellationToken ct = default)
    {
        if (!CanSeeServer) throw UserError("servidor/abrir-detalle/no-autorizado", "Only an administrator with server backup, restore or update rights sees the server's state.");
        var cmd = new JsonObject { ["action"] = "serverstats" };
        if (every is { } e) cmd["interval"] = (int)Math.Clamp(e.TotalMilliseconds, 1000, 1_000_000);
        return SendAsync(cmd, ct);
    }

    /// <summary>The server's error log (mesherrors.txt); empty when there is none. / El registro de errores del servidor; vacío si no hay.</summary>
    public async Task<string> GetServerErrorsAsync(CancellationToken ct = default)
    {
        if (!CanSeeErrorLog) throw UserError("servidor/abrir-detalle/no-autorizado", "Only an administrator with server update rights sees the error log.");
        var r = await RequestAsync(new JsonObject { ["action"] = "servererrors" }, o => o.Str("action") == "servererrors",
            "servidor/abrir-detalle/tiempo-agotado", "servidor/abrir-detalle/conexion-cortada", ct).ConfigureAwait(false);
        return r.Str("data") ?? "";
    }

    /// <summary>
    /// A notification to the people connected now: everyone, one account, or the accounts linked to a user group or
    /// device group (<paramref name="targetId"/>). / Un aviso a las personas conectadas ahora: todas, una cuenta, o las
    /// cuentas enlazadas a un grupo.
    /// </summary>
    public async Task SendNotificationAsync(string text, UserInfo? toUser = null, string? targetId = null, CancellationToken ct = default)
    {
        if (!CanManageUsers) throw UserError("servidor/enviar-mensaje/no-autorizado", "Only an account that manages users sends notifications.");
        text = text.Trim();
        if (text.Length is 0 or > 512) throw UserError("servidor/enviar-mensaje/dato-invalido", "The notification needs 1 to 512 characters.");
        var cmd = new JsonObject { ["action"] = "userbroadcast", ["msg"] = text };
        if (toUser is not null) cmd["userid"] = toUser.Id;
        if (targetId is not null) cmd["target"] = targetId;
        var r = await AnsweredRequest(cmd, "servidor/enviar-mensaje/tiempo-agotado", "servidor/enviar-mensaje/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("servidor/enviar-mensaje/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
    }

    // ------------------------------------------------------------------ incoming / entrante

    /// <summary>Every event with a message is told to the event log listeners. / Todo suceso con mensaje se cuenta a quien escucha el registro.</summary>
    private void OnServerEvent(JsonObject e)
    {
        if (EventReceived is not { } handler || e.Str("msg") is not { Length: > 0 } || e["nolog"] is not null) return;
        var ev = ServerEvent.From(e);
        if (ev.Time is null) ev = ev with { Time = DateTimeOffset.Now };
        try { handler(this, ev); } catch (Exception) { /* a subscriber must not kill the session / un suscriptor no puede tumbar la sesión */ }
    }

    /// <summary>Messages that are not events: notifications for the person, and the server's live state.
    /// Mensajes que no son sucesos: avisos para la persona y el estado en vivo del servidor.</summary>
    private void OnServerMessage(JsonObject o)
    {
        try
        {
            switch (o.Str("action"))
            {
                // Replies to this app's own requests carry a titleid and are shown where they were asked for.
                // Las respuestas a peticiones de la propia app llevan titleid y se enseñan donde se pidieron.
                case "msg" when o.Str("type") == "notify" && o["titleid"] is null && o.Str("value") is { Length: > 0 } text:
                    NotificationReceived?.Invoke(this, new(o.Str("title") ?? o.Str("username") ?? "", text));
                    break;
                case "serverstats":
                    var state = new Dictionary<string, long>();
                    var errors = new Dictionary<string, long>();
                    if (o["values"] is JsonObject values)
                    {
                        if (values["ServerState"] is JsonObject s) foreach (var (k, v) in s) if (v is JsonValue n && n.TryGetValue<long>(out var l)) state[k] = l;
                        if (values["AgentErrorCounters"] is JsonObject a) foreach (var (k, v) in a) if (v is JsonValue n && n.TryGetValue<long>(out var l)) errors[k] = l;
                    }
                    var cpu = (o["cpuavg"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0).ToList() ?? [];
                    ServerStatsReceived?.Invoke(this, new(o.Long("totalmem") ?? 0, o.Long("freemem") ?? 0, o.Long("availablemem"), cpu, state, errors));
                    break;
            }
        }
        catch (Exception) { /* a subscriber must not kill the session / un suscriptor no puede tumbar la sesión */ }
    }
}
