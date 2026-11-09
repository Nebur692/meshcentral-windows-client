using System.Security.Cryptography;
using MeshCentralClient.Core;
using Xunit;

/// <summary>Files read only when asked for, against the real Linux agent of run-integration.sh. / Ficheros leídos solo al pedirlos, contra el agente Linux real.</summary>
[Trait("Category", "Integration")]
public class RemoteFileSourceTests
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

    private static async Task<string> Tree(MeshServerSession s, Device d, int bigSize)
    {
        var root = "/tmp/mcc-src-" + Guid.NewGuid().ToString("N")[..8];
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        await f.CreateFolderAsync(root);
        await f.CreateFolderAsync(root + "/sub");
        for (var i = 0; i < 40 && !(await f.ListAsync(root)).Entries.Any(e => e.Name == "sub"); i++) await Task.Delay(100);
        await f.UploadAsync(root, "a.txt", new MemoryStream("uno"u8.ToArray()));
        await f.UploadAsync(root + "/sub", "b.txt", new MemoryStream("dos"u8.ToArray()));
        if (bigSize > 0) await f.UploadAsync(root, "big.bin", new MemoryStream(Big(bigSize)));
        return root;
    }

    private static byte[] Big(int size) { var b = new byte[size]; new Random(7).NextBytes(b); return b; }

    private static byte[] All(Stream s)
    {
        using var m = new MemoryStream();
        var buffer = new byte[70_000];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) m.Write(buffer, 0, n);
        return m.ToArray();
    }

    [Fact]
    public async Task A_copied_folder_is_listed_parents_first_and_each_file_comes_when_read()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        var root = await Tree(s, d, 0);
        await using var source = new RemoteFileSource(s, d.Id);

        var entries = await source.ListAsync(new[] { root.Replace('/', '\\') });
        var name = root[(root.LastIndexOf('/') + 1)..];
        Assert.Equal(new[] { name, name + "\\a.txt", name + "\\sub", name + "\\sub\\b.txt" }, entries.Select(e => e.RelativePath).OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(entries[0].IsFolder);
        var a = entries.Single(e => e.RelativePath.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Equal(3, a.Size);
        Assert.False(a.IsFolder);
        Assert.NotNull(a.Modified);

        await using (var stream = source.Open(a)) Assert.Equal("uno"u8.ToArray(), All(stream));
        // The tunnel freed by the first file carries the second. / El túnel que libera el primero lleva el segundo.
        await using (var stream = source.Open(entries.Single(e => e.RelativePath.EndsWith("b.txt", StringComparison.Ordinal)))) Assert.Equal("dos"u8.ToArray(), All(stream));
    }

    [Fact]
    public async Task A_big_file_arrives_whole_though_the_reader_goes_slower_than_the_device()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        const int size = 12 * 1024 * 1024; // over what waits in memory, so the device has to pause / más de lo que espera en memoria, así que el equipo tiene que pararse
        var root = await Tree(s, d, size);
        await using var source = new RemoteFileSource(s, d.Id);
        var big = (await source.ListAsync(new[] { root + "/big.bin" })).Single();
        Assert.Equal(size, big.Size);
        await using var stream = source.Open(big);
        var first = new byte[1000];
        Assert.Equal(1000, stream.Read(first, 0, first.Length));
        await Task.Delay(1500);
        using var m = new MemoryStream();
        m.Write(first);
        m.Write(All(stream));
        Assert.Equal(SHA256.HashData(Big(size)), SHA256.HashData(m.ToArray()));
    }

    [Fact]
    public async Task A_paste_given_up_half_way_stops_the_transfer_and_the_next_file_still_comes()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        var root = await Tree(s, d, 12 * 1024 * 1024);
        await using var source = new RemoteFileSource(s, d.Id);
        var entries = await source.ListAsync(new[] { root + "/big.bin", root + "/a.txt" });
        var big = entries.Single(e => e.IsFolder == false && e.Size > 3);

        var abandoned = new RemoteFileStream(source, big.RemotePath, big.Size) { Abandoned = TimeSpan.FromMilliseconds(300) };
        var buffer = new byte[1000];
        Assert.Equal(1000, abandoned.Read(buffer, 0, buffer.Length));
        await Task.Delay(2000); // nobody reads: it stops by itself / nadie lee: se para solo
        // What it had waiting can still be read, then it says it was cut, not that the file ended. / Lo que tenía esperando aún se lee; luego dice que se cortó, no que acabó.
        Assert.ThrowsAny<OperationCanceledException>(() => All(abandoned));

        await using var next = source.Open(entries.Single(e => e.Size == 3));
        Assert.Equal("uno"u8.ToArray(), All(next));
        abandoned.Dispose();
    }

    [Fact]
    public async Task A_file_that_is_gone_when_pasting_fails_and_is_told()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var source = new RemoteFileSource(s, d.Id);
        MeshClientException? told = null;
        source.Failed += ex => told = ex;
        await using var stream = source.Open(new RemoteClipEntry("x.txt", "/tmp/mcc-no-existe-" + Guid.NewGuid().ToString("N"), false, 3, null));
        var ex = Assert.Throws<MeshClientException>(() => All(stream));
        Assert.Equal("ficheros/descargar/no-encontrado", ex.BurburitoKey);
        Assert.Same(ex, told);
    }
}
