using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Security keys against the bench: a key in software (P-256, attestation "none", as a passkey gives) is added over the
/// control channel and then signs in through the login form, as the website does; the control channel alone keeps asking
/// for the second factor. What the app does with webauthn.dll (Windows Hello, a USB key or a phone). Uses the "mfa" account.
/// Llaves de seguridad contra el banco: una llave por software se añade por el canal de control y luego entra por el
/// formulario, como la web; el canal de control solo sigue pidiendo el segundo factor. Lo que hace la app con webauthn.dll.
/// </summary>
[Trait("Category", "Integration")]
[Collection("mfa")]
public class SecurityKeysIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }
    private static CertificateTrust Trust => new(Env("MESHTEST_CERT_SHA256"));

    private static Task<MeshServerSession> Connect() => MeshServerSession.ConnectAsync(Server, new("mfa", "Mfa-Pass-1234", null), Trust);

    [Fact]
    public async Task A_key_is_added_signs_in_through_the_form_and_is_removed()
    {
        var key = new SoftwareKey();
        var s = await Connect();
        try
        {
            await s.AddSecurityKeyAsync("Llave de prueba", key);
            Assert.Equal(1, s.User.SecurityKeys);
            Assert.Contains(await s.GetSecurityKeysAsync(), k => k.Name == "Llave de prueba");
            await s.DisposeAsync();

            // The control channel asks for the second factor, and takes no key. / El canal de control pide el segundo factor.
            var asked = await Assert.ThrowsAsync<MeshClientException>(Connect);
            Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);

            // Through the form: the key signs the server's real challenge, for the server's https origin.
            // Por el formulario: la llave firma el reto de verdad del servidor, para su origen https.
            s = await SecurityKeys.SignInAsync(Server, "mfa", "Mfa-Pass-1234", Trust, key);
            Assert.Equal("user//mfa", s.User.Id);
            var data = (JsonObject)JsonNode.Parse(key.LastRequest!.ClientDataJson)!;
            Assert.Equal("webauthn.get", data["type"]!.GetValue<string>());
            Assert.Equal(SecurityKeys.Site(Server).Origin, data["origin"]!.GetValue<string>());
            Assert.Equal(64, Convert.FromBase64String(data["challenge"]!.GetValue<string>().Replace('-', '+').Replace('_', '/') + "==").Length);
            Assert.Contains(key.LastRequest.AllowedIds, i => i.SequenceEqual(key.Id));

            // Another key is not this account's; a wrong password asks for no key. / Otra llave no vale; con la contraseña mala no hay reto.
            var other = await Assert.ThrowsAsync<MeshClientException>(() => SecurityKeys.SignInAsync(Server, "mfa", "Mfa-Pass-1234", Trust, new SoftwareKey()));
            Assert.Equal(MeshFailure.InvalidTwoFactorCode, other.Failure);
            var wrong = await Assert.ThrowsAsync<MeshClientException>(() => SecurityKeys.SignInAsync(Server, "mfa", "Not-The-Pass-1", Trust, key));
            Assert.Equal(MeshFailure.NotFound, wrong.Failure);

            // A key that is not there is not "removed" (the server sends its event anyway). / Una llave que no está no se «quita».
            var none = await Assert.ThrowsAsync<MeshClientException>(() => s.RemoveSecurityKeyAsync(new SecurityKey(1, "none", null)));
            Assert.Equal("usuarios/cambiar-ajustes/rechazo-remoto", none.BurburitoKey);

            // Removed: the password alone signs in again. / Quitada: vuelve a entrar solo con la contraseña.
            foreach (var k in await s.GetSecurityKeysAsync()) await s.RemoveSecurityKeyAsync(k);
            await s.DisposeAsync();
            s = await Connect();
            Assert.Equal(0, s.User.SecurityKeys);
        }
        finally
        {
            // A test that failed halfway signs in with the key and removes it, so the other "mfa" tests do not fail.
            // Una prueba que falla a medias entra con la llave y la quita, para no tumbar a las demás de «mfa».
            try { foreach (var k in await s.GetSecurityKeysAsync()) await s.RemoveSecurityKeyAsync(k); }
            catch (Exception)
            {
                try
                {
                    await using var c = await SecurityKeys.SignInAsync(Server, "mfa", "Mfa-Pass-1234", Trust, key);
                    foreach (var k in await c.GetSecurityKeysAsync()) await c.RemoveSecurityKeyAsync(k);
                }
                catch (MeshClientException) { }
            }
            await s.DisposeAsync();
        }
    }
}

/// <summary>A security key in software: P-256, attestation "none", user present and verified. / Una llave por software.</summary>
internal sealed class SoftwareKey : ISecurityKey
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _counter;
    public byte[] Id { get; } = RandomNumberGenerator.GetBytes(32);
    public KeyRequest? LastRequest { get; private set; }

    public Task<KeyAttestation> MakeCredentialAsync(KeyCreation c, CancellationToken ct)
    {
        var p = _key.ExportParameters(false);
        var cose = Cbor.Map((Cbor.Int(1), Cbor.Int(2)), (Cbor.Int(3), Cbor.Int(-7)), (Cbor.Int(-1), Cbor.Int(1)), (Cbor.Int(-2), Cbor.Bytes(p.Q.X!)), (Cbor.Int(-3), Cbor.Bytes(p.Q.Y!)));
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(c.RpId)), [0x45], Be32(_counter), new byte[16], [(byte)(Id.Length >> 8), (byte)Id.Length], Id, cose);
        var att = Cbor.Map((Cbor.Text("fmt"), Cbor.Text("none")), (Cbor.Text("attStmt"), Cbor.Map()), (Cbor.Text("authData"), Cbor.Bytes(authData)));
        return Task.FromResult(new KeyAttestation(Id, att));
    }

    public Task<KeyAssertion> GetAssertionAsync(KeyRequest r, CancellationToken ct)
    {
        LastRequest = r;
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(r.RpId)), [0x05], Be32(++_counter));
        var signature = _key.SignData(Concat(authData, SHA256.HashData(r.ClientDataJson)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Task.FromResult(new KeyAssertion(Id, authData, signature, null));
    }

    private static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
}

/// <summary>Just enough CBOR (RFC 8949) for an attestation. / Lo justo de CBOR para una atestación.</summary>
internal static class Cbor
{
    private static byte[] Head(int major, ulong n) => n switch
    {
        < 24 => [(byte)((major << 5) | (int)n)],
        < 0x100 => [(byte)((major << 5) | 24), (byte)n],
        < 0x10000 => [(byte)((major << 5) | 25), (byte)(n >> 8), (byte)n],
        _ => [(byte)((major << 5) | 26), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n],
    };
    public static byte[] Int(long v) => v >= 0 ? Head(0, (ulong)v) : Head(1, (ulong)(-1 - v));
    public static byte[] Bytes(byte[] b) => [.. Head(2, (ulong)b.Length), .. b];
    public static byte[] Text(string s) { var b = Encoding.UTF8.GetBytes(s); return [.. Head(3, (ulong)b.Length), .. b]; }
    public static byte[] Map(params (byte[] Key, byte[] Value)[] items) => [.. Head(5, (ulong)items.Length), .. items.SelectMany(i => i.Key.Concat(i.Value))];
}
