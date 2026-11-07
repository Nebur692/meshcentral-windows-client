using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshCentralClient.Core;

/// <summary>
/// Decides whether the server's TLS certificate is trusted. A certificate the system trusts is always accepted
/// (so a renewed public certificate keeps working); otherwise only the SHA-256 fingerprint the user accepted is.
/// Decide si se confía en el certificado TLS del servidor. Uno en el que confía el sistema se acepta siempre
/// (así un certificado público renovado sigue valiendo); si no, solo el de la huella SHA-256 que aceptó la persona.
/// </summary>
public sealed class CertificateTrust
{
    public CertificateTrust(string? pinnedSha256 = null) => PinnedSha256 = Normalise(pinnedSha256);

    public string? PinnedSha256 { get; }

    /// <summary>Fingerprint of the last certificate rejected. / Huella del último certificado rechazado.</summary>
    public string? RejectedSha256 { get; private set; }
    public string? RejectedSubject { get; private set; }
    public SslPolicyErrors RejectedErrors { get; private set; }

    internal bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null) { Reject(null, null, errors); return false; }
        var fp = Fingerprint(certificate);
        if (PinnedSha256 is not null && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(fp), System.Text.Encoding.ASCII.GetBytes(PinnedSha256)))
            return true;
        Reject(fp, certificate.Subject, errors);
        return false;
    }

    private void Reject(string? fp, string? subject, SslPolicyErrors errors)
    {
        RejectedSha256 = fp;
        RejectedSubject = subject;
        RejectedErrors = errors;
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>AB:CD:EF… for people to compare. / AB:CD:EF… para que lo compare una persona.</summary>
    public static string Display(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));

    private static string? Normalise(string? fp)
    {
        if (string.IsNullOrWhiteSpace(fp)) return null;
        var hex = new string(fp.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 64 ? hex : throw new ArgumentException("A SHA-256 fingerprint has 64 hex digits.", nameof(fp));
    }
}
