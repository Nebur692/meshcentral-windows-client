using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MeshCentralClient.Core.Updates;

/// <summary>A newer version published in the repository's releases. / Una versión más nueva publicada en las releases del repositorio.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri ExeUrl, Uri Sha256Url, Uri? PageUrl);

/// <summary>
/// Asks the release feed (GitHub's "latest release" API) whether there is a newer version than the running one.
/// Pregunta a la fuente de versiones (la API «latest release» de GitHub) si hay una versión más nueva que la que corre.
/// </summary>
public sealed class UpdateChecker(HttpClient http, Uri feed)
{
    public const string ExeName = "MeshCentralClient.exe";
    public const string Sha256Name = "MeshCentralClient.exe.sha256";
    public static readonly Uri DefaultFeed = new("https://api.github.com/repos/Nebur692/meshcentral-windows-client/releases/latest");

    /// <summary>
    /// The feed to use: MESHCLIENT_UPDATE_FEED may point to another https feed or to a loopback one (tests), or be
    /// "off"; anything else is ignored. Null means updates are off.
    /// La fuente a usar: MESHCLIENT_UPDATE_FEED puede apuntar a otra fuente https o a una local (pruebas), o valer
    /// «off»; lo demás se ignora. Nulo es que no se buscan actualizaciones.
    /// </summary>
    public static Uri? FeedFromEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultFeed;
        if (value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            return u;
        return DefaultFeed;
    }

    /// <summary>Null when there is nothing newer (or no release yet). / Nulo si no hay nada más nuevo (o aún no hay releases).</summary>
    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, feed);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MeshCentralWindowsClient", current.ToString(3)));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new MeshClientException(MeshFailure.ServerUnreachable, "actualizar/desplegar/sin-conexion", "Could not reach the release feed.", ex); }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet / aún no hay ninguna
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/limite-de-tasa", "The release feed is limiting requests; it will be checked again later.") { HttpStatus = (int)resp.StatusCode };
            if (!resp.IsSuccessStatusCode)
                throw new MeshClientException(MeshFailure.HttpError, "actualizar/desplegar/error-remoto", $"The release feed answered HTTP {(int)resp.StatusCode}.") { HttpStatus = (int)resp.StatusCode };
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, current);
        }
    }

    internal static AvailableUpdate? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) throw Bad($"tag \"{tag}\"");
            v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            if (v <= new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) return null;
            Uri? exe = null, sha = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) continue;
                if (name == ExeName) exe = u;
                else if (name == Sha256Name) sha = u;
            }
            // A release without its fingerprint is never installed. / Una release sin su huella no se instala nunca.
            if (exe is null || sha is null) throw Bad("assets");
            Uri? page = r.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var p) ? p : null;
            return new AvailableUpdate(v, tag, exe, sha, page);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Bad("JSON", ex);
        }
    }

    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static MeshClientException Bad(string what, Exception? inner = null) =>
        new(MeshFailure.UnexpectedResponse, "actualizar/desplegar/respuesta-ilegible", $"The release feed sent an unexpected {what}.", inner);
}
