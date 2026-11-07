using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Single sign-on against the real test MeshCentral and the throwaway OpenID Connect provider of tests/oidc.
/// Inicio de sesión único contra el MeshCentral de pruebas real y el proveedor OpenID Connect desechable de tests/oidc.
/// </summary>
[Trait("Category", "Integration")]
public class OidcIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }
    private static CertificateTrust Pinned => new(Env("MESHTEST_CERT_SHA256"));

    private async Task<IReadOnlyDictionary<string, string>> SignIn(string user)
    {
        using var browser = new OidcBrowser(Env("MESHTEST_CERT_SHA256"));
        var sso = Assert.Single(await SignInMethods.DiscoverAsync(Server, Pinned));
        var (url, _) = await browser.SignInAsync(new Uri(Server.BaseUri, sso.Path), user);
        output.WriteLine($"{user}: ended at {url.GetLeftPart(UriPartial.Path)}");
        return browser.CookiesFor(Server.BaseUri.Authority);
    }

    [Fact]
    public async Task The_server_offers_its_single_sign_on()
    {
        var methods = await SignInMethods.DiscoverAsync(Server, Pinned);
        var m = Assert.Single(methods);
        Assert.Equal("oidc", m.Strategy);
        Assert.Equal("auth-oidc", m.Path);
    }

    [Fact]
    public async Task Without_trusting_the_certificate_nothing_is_offered() =>
        Assert.Empty(await SignInMethods.DiscoverAsync(Server, new CertificateTrust()));

    [Fact]
    public async Task A_person_with_access_signs_in_and_sees_the_server()
    {
        var cookies = await SignIn("alice");
        Assert.Contains("xid", cookies.Keys);
        await using var s = await MeshServerSession.ConnectWithSessionCookiesAsync(Server, cookies, Pinned);
        output.WriteLine($"{s.User.Id} {s.User.Name}");
        Assert.Contains("~oidc:", s.User.Id);
        Assert.Contains("alice", s.User.Name);
        Assert.NotNull(await s.GetDeviceGroupsAsync()); // the session really works / la sesión funciona de verdad
    }

    [Fact]
    public async Task A_person_the_server_refuses_gets_a_cookie_but_no_session()
    {
        var cookies = await SignIn("denied");
        Assert.Contains("xid", cookies.Keys); // MeshCentral gives one anyway / MeshCentral la da igualmente
        var e = await Assert.ThrowsAsync<MeshClientException>(() => MeshServerSession.ConnectWithSessionCookiesAsync(Server, cookies, Pinned));
        Assert.Equal(MeshFailure.SessionExpired, e.Failure);
        Assert.Equal("conexion/iniciar-sesion/sesion-cerrada", e.BurburitoKey);
    }

    /// <summary>MENTIRA: an invented session cookie must not open anything.
    /// Una cookie de sesión inventada no puede abrir nada.</summary>
    [Fact]
    public async Task Lie_an_invented_cookie_is_refused()
    {
        var real = await SignIn("bob");
        var forged = real.ToDictionary(kv => kv.Key, kv => kv.Key == "xid" ? "eyJ1c2VyaWQiOiJ1c2VyLy9hZG1pbiJ9" : kv.Value); // {"userid":"user//admin"}
        var e = await Assert.ThrowsAsync<MeshClientException>(() => MeshServerSession.ConnectWithSessionCookiesAsync(Server, forged, Pinned));
        Assert.Equal(MeshFailure.SessionExpired, e.Failure);
        var none = await Assert.ThrowsAsync<MeshClientException>(() => MeshServerSession.ConnectWithSessionCookiesAsync(Server, new Dictionary<string, string>(), Pinned));
        Assert.Equal(MeshFailure.SessionExpired, none.Failure);
    }

    [Fact]
    public async Task Password_sign_in_still_works_on_a_server_with_single_sign_on()
    {
        await using var s = await MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), Pinned);
        Assert.Equal("user//admin", s.User.Id);
    }
}
