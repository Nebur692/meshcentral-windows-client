using System.Net.Http;
using System.Text.RegularExpressions;

namespace MeshCentralClient.Core;

/// <summary>A single sign-on provider a MeshCentral domain offers. / Un proveedor de inicio de sesión único que ofrece un dominio de MeshCentral.</summary>
public sealed record SingleSignOn(string Strategy)
{
    /// <summary>Where the browser starts it, relative to the server address. / Dónde lo arranca el navegador, respecto a la dirección del servidor.</summary>
    public string Path => "auth-" + Strategy;

    /// <summary>A readable name, without assuming any particular company. / Un nombre legible, sin dar por hecho ninguna empresa.</summary>
    public string DisplayName => Strategy switch
    {
        "azure" => "Microsoft",
        "google" => "Google",
        "github" => "GitHub",
        "twitter" => "X (Twitter)",
        "reddit" => "Reddit",
        "jumpcloud" => "JumpCloud",
        "intel" => "Intel",
        "saml" => "SAML",
        _ => "OpenID Connect",
    };
}

/// <summary>
/// Reads which sign-in methods a server offers from its own login page (MeshCentral writes them as
/// <c>authStrategies = 'oidc,saml'.split(',')</c>). The page names every possible method in its code, so only that
/// list counts.
/// Lee qué formas de entrar ofrece un servidor en su propia página de entrada (MeshCentral las escribe como
/// <c>authStrategies = 'oidc,saml'.split(',')</c>). La página nombra todas las posibles en su código, así que solo
/// cuenta esa lista.
/// </summary>
public static partial class SignInMethods
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        { "oidc", "saml", "azure", "google", "github", "twitter", "reddit", "jumpcloud", "intel" };

    [GeneratedRegex(@"authStrategies\s*=\s*'([a-z0-9,\-]*)'\s*\.split", RegexOptions.CultureInvariant)]
    private static partial Regex StrategiesRx();

    public static IReadOnlyList<SingleSignOn> ParseLoginPage(string html)
    {
        var m = StrategiesRx().Match(html);
        if (!m.Success) return Array.Empty<SingleSignOn>();
        return m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Known.Contains).Distinct().Select(s => new SingleSignOn(s)).ToList();
    }

    /// <summary>Never throws: if the page cannot be read, the server simply offers nothing extra.
    /// No lanza nunca: si la página no se puede leer, el servidor sencillamente no ofrece nada más.</summary>
    public static async Task<IReadOnlyList<SingleSignOn>> DiscoverAsync(ServerAddress server, CertificateTrust trust, CancellationToken ct = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => trust.Validate(req, cert, chain, errors),
                AllowAutoRedirect = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCentralWindowsClient/" + typeof(SignInMethods).Assembly.GetName().Version?.ToString(3));
            var html = await http.GetStringAsync(server.BaseUri, ct).ConfigureAwait(false);
            return ParseLoginPage(html);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Array.Empty<SingleSignOn>();
        }
    }
}
