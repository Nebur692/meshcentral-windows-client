using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Two-factor by email and by SMS against the bench, whose server sends both to the bench's mailbox (tests/server/sink.mjs):
/// the account turns each on, signs in asking for the code, reads it from the mailbox as the person would, signs in with
/// it, and turns it off. Uses the "mfa" account, made for it by the bench (tests run in parallel). The same as the Android app's EmailSmsTwoFactorIntegrationTest.
/// Doble factor por correo y por SMS contra el banco, cuyo servidor manda los dos al buzón del banco. Usa la cuenta «mfa», hecha para ella por el banco (las pruebas corren en paralelo).
/// </summary>
[Trait("Category", "Integration")]
[Collection("mfa")] // the "mfa" account is shared with the messaging test: one after the other. / Cuenta compartida: una detrás de otra.
public class EmailSmsTwoFactorIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }

    private static Task<MeshServerSession> Connect(string? code = null) =>
        MeshServerSession.ConnectAsync(Server, new("mfa", "Mfa-Pass-1234", code), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));

    private static readonly HttpClient Http = new();

    private static async Task<JsonObject> Mailbox() => (JsonObject)JsonNode.Parse(await Http.GetStringAsync(Env("MESHTEST_SINK") + "/read"))!;

    private static async Task<int> Count(string kind) => ((JsonArray)(await Mailbox())[kind]!).Count;

    /// <summary>An email's body as text: quoted-printable joined, base64 parts decoded. / El cuerpo de un correo como texto.</summary>
    private static string Readable(string raw)
    {
        var body = raw.Contains("\n\n") ? raw[(raw.IndexOf("\n\n", StringComparison.Ordinal) + 2)..] : raw;
        var text = Regex.Replace(body.Replace("=\n", ""), "=([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        foreach (Match m in Regex.Matches(body, "(?m)^([A-Za-z0-9+/=]{20,})$"))
            try { text += "\n" + Encoding.UTF8.GetString(Convert.FromBase64String(m.Value)); } catch (FormatException) { }
        return text;
    }

    /// <summary>The code of the newest message after <paramref name="since"/>: 8 digits by email, 6 by SMS (webserver.js).
    /// El código del último mensaje: 8 cifras por correo, 6 por SMS.</summary>
    private static async Task<string> NewestCode(string kind, int since)
    {
        for (var i = 0; i < 40; i++)
        {
            // Only this account's mail: other tests' notices (a device that connects, by email) share the mailbox.
            // Solo el correo de esta cuenta: los avisos de otras pruebas (un equipo que se conecta) comparten el buzón.
            var mine = ((JsonArray)(await Mailbox())[kind]!).Skip(since).OfType<JsonObject>()
                .Where(x => kind == "sms" || (x["to"] as JsonArray)?.Any(t => t?.GetValue<string>() == "mfa@mesh.test") == true).ToList();
            if (mine.Count > 0)
            {
                var m = mine[^1];
                return CodeIn(kind, kind == "sms" ? m["msg"]!.GetValue<string>() : Readable(m["data"]!.GetValue<string>()));
            }
            await Task.Delay(250);
        }
        throw new Exception($"nothing arrived by {kind}");
    }

    /// <summary>The code in a message: by email, from its sentence, since the headers (Message-ID, boundary) are random and
    /// now and then carry 8 digits. / El código de un mensaje: por correo, de su frase, porque las cabeceras son al azar.</summary>
    private static string CodeIn(string kind, string text)
    {
        var found = Regex.Match(text, kind == "sms" ? @"(?<!\d)(\d{6})(?!\d)" : @"token is:\s*(\d{8})(?!\d)");
        Assert.True(found.Success, "no code in: " + text[..Math.Min(400, text.Length)]);
        return found.Groups[1].Value;
    }

    [Fact]
    public void The_code_is_read_from_its_sentence_not_from_a_header_full_of_digits()
    {
        const string mail = "From: mesh@mesh.test\nTo: mfa@mesh.test\nMessage-ID: <20261118-3729-82d9-e774-2122a23bcec1@mesh.test>\n" +
                            "Content-Transfer-Encoding: 7bit\nYour login token is: 58006776\nThis token can only be used once.";
        Assert.Equal("58006776", CodeIn("mails", Readable(mail)));
    }

    [Fact]
    public async Task Codes_by_email_and_by_SMS_are_turned_on_used_to_sign_in_and_turned_off()
    {
        var s = await Connect();
        try
        {
            Assert.True(s.ServerInfo.EmailTwoFactorAllowed, "the bench's server offers email codes");
            Assert.True(s.ServerInfo.SmsTwoFactorAllowed, "and SMS codes");

            // Email: on, then a sign-in asks for a code, the email brings it, it lets in. / Correo: se activa y deja entrar con su código.
            await s.SetEmailTwoFactorAsync(true);
            Assert.True(s.User.HasEmailTwoFactor);
            await s.DisposeAsync();
            var asked = await Assert.ThrowsAsync<MeshClientException>(() => Connect());
            Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);
            Assert.True(asked.EmailCodeAvailable);
            var mails = await Count("mails");
            var sent = await Assert.ThrowsAsync<MeshClientException>(() => Connect("**email**"));
            Assert.True(sent.EmailCodeSent);
            var code = await NewestCode("mails", mails);
            var wrong = await Assert.ThrowsAsync<MeshClientException>(() => Connect(((long.Parse(code) + 1) % 100_000_000).ToString("D8")));
            Assert.Equal(MeshFailure.InvalidTwoFactorCode, wrong.Failure);
            s = await Connect(code);
            Assert.True(s.User.HasEmailTwoFactor);

            // SMS: the phone is verified with the code it gets; a wrong code is refused. / SMS: se verifica con su código.
            var texts = await Count("sms");
            var check = await s.StartPhoneAsync("+34 600 000 001");
            var smsCode = await NewestCode("sms", texts);
            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.ConfirmPhoneAsync(check, ((int.Parse(smsCode) + 1) % 1_000_000).ToString("D6")));
            Assert.Equal("usuarios/cambiar-ajustes/credencial-invalida", bad.BurburitoKey);
            Assert.Null(s.User.Phone);
            var again = await s.StartPhoneAsync("+34 600 000 001");
            await s.ConfirmPhoneAsync(again, await NewestCode("sms", texts + 1));
            Assert.Equal("+34 600 000 001", s.User.Phone);

            // Signing in, the SMS code is offered too and lets in. / Al entrar se ofrece también el SMS y deja entrar.
            await s.DisposeAsync();
            var both = await Assert.ThrowsAsync<MeshClientException>(() => Connect());
            Assert.True(both.SmsCodeAvailable);
            var before = await Count("sms");
            var smsSent = await Assert.ThrowsAsync<MeshClientException>(() => Connect("**sms**"));
            Assert.True(smsSent.SmsCodeSent);
            s = await Connect(await NewestCode("sms", before));

            // Off again: the account signs in with its password alone. / Se quitan: vuelve a entrar solo con su contraseña.
            await s.RemovePhoneAsync();
            await s.SetEmailTwoFactorAsync(false);
            Assert.Null(s.User.Phone);
            Assert.False(s.User.HasEmailTwoFactor);
            await s.DisposeAsync();
            s = await Connect();

            var notPhone = await Assert.ThrowsAsync<MeshClientException>(() => s.StartPhoneAsync("123"));
            Assert.Equal("usuarios/cambiar-ajustes/dato-invalido", notPhone.BurburitoKey);
        }
        finally
        {
            // Whatever happened, the account is left without second factors for the other tests. / Se deja sin doble factor.
            try { await s.RemovePhoneAsync(); await s.SetEmailTwoFactorAsync(false); } catch (Exception) { }
            await s.DisposeAsync();
        }
    }
}
