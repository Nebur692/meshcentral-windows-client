namespace MeshCentralClient.Core;

/// <summary>
/// The address of a MeshCentral server (and optional domain), normalised so it always ends in "/".
/// La dirección de un servidor MeshCentral (y su dominio, si lo hay), normalizada para que acabe siempre en «/».
/// </summary>
public sealed class ServerAddress
{
    private ServerAddress(Uri baseUri) => BaseUri = baseUri;

    /// <summary>https://host[:port]/[domain/]</summary>
    public Uri BaseUri { get; }

    public string Host => BaseUri.Host;

    /// <summary>wss://host[:port]/[domain/]control.ashx</summary>
    public Uri ControlUri => WebSocketUri("control.ashx");

    internal Uri WebSocketUri(string relative)
    {
        var b = new UriBuilder(new Uri(BaseUri, relative)) { Scheme = "wss" };
        if (BaseUri.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    /// <summary>
    /// Accepts "host", "host:port", "https://host/domain" or "wss://…". Only encrypted addresses are accepted.
    /// Acepta «host», «host:puerto», «https://host/dominio» o «wss://…». Solo se aceptan direcciones cifradas.
    /// </summary>
    public static bool TryParse(string? text, out ServerAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != "wss") return false;
        if (string.IsNullOrEmpty(u.Host) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        var path = u.AbsolutePath;
        foreach (var page in new[] { "control.ashx", "login", "auth-oidc" })
            if (path.EndsWith("/" + page, StringComparison.OrdinalIgnoreCase)) path = path[..^page.Length];
        if (!path.EndsWith('/')) path += "/";
        var b = new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Path = path };
        if (u.IsDefaultPort || u.Port == 443) b.Port = -1;
        address = new ServerAddress(b.Uri);
        return true;
    }

    public override string ToString() => BaseUri.ToString();
}
