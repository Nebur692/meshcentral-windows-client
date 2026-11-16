using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshCentralClient.Core;

/// <summary>What a security key signs to sign in (WebAuthn get). The client data is built here, with the server's real
/// challenge and its https origin, so the signature is the one a browser would give.
/// Lo que firma una llave para entrar. Los datos del cliente se hacen aquí, con el reto de verdad y el origen https.</summary>
public sealed record KeyRequest(string RpId, string Origin, byte[] ClientDataJson, IReadOnlyList<byte[]> AllowedIds, string UserVerification, TimeSpan Timeout);

/// <summary>What a security key needs to make a new credential (WebAuthn create). / Lo que necesita una llave para crear una credencial.</summary>
public sealed record KeyCreation(string RpId, string RpName, byte[] UserId, string UserName, string UserDisplayName, byte[] ClientDataJson,
    IReadOnlyList<byte[]> ExcludedIds, string UserVerification, TimeSpan Timeout);

public sealed record KeyAssertion(byte[] CredentialId, byte[] AuthenticatorData, byte[] Signature, byte[]? UserHandle);
public sealed record KeyAttestation(byte[] CredentialId, byte[] AttestationObject);

/// <summary>A security key or passkey: Windows Hello, a USB/NFC key or a phone (webauthn.dll) in the app; a key in
/// software in the tests. / Una llave de seguridad: Windows Hello, una llave USB/NFC o un móvil en la app; una por software en las pruebas.</summary>
public interface ISecurityKey
{
    Task<KeyAssertion> GetAssertionAsync(KeyRequest request, CancellationToken ct);
    Task<KeyAttestation> MakeCredentialAsync(KeyCreation creation, CancellationToken ct);
}

/// <summary>
/// WebAuthn with MeshCentral. Keys are added over the control channel (webauthn-startregister / -endregister), but the
/// control channel never takes a key to sign in (it passes no hardware token), so signing in with one goes through the
/// login form, as the website: POST / action=login gives the second-factor page with the challenge and an encrypted
/// state (hwstate), and POST / action=tokenlogin with the signed answer gives the session cookies.
/// WebAuthn con MeshCentral. Las llaves se añaden por el canal de control, pero ese canal nunca acepta una llave para
/// entrar, así que se entra por el formulario, como la web: login da la página del segundo factor con el reto y un
/// estado cifrado, y tokenlogin con la respuesta firmada da las cookies de sesión.
/// </summary>
public static partial class SecurityKeys
{
    /// <summary>The origin and relying party a browser would use for this server. / El origen y el sitio que usaría un navegador.</summary>
    public static (string RpId, string Origin) Site(ServerAddress server)
    {
        var u = server.BaseUri;
        return (u.Host, u.IsDefaultPort ? $"https://{u.Host}" : $"https://{u.Host}:{u.Port}");
    }

