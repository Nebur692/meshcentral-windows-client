using System.Security.Cryptography;
using MeshCentralClient.Core;
using Xunit;

/// <summary>The Files tunnel against the real Linux agent of run-integration.sh. / El túnel de ficheros contra el agente Linux real.</summary>
[Trait("Category", "Integration")]
public class FilesIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<(MeshServerSession S, Device D)> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return (s, d);
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online");
    }

    private static string Folder() => "/tmp/mcc-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<RemoteFolder> WaitList(FilesSession f, string path, Func<RemoteFolder, bool> until)
    {
        // The agent does not answer mkdir/rename/rm: poll the listing (condition, not a fixed sleep).
        // El agente no contesta a mkdir/rename/rm: se mira el listado (condición, no un tiempo fijo).
        RemoteFolder last = await f.ListAsync(path);
        for (var i = 0; i < 40 && !until(last); i++) { await Task.Delay(100); last = await f.ListAsync(path); }
        return last;
    }

    [Fact]
    public async Task Lists_the_root_and_tells_folders_from_files()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var root = await f.ListAsync("/");
        Assert.Contains(root.Entries, e => e.Name == "etc" && e.Kind == RemoteEntryKind.Folder);
        var etc = await f.ListAsync("/etc");
        var hostname = Assert.Single(etc.Entries, e => e.Name == "hostname");
        Assert.Equal(RemoteEntryKind.File, hostname.Kind);
        Assert.True(hostname.Size > 0);
        Assert.NotNull(hostname.Modified);
    }

    [Theory]
    [InlineData("big.bin", 1_500_000, 0)]     // many blocks both ways / muchos bloques en las dos direcciones
    [InlineData("empty.txt", 0, 0)]           // no block at all / ningún bloque
    [InlineData("brace.json", 70_000, 1)]     // starts with '{': needs the escape byte / empieza por «{»: necesita el byte de escape
    [InlineData("zero.bin", 70_000, 2)]       // starts with 0: needs the escape byte / empieza por 0: necesita el byte de escape
    public async Task Upload_and_download_give_back_the_same_bytes(string name, int size, int start)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        if (start == 1 && size > 0) { data[0] = (byte)'{'; data[65564] = (byte)'{'; } // also the 2nd block / también el 2.º bloque
        if (start == 2 && size > 0) { data[0] = 0; data[65564] = 0; }
        if (start == 0 && size > 0) data[0] = 0x42;

        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var dir = Folder();
        await f.CreateFolderAsync(dir);
        await WaitList(f, "/tmp", l => l.Entries.Any(e => "/tmp/" + e.Name == dir));
        try
        {
            var up = new List<long>();
            await f.UploadAsync(dir, name, new MemoryStream(data), new InlineProgress<long>(up.Add));
            var listed = Assert.Single((await f.ListAsync(dir)).Entries, e => e.Name == name);
            Assert.Equal(size, listed.Size);

            var back = new MemoryStream();
            await f.DownloadAsync(dir + "/" + name, back);
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(back.ToArray()));
        }
        finally { await f.DeleteAsync("/tmp", new[] { dir[5..] }, recursive: true); }
    }

    [Fact]
    public async Task Create_rename_and_delete()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var dir = Folder();
        await f.CreateFolderAsync(dir);
        await f.CreateFolderAsync(dir + "/inner");
        Assert.Contains((await WaitList(f, dir, l => l.Entries.Any(e => e.Name == "inner"))).Entries, e => e.Name == "inner" && e.Kind == RemoteEntryKind.Folder);

        await f.RenameAsync(dir, "inner", "renamed");
        var after = await WaitList(f, dir, l => l.Entries.Any(e => e.Name == "renamed"));
        Assert.Contains(after.Entries, e => e.Name == "renamed");
        Assert.DoesNotContain(after.Entries, e => e.Name == "inner");

        await f.UploadAsync(dir + "/renamed", "x.txt", new MemoryStream(new byte[] { 1, 2, 3 }));
        await f.DeleteAsync("/tmp", new[] { dir[5..] }, recursive: true);
        Assert.DoesNotContain((await WaitList(f, "/tmp", l => l.Entries.All(e => "/tmp/" + e.Name != dir))).Entries, e => "/tmp/" + e.Name == dir);
    }

    [Fact]
    public async Task What_does_not_exist_fails_with_its_code()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var missing = "/no/such/" + Guid.NewGuid().ToString("N");
        Assert.Equal("ficheros/ver-pantalla/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => f.ListAsync(missing))).BurburitoKey);
        Assert.Equal("ficheros/descargar/no-encontrado", (await Assert.ThrowsAsync<MeshClientException>(() => f.DownloadAsync(missing + "/x", new MemoryStream()))).BurburitoKey);
        Assert.Equal("ficheros/subir/rechazo-remoto", (await Assert.ThrowsAsync<MeshClientException>(() => f.UploadAsync(missing, "x", new MemoryStream(new byte[10])))).BurburitoKey);
        // The session is still usable after each failure. / La sesión sigue sirviendo tras cada fallo.
        Assert.NotEmpty((await f.ListAsync("/")).Entries);
    }

    [Fact]
    public async Task A_cancelled_download_leaves_the_session_usable()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var dir = Folder();
        await f.CreateFolderAsync(dir);
        await WaitList(f, "/tmp", l => l.Entries.Any(e => "/tmp/" + e.Name == dir));
        try
        {
            await f.UploadAsync(dir, "big.bin", new MemoryStream(RandomNumberGenerator.GetBytes(2_000_000)));
            using var cts = new CancellationTokenSource();
            var sink = new MemoryStream();
            var p = new Progress<long>(n => { if (n > 100_000) cts.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.DownloadAsync(dir + "/big.bin", sink, p, cts.Token));
            Assert.True(sink.Length < 2_000_000);
            // The next operation is not confused by late blocks. / La siguiente operación no se lía con bloques tardíos.
            Assert.Contains((await f.ListAsync(dir)).Entries, e => e.Name == "big.bin" && e.Size == 2_000_000);
            var whole = new MemoryStream();
            await f.DownloadAsync(dir + "/big.bin", whole);
            Assert.Equal(2_000_000, whole.Length);
        }
        finally { await f.DeleteAsync("/tmp", new[] { dir[5..] }, recursive: true); }
    }

    [Fact]
    public async Task A_user_without_rights_cannot_open_the_files()
    {
        var (admin, dev) = await Admin();
        await using var _ = admin;
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var user = await MeshServerSession.ConnectAsync(a!, new("user", "User-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        var e = await Assert.ThrowsAsync<MeshClientException>(() => FilesSession.OpenAsync(user, dev.Id));
        Assert.StartsWith("tunel/abrir-detalle/", e.BurburitoKey);
    }
}
