using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Two-factor by messaging through ntfy, against the bench: the bench's mailbox is also an ntfy server (HTTPS, its certificate
/// trusted only by the test server), where MeshCentral POSTs the codes. The handle is what the Android app sets with its own
/// topic; this app signs in asking the server to send the code there (to the phone), and with the code that arrives. Uses the
/// "mfa" account. The same as the Android app's MessagingTwoFactorIntegrationTest.
/// Doble factor por mensajería con ntfy, contra el banco: el buzón es también un servidor ntfy. El canal lo pone la app de
/// Android con su tema; esta app entra pidiendo que el código vaya allí (al móvil), y con el código que llega.
/// </summary>
[Trait("Category", "Integration")]
[Collection("mfa")]
public class MessagingTwoFactorIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }

    private static Task<MeshServerSession> Connect(string? code = null) =>
        MeshServerSession.ConnectAsync(Server, new("mfa", "Mfa-Pass-1234", code), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));

    private static readonly HttpClient Http = new();

    private static async Task<List<string>> Pushes(string topic) =>
        ((JsonArray)((JsonObject)JsonNode.Parse(await Http.GetStringAsync(Env("MESHTEST_SINK") + "/read"))!)["ntfy"]!)
            .OfType<JsonObject>().Where(p => p["topic"]!.GetValue<string>() == topic).Select(p => p["msg"]!.GetValue<string>()).ToList();

    /// <summary>The code of the newest push to the topic after <paramref name="since"/> of them. / El código del último aviso.</summary>
    private static async Task<string> Code(string topic, int since)
    {
        for (var i = 0; i < 40; i++)
        {
            var list = await Pushes(topic);
            if (list.Count > since)
            {
                var m = Regex.Match(list[^1], @"(?<!\d)(\d{6})(?!\d)");
                Assert.True(m.Success, "no code in: " + list[^1]);
                return m.Groups[1].Value;
            }
            await Task.Delay(250);
        }
        throw new Exception("nothing arrived at " + topic);
    }

    [Fact]
    public async Task An_ntfy_topic_is_checked_and_its_codes_sign_in()
    {
        var topic = "up" + DateTime.UtcNow.Ticks.ToString("x");
        var s = await Connect();
        try
        {
            Assert.True(s.ServerInfo.MessagingTwoFactorAllowed, "the bench's server offers messaging codes");

            // The handle: a wrong code is refused, the one pushed to the topic is taken. / El canal: el malo no, el del aviso sí.
            var check = await s.StartNtfyAsync(topic);
            var good = await Code(topic, 0);
            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.ConfirmMessagingAsync(check, ((int.Parse(good) + 1) % 1_000_000).ToString("D6")));
            Assert.Equal("usuarios/cambiar-ajustes/credencial-invalida", bad.BurburitoKey);
            Assert.Null(s.User.MessagingHandle);
            var again = await s.StartNtfyAsync(topic);
            await s.ConfirmMessagingAsync(again, await Code(topic, 1));
            Assert.Equal("ntfy:" + topic, s.User.MessagingHandle);

            // Signing in: offered, asked for (that is not a wrong code), pushed, and it lets in.
            // Al entrar: se ofrece, se pide (pedirlo no es un código malo), llega al tema y deja entrar.
            await s.DisposeAsync();
            var asked = await Assert.ThrowsAsync<MeshClientException>(() => Connect());
            Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);
            Assert.True(asked.MessagingCodeAvailable, "the server offers the messaging code");
            var before = (await Pushes(topic)).Count;
            var sent = await Assert.ThrowsAsync<MeshClientException>(() => Connect("**msg**"));
            Assert.Equal(MeshFailure.TwoFactorRequired, sent.Failure);
            Assert.True(sent.MessagingCodeSent, "the server says it sent it");
            s = await Connect(await Code(topic, before));

            // Off again: the password alone signs in. / Se quita: vuelve a entrar solo con la contraseña.
            await s.RemoveMessagingAsync();
            Assert.Null(s.User.MessagingHandle);
            await s.DisposeAsync();
            s = await Connect();

            var notTopic = await Assert.ThrowsAsync<MeshClientException>(() => s.StartNtfyAsync("with spaces"));
            Assert.Equal("usuarios/cambiar-ajustes/dato-invalido", notTopic.BurburitoKey);
        }
        finally
        {
            try { await s.RemoveMessagingAsync(); } catch (Exception) { await CleanUp(topic); }
            await s.DisposeAsync();
        }
    }

    /// <summary>A test that failed halfway leaves the account with the handle and its session closed: sign in again with the
    /// topic's code and remove it, so the other tests of the "mfa" account do not fail because of this one.
    /// Una prueba que falla a medias deja el canal puesto: se vuelve a entrar con el código del tema y se quita.</summary>
    private static async Task CleanUp(string topic)
    {
        try
        {
            await (await Connect()).DisposeAsync();
            return;
        }
        catch (MeshClientException e) when (e.Failure == MeshFailure.TwoFactorRequired && e.MessagingCodeAvailable)
        {
            var before = (await Pushes(topic)).Count;
            try { await (await Connect("**msg**")).DisposeAsync(); } catch (MeshClientException) { }
            var s = await Connect(await Code(topic, before));
            await s.RemoveMessagingAsync();
            await s.DisposeAsync();
        }
    }
}
