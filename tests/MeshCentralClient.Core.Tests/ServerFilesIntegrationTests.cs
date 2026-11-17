using System.Text;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// The server's own files ("My Files"): a folder is made, a file goes up and comes back the same, is renamed and deleted,
/// each change showing in the tree; an account without the right is refused before asking the server.
/// Los ficheros del propio servidor: se crea una carpeta, se sube un fichero y vuelve igual, se renombra y se borra, y cada
/// cambio sale en el árbol; una cuenta sin el permiso se rechaza sin preguntar al servidor.
/// </summary>
[Trait("Category", "Integration")]
public class ServerFilesIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> Open(string user, string pass)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new(user, pass), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
    }

    [Fact]
    public async Task A_file_goes_up_comes_back_is_renamed_and_deleted()
    {
        await using var s = await Open("admin", "Admin-Pass-1234");
        Assert.True(s.CanUseServerFiles);
        var mine = (await s.GetServerFilesAsync()).First(r => !r.IsGroup);
        Assert.Equal(s.User.Id, mine.Id);

        var folder = "prueba-" + Guid.NewGuid().ToString("N")[..8];
        mine = (await s.CreateServerFolderAsync(mine, [], folder)).First(r => r.Id == mine.Id);
        Assert.NotNull(mine.Find([folder]));

        var data = Encoding.UTF8.GetBytes("¡Hola desde la prueba! " + new string('x', 5000));
        mine = (await s.UploadServerFileAsync(mine, [folder], "nota.txt", data)).First(r => r.Id == mine.Id);
        var file = Assert.Single(mine.Find([folder])!);
        Assert.Equal(data.Length, file.Size);
        Assert.Equal(data, await s.DownloadServerFileAsync(mine, [folder], file));

        mine = (await s.RenameServerFileAsync(mine, [folder], "nota.txt", "otra.txt")).First(r => r.Id == mine.Id);
        Assert.Equal("otra.txt", Assert.Single(mine.Find([folder])!).Name);

        var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.CreateServerFolderAsync(mine, [folder], "a/b"));
        Assert.Equal("ficheros-servidor/gestionar-ficheros/dato-invalido", bad.BurburitoKey);
        var big = await Assert.ThrowsAsync<MeshClientException>(() => s.DownloadServerFileAsync(mine, [folder], file with { Size = 300_000 }));
        Assert.Equal("ficheros-servidor/descargar/no-implementado", big.BurburitoKey);

        mine = (await s.DeleteServerFilesAsync(mine, [], [folder])).First(r => r.Id == mine.Id);
        Assert.Null(mine.Find([folder]));
    }

    [Fact]
    public async Task An_account_without_the_right_is_refused()
    {
        await using var s = await Open("user", "User-Pass-1234");
        Assert.False(s.CanUseServerFiles);
        var e = await Assert.ThrowsAsync<MeshClientException>(() => s.GetServerFilesAsync());
        Assert.Equal("ficheros-servidor/ver-pantalla/no-autorizado", e.BurburitoKey);
    }
}