    /// <summary>The client data a browser makes (type, challenge in base64url, origin). / Los datos del cliente que hace un navegador.</summary>
    public static byte[] ClientData(string type, byte[] challenge, string origin) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["type"] = type, ["challenge"] = Base64Url(challenge), ["origin"] = origin, ["crossOrigin"] = false,
    }.ToJsonString());

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The full and the minified pages differ: quotes ' or " around the challenge, hwstate quoted or not.
    // La página entera y la minificada cambian: comillas ' o " en el reto, hwstate con o sin comillas.
    [GeneratedRegex(@"hardwareKeyChallenge\s*=\s*decodeURIComponent\((['""])([^'""]*)\1\)", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengeRx();

    [GeneratedRegex(@"name=hwstate\s+value=(""?)([^""\s>]*)\1", RegexOptions.CultureInvariant)]
    private static partial Regex StateRx();

    /// <summary>The key challenge of the second-factor page, or null if the account has no key. / El reto de la página, o null si no hay llave.</summary>
    internal static (JsonObject Challenge, string State)? ParseSecondFactorPage(string html)
    {
        var c = ChallengeRx().Match(html);
        var s = StateRx().Match(html);
        if (!c.Success || !s.Success) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(Uri.UnescapeDataString(c.Groups[2].Value)) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
        if (o is null || o.Str("type") != "webAuthn" || o.Str("challenge") is null || s.Groups[2].Value.Length == 0) return null;
        return (o, WebUtility.HtmlDecode(s.Groups[2].Value));
    }

    /// <summary>
    /// Signs in with the password and a security key, as the website does, and opens the control channel with the session
    /// it gives. Throws InvalidCredentials for a wrong password, NotFound if the account has no key, and InvalidTwoFactorCode
    /// if the server does not take the key's answer.
    /// Entra con la contraseña y una llave, como la web, y abre el canal de control con la sesión que da.
    /// </summary>
    public static async Task<MeshServerSession> SignInAsync(ServerAddress server, string user, string password, CertificateTrust trust,
        ISecurityKey key, CancellationToken ct = default)
    {
        var jar = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => trust.Validate(req, cert, chain, errors),
            CookieContainer = jar, AllowAutoRedirect = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCentralWindowsClient/" + typeof(SecurityKeys).Assembly.GetName().Version?.ToString(3));

        async Task<string> Post(Dictionary<string, string> form)
        {
            try
            {
                using var r = await http.PostAsync(server.BaseUri, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode)
                    throw new MeshClientException(MeshFailure.HttpError, "conexion/verificar-identidad/error-remoto", $"The server answered the sign-in form with HTTP {(int)r.StatusCode}.") { HttpStatus = (int)r.StatusCode };
                return await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new MeshClientException(MeshFailure.ServerUnreachable, "conexion/verificar-identidad/sin-conexion", "The sign-in form could not be reached.", e);
            }
        }

        var page = await Post(new() { ["action"] = "login", ["username"] = user, ["password"] = password }).ConfigureAwait(false);
        var second = ParseSecondFactorPage(page)
            ?? throw new MeshClientException(MeshFailure.NotFound, "conexion/verificar-identidad/no-encontrado",
                "The server asked for no security key: the password is wrong, or the account has no key.");

        var (rpId, origin) = Site(server);
        var ch = second.Challenge;
        var allowed = (ch["keyIds"] as JsonArray ?? new JsonArray()).Select(k => Convert.FromBase64String(k!.GetValue<string>())).ToList();
        var request = new KeyRequest(rpId, origin, ClientData("webauthn.get", Convert.FromBase64String(ch.Str("challenge")!), origin), allowed,
            ch.Str("userVerification") ?? "preferred", TimeSpan.FromMilliseconds(ch.Long("timeout") ?? 60_000));
        var a = await key.GetAssertionAsync(request, ct).ConfigureAwait(false);
        var answer = new JsonObject
        {
            ["id"] = Convert.ToBase64String(a.CredentialId),
            ["clientDataJSON"] = Convert.ToBase64String(request.ClientDataJson),
            ["userHandle"] = Convert.ToBase64String(a.UserHandle ?? Array.Empty<byte>()),
            ["signature"] = Convert.ToBase64String(a.Signature),
            ["authenticatorData"] = Convert.ToBase64String(a.AuthenticatorData),
        };
        await Post(new() { ["action"] = "tokenlogin", ["hwstate"] = second.State, ["token"] = "", ["hwtoken"] = answer.ToJsonString() }).ConfigureAwait(false);

        var cookies = jar.GetCookies(server.BaseUri).Cast<Cookie>().ToDictionary(c => c.Name, c => c.Value);
        try
        {
            return await MeshServerSession.ConnectWithSessionCookiesAsync(server, cookies, trust, ct).ConfigureAwait(false);
        }
        catch (MeshClientException e) when (e.Failure is MeshFailure.SessionExpired or MeshFailure.InvalidCredentials or MeshFailure.Rejected)
        {
            throw new MeshClientException(MeshFailure.InvalidTwoFactorCode, "conexion/verificar-identidad/credencial-invalida", "The server did not take the security key's answer.", e);
        }
    }
}
