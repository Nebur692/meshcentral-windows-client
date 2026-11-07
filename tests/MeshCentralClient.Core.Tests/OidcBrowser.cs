using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

/// <summary>
/// The smallest browser that can go through a sign-on: follows redirects by hand (HttpClient does not follow https→http),
/// keeps cookies per host and submits the test provider's forms. Only for the tests.
/// El navegador más pequeño capaz de recorrer un inicio de sesión: sigue las redirecciones a mano (HttpClient no sigue
/// de https a http), guarda las cookies por servidor y envía los formularios del proveedor de pruebas. Solo para las pruebas.
/// </summary>
internal sealed class OidcBrowser : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Dictionary<string, string>> _jar = new(StringComparer.OrdinalIgnoreCase);

    public OidcBrowser(string pinnedSha256)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || (cert is not null && MeshCentralClient.Core.CertificateTrust.Fingerprint(cert) == pinnedSha256),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public IReadOnlyDictionary<string, string> CookiesFor(string host) =>
        _jar.TryGetValue(host, out var c) ? c : new Dictionary<string, string>();

    public void Dispose() => _http.Dispose();

    /// <summary>Signs in as <paramref name="user"/> starting at the server's auth-… page; returns the last page.
    /// Entra como <paramref name="user"/> desde la página auth-… del servidor; devuelve la última página.</summary>
    public async Task<(Uri Url, string Body)> SignInAsync(Uri start, string user)
    {
        var (url, body) = await GoAsync(start, null);
        for (var step = 0; step < 5 && url.AbsolutePath.Contains("/interaction/"); step++)
        {
            var action = Regex.Match(body, "action=\"([^\"]+)\"").Groups[1].Value;
            var fields = Regex.Matches(body, "<input[^>]*>").Select(m => m.Value)
                .Select(tag => (Name: Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value, Value: Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value))
                .Where(f => f.Name.Length > 0)
                .Select(f => new KeyValuePair<string, string>(f.Name, f.Name == "login" ? user : f.Name == "password" ? "any" : f.Value))
                .ToList();
            (url, body) = await GoAsync(new Uri(url, WebUtility.HtmlDecode(action)), new FormUrlEncodedContent(fields));
        }
        return (url, body);
    }

    private async Task<(Uri, string)> GoAsync(Uri url, HttpContent? post)
    {
        for (var i = 0; i < 25; i++)
        {
            using var req = new HttpRequestMessage(post is null ? HttpMethod.Get : HttpMethod.Post, url) { Content = post };
            if (_jar.TryGetValue(url.Authority, out var c) && c.Count > 0)
                req.Headers.Add("Cookie", string.Join("; ", c.Select(kv => $"{kv.Key}={kv.Value}")));
            using var res = await _http.SendAsync(req);
            if (res.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var sc in set)
                {
                    var kv = sc.Split(';')[0];
                    var eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!_jar.TryGetValue(url.Authority, out var hostJar)) _jar[url.Authority] = hostJar = new(StringComparer.Ordinal);
                    hostJar[kv[..eq]] = kv[(eq + 1)..];
                }
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                url = new Uri(url, loc);
                post = null;
                continue;
            }
            return (url, await res.Content.ReadAsStringAsync());
        }
        throw new InvalidOperationException("too many redirects / demasiadas redirecciones");
    }
}
