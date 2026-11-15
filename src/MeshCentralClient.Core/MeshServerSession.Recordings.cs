using System.Net;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>What kind of session was recorded (relay "protocol"). / Qué tipo de sesión se grabó.</summary>
public enum RecordedKind { Other, Terminal, Desktop, Files, AdminPowerShell, UserShell, UserPowerShell, AmtWsman, AmtRedirection, Chat }

/// <summary>One recorded session, as the website's "Recordings" lists it. / Una sesión grabada, como la lista la web.</summary>
public sealed record Recording(string FileName, RecordedKind Kind, string? DeviceId, string? DeviceName, string? GroupName,
    IReadOnlyList<string> UserIds, DateTimeOffset? Start, TimeSpan Length, long Bytes, bool Present)
{
    internal static Recording? From(JsonObject o)
    {
        if (o.Str("filename") is not { } f) return null;
        var kind = o.Long("protocol") switch
        {
            1 => RecordedKind.Terminal, 2 => RecordedKind.Desktop, 5 => RecordedKind.Files, 6 => RecordedKind.AdminPowerShell,
            8 => RecordedKind.UserShell, 9 => RecordedKind.UserPowerShell, 100 => RecordedKind.AmtWsman, 101 => RecordedKind.AmtRedirection,
            200 => RecordedKind.Chat, _ => RecordedKind.Other,
        };
        var users = (o["userids"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        var start = o.Long("startTime") is { } ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        return new Recording(f, kind, o.Str("nodeid"), o.Str("name"), o.Str("meshname"), users, start,
            TimeSpan.FromSeconds(o.Long("lengthTime") ?? 0), o.Long("size") ?? 0, o.Long("present") == 1);
    }
}

public sealed partial class MeshServerSession
{
    /// <summary>This account may see recordings (site right 512); the server still has to record. / Puede ver grabaciones.</summary>
    public bool CanSeeRecordings => HasSite(User.SiteRights, SiteRight.ManageRecordings);

    /// <summary>
    /// The most recent recordings, newest first. The server stays silent when recording is off (meshuser.js
    /// "recordings" returns without answering), so silence for a while means just that.
    /// Las grabaciones más recientes, primero las nuevas. El servidor calla si la grabación está apagada.
    /// </summary>
    public async Task<IReadOnlyList<Recording>> GetRecordingsAsync(int limit = 200, CancellationToken ct = default)
    {
        if (!CanSeeRecordings) throw new MeshClientException(MeshFailure.Rejected, "grabaciones/ver-pantalla/no-autorizado", "This account has no rights to see recordings.");
        var tag = NewId();
        JsonObject r;
        try
        {
            r = await RequestAsync(new JsonObject { ["action"] = "recordings", ["limit"] = limit, ["tag"] = tag },
                o => o.Str("action") == "recordings" && o.Str("tag") == tag,
                "grabaciones/ver-pantalla/tiempo-agotado", "grabaciones/ver-pantalla/conexion-cortada", ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (MeshClientException ex) when (ex.Failure == MeshFailure.Timeout)
        {
            throw new MeshClientException(MeshFailure.NotFound, "grabaciones/ver-pantalla/no-encontrado", "This server does not record sessions.", ex);
        }
        if (r.Long("error") is { } e)
            throw new MeshClientException(MeshFailure.Rejected, "grabaciones/ver-pantalla/rechazo-remoto", $"The server could not read its recordings (error {e}).");
        return (r["events"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(Recording.From).OfType<Recording>()
            .OrderByDescending(x => x.Start ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>The website's own list of recordings (viewmode 52), where the browser signs in if it has to.
    /// La lista de grabaciones de la propia web, donde el navegador entra si hace falta.</summary>
    public Uri RecordingsPageUri => new(Server.BaseUri, "?viewmode=52");

    /// <summary>
    /// Downloads a recording with the web session's cookies. The server serves it only to a signed-in web session
    /// (webserver.js handleGetRecordings reads req.session), never to x-meshauth nor the relay cookie: measured on
    /// 1.3.1, both answer 401. So without a single sign-on it says so before trying.
    /// Baja una grabación con las cookies de la sesión web. El servidor solo la da a una sesión web iniciada (medido:
    /// x-meshauth y la cookie del relé dan 401), así que sin inicio de sesión único lo dice antes de intentarlo.
    /// </summary>
    public async Task<long> DownloadRecordingAsync(Recording recording, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destination);
        var cookies = _webCookies ?? throw new MeshClientException(MeshFailure.Rejected, "grabaciones/descargar/sesion-cerrada",
            "The server only gives recordings to a web session: open it on the website.");
        if (!recording.Present) throw new MeshClientException(MeshFailure.NotFound, "grabaciones/descargar/no-encontrado", "The recording's file is no longer on the server.");
        var jar = new CookieContainer();
        foreach (var (name, value) in cookies) jar.Add(Server.BaseUri, new Cookie(name, value, "/"));
        using var handler = new HttpClientHandler { CookieContainer = jar, ServerCertificateCustomValidationCallback = (m, c, ch, e) => Trust.Validate(m, c, ch, e) };
        using var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var uri = new Uri(Server.BaseUri, "recordings.ashx?file=" + Uri.EscapeDataString(recording.FileName));
        try
        {
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new MeshClientException(MeshFailure.SessionExpired, "grabaciones/descargar/sesion-cerrada", "The server did not accept the web session: sign in again.");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new MeshClientException(MeshFailure.NotFound, "grabaciones/descargar/no-encontrado", "The recording's file is no longer on the server.");
            if (!resp.IsSuccessStatusCode)
                throw new MeshClientException(MeshFailure.HttpError, "grabaciones/descargar/rechazo-remoto", $"The server answered HTTP {(int)resp.StatusCode}.");
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
            return total;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, "grabaciones/descargar/conexion-cortada", "The download was cut.", ex);
        }
    }
}
