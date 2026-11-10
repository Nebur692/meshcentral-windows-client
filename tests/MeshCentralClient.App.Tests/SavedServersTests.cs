using System.IO;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

public sealed class SavedServersTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-saved-");
    private string File(string name) => Path.Combine(_dir.FullName, name);
    public void Dispose() => _dir.Delete(true);

    [Fact]
    public void Missing_file_is_an_empty_list()
    {
        var s = new SavedServers(_dir.FullName);
        Assert.Null(s.Load());
        Assert.Empty(s.Items);
    }

    [Fact]
    public void Saves_and_loads_with_the_password_encrypted()
    {
        var s = new SavedServers(_dir.FullName);
        var e = s.Upsert("https://remote.example.com/", "alice");
        e.WritePassword("p4ss-wörd");
        e.CertificateSha256 = new string('A', 64);
        Assert.Null(s.Save());

        // With the dash, which base64 never has, so the ciphertext cannot contain it by chance (it did: "...Tp4ss...").
        // Con el guion, que el base64 nunca lleva, para que el cifrado no lo contenga por azar (pasó: «...Tp4ss...»).
        Assert.DoesNotContain("p4ss-w", System.IO.File.ReadAllText(File("servers.json")));
        var again = new SavedServers(_dir.FullName);
        Assert.Null(again.Load());
        var only = Assert.Single(again.Items);
        Assert.Equal("p4ss-wörd", only.ReadPassword());
        Assert.Equal(new string('A', 64), again.PinFor("HTTPS://REMOTE.EXAMPLE.COM/"));
    }

    [Fact]
    public void Upsert_is_case_insensitive_and_does_not_duplicate()
    {
        var s = new SavedServers(_dir.FullName);
        s.Upsert("https://a/", "Bob");
        s.Upsert("https://A/", "bob");
        Assert.Single(s.Items);
    }

    [Fact]
    public void No_password_when_not_remembered()
    {
        var e = new SavedServer();
        e.WritePassword(null);
        Assert.Null(e.ProtectedPassword);
        Assert.Null(e.ReadPassword());
    }

    [Fact]
    public void A_password_from_another_machine_reads_as_none() =>
        Assert.Null(new SavedServer { ProtectedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }.ReadPassword());

    [Fact]
    public void Corrupt_file_is_set_aside_not_deleted()
    {
        System.IO.File.WriteAllText(File("servers.json"), "{ not json");
        var s = new SavedServers(_dir.FullName);
        var problem = s.Load();
        Assert.NotNull(problem);
        Assert.EndsWith(Burburito.Code("ajustes/cambiar-ajustes/dato-corrupto"), problem!.Footer);
        Assert.Empty(s.Items);
        Assert.Equal("{ not json", System.IO.File.ReadAllText(File("servers.json.broken")));
        Assert.False(System.IO.File.Exists(File("servers.json")));
    }
}
