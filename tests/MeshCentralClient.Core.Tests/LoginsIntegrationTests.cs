using System.Net.Http;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// A failed and a good sign-in through the website show in the account's previous logins, newest first. MeshCentral logs
/// only sign-ins through its web pages (form, single sign-on, keys), not the control channel the apps use with a password.
/// Una entrada fallida y otra buena por la web salen en el historial. MeshCentral solo apunta las de sus páginas web.
/// </summary>
[Trait("Category", "Integration")]
public class LoginsIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    [Fact]
    public async Task Failed_and_good_web_sign_ins_show_in_the_history()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var trust = new CertificateTrust(Env("MESHTEST_CERT_SHA256"));
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        async Task Form(string password)
        {
            using var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = (r, c, ch, e) => trust.Validate(r, c, ch, e) };
            using var http = new HttpClient(h);
            (await http.PostAsync(a!.BaseUri, new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "login", ["username"] = "user", ["password"] = password }))).Dispose();
        }
        await Form("Not-The-Pass-1");
        await Form("User-Pass-1234");
        await using var s = await MeshServerSession.ConnectAsync(a!, new("user", "User-Pass-1234"), trust);
        var logins = await s.GetPreviousLoginsAsync();
        Assert.Contains(logins, l => l.Kind == LoginKind.WrongPassword && l.Time >= started);
        var good = logins.First(l => l.Kind == LoginKind.Valid && l.Time >= started);
        Assert.NotEmpty(good.Details);   // its address and browser / su dirección y navegador
        Assert.True(logins.SequenceEqual(logins.OrderByDescending(l => l.Time)));
    }
}
