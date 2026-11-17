using System.Net.Http;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Sharing the test device with a guest: a link is made, listed and its page opens on the server; removed, it is no longer
/// listed. The same as the Android app's.
/// Compartir el equipo de prueba con un invitado: el enlace se hace, se lista y su página abre en el servidor; quitado, ya no sale.
/// </summary>
[Trait("Category", "Integration")]
public class SharingIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    [Fact]
    public async Task A_guest_link_is_made_listed_and_removed()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var trust = new CertificateTrust(Env("MESHTEST_CERT_SHA256"));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), trust);
        await s.GetDeviceGroupsAsync();
        Device? d = null;
        for (var i = 0; i < 60 && d is null; i++) { d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline); if (d is null) await Task.Delay(500); }
        Assert.NotNull(d);
        Assert.True(s.CanShare(d!));

        var share = await s.ShareDeviceAsync(d!, "Invitado de prueba", ShareKinds.Desktop | ShareKinds.Terminal, TimeSpan.FromHours(1), askConsent: true);
        Assert.Contains("sharing?c=", share.Url);
        var listed = await s.GetDeviceSharesAsync(d!);
        var mine = Assert.Single(listed, x => x.PublicId == share.PublicId);
        Assert.Equal("Invitado de prueba", mine.GuestName);
        Assert.Equal(ShareKinds.Desktop | ShareKinds.Terminal, mine.Kinds);
        Assert.True(mine.AsksConsent);
        Assert.True(mine.Expires > DateTimeOffset.UtcNow.AddMinutes(50));

        // The guest's page opens (the test calls the server by its certificate name). / La página del invitado abre.
        using var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = (r, c, ch, e) => trust.Validate(r, c, ch, e) };
        using var http = new HttpClient(h);
        var page = await http.GetAsync(new Uri(a!.BaseUri, new Uri(share.Url!).PathAndQuery));
        Assert.True(page.IsSuccessStatusCode, $"the guest page answered {(int)page.StatusCode}");

        await s.RemoveDeviceShareAsync(d!, share);
        Assert.DoesNotContain(await s.GetDeviceSharesAsync(d!), x => x.PublicId == share.PublicId);

        var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.ShareDeviceAsync(d!, "", ShareKinds.Desktop, TimeSpan.FromHours(1), false));
        Assert.Equal("ficha/compartir/dato-invalido", bad.BurburitoKey);
    }
}
