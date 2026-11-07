using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Whole trees against the real Linux agent: upload a local folder, copy and move it on the device, download it back
/// and compare every byte with the original.
/// Árboles enteros contra el agente Linux real: subir una carpeta local, copiarla y moverla en el equipo, bajarla otra
/// vez y comparar cada byte con el original.
/// </summary>
[Trait("Category", "Integration")]
public class FilesTreeIntegrationTests : IDisposable
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private readonly DirectoryInfo _local = Directory.CreateTempSubdirectory("mcc-tree-");

    public void Dispose() { try { _local.Delete(true); } catch (Exception) { } }

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

    /// <summary>Every file and folder under <paramref name="root"/>, with its bytes. / Cada fichero y carpeta, con sus bytes.</summary>
    private static SortedDictionary<string, string> Tree(string root) => new(
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(root, p).Replace('\\', '/'),
            p => Directory.Exists(p) ? "<dir>" : Convert.ToHexString(File.ReadAllBytes(p))), StringComparer.Ordinal);

    private string MakeSource()
    {
        var src = Directory.CreateDirectory(Path.Combine(_local.FullName, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "{ starts like JSON");
        Directory.CreateDirectory(Path.Combine(src, "sub", "deeper"));
        var big = new byte[70_000];
        new Random(3).NextBytes(big);
        big[0] = 0; // the other escape case / el otro caso del escape
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), big);
        File.WriteAllText(Path.Combine(src, "sub", "deeper", "c.txt"), "ñandú €");
        File.WriteAllBytes(Path.Combine(src, "sub", "empty.txt"), []);
        Directory.CreateDirectory(Path.Combine(src, "vacia"));
        return src;
    }

    [Fact]
    public async Task A_folder_goes_up_is_copied_and_moved_on_the_device_and_comes_back_byte_for_byte()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var f = await FilesSession.OpenAsync(s, d.Id);
        var src = MakeSource();
        var root = "/tmp/mcc-tree-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await f.CreateFolderAsync(root);
            await f.WaitForAsync("/tmp", l => l.Entries.Any(e => "/tmp/" + e.Name == root), "create the root");
            foreach (var n in new[] { "up", "copy", "moved" }) await f.CreateFolderAsync(root + "/" + n);
            await f.WaitForAsync(root, l => l.Entries.Count == 3, "create the three folders");

            // Up: the whole local folder. / Subir: la carpeta local entera.
            await f.UploadTreeAsync([(src, "src")], root + "/up");

            // Copy on the device, folders included. / Copiar en el equipo, carpetas incluidas.
            var up = await f.ListAsync(root + "/up");
            await f.CopyTreeAsync(root + "/up", up.Entries, root + "/copy");

            // Down again, and every byte compared. / Bajar otra vez y comparar cada byte.
            var back = Directory.CreateDirectory(Path.Combine(_local.FullName, "back")).FullName;
            var copied = await f.ListAsync(root + "/copy");
            await f.DownloadTreeAsync(root + "/copy", copied.Entries, back);
            Assert.Equal(Tree(src), Tree(Path.Combine(back, "src")));
            Assert.Empty(Directory.EnumerateFiles(back, "*.part", SearchOption.AllDirectories));

            // Move: it is in the new place, whole, and no longer in the old one. / Mover: está entero en el sitio nuevo y ya no en el viejo.
            var sub = (await f.ListAsync(root + "/copy/src")).Entries.Where(e => e.Name == "sub").ToList();
            await f.MoveTreeAsync(root + "/copy/src", sub, root + "/moved");
            Assert.DoesNotContain((await f.ListAsync(root + "/copy/src")).Entries, e => e.Name == "sub");
            var again = Directory.CreateDirectory(Path.Combine(_local.FullName, "again")).FullName;
            await f.DownloadTreeAsync(root + "/moved", (await f.ListAsync(root + "/moved")).Entries, again);
            Assert.Equal(Tree(Path.Combine(src, "sub")), Tree(Path.Combine(again, "sub")));

            // Lie: a folder copied inside itself would never end; it is refused before anything is sent.
            // Mentira: una carpeta copiada dentro de sí misma no acabaría nunca; se rechaza antes de mandar nada.
            var self = (await f.ListAsync(root + "/up")).Entries.Where(e => e.Name == "src").ToList();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.CopyTreeAsync(root + "/up", self, root + "/up/src/sub"));
        }
        finally
        {
            await f.DeleteAsync("/tmp", [root[5..]], recursive: true);
            await f.WaitForAsync("/tmp", l => l.Entries.All(e => "/tmp/" + e.Name != root), "clean up");
        }
    }
}

/// <summary>Path rules of the device side. / Reglas de rutas del lado del equipo.</summary>
public class RemotePathTests
{
    [Theory]
    [InlineData("", "C:\\", "C:/")]
    [InlineData("/", "tmp", "/tmp")]
    [InlineData("/tmp/", "x", "/tmp/x")]
    [InlineData("C:/Users", "Ana", "C:/Users/Ana")]
    public void Join(string folder, string name, string expected) => Assert.Equal(expected, FilesSession.JoinPath(folder, name));

    [Theory]
    [InlineData("/a/b", "/a", true)]
    [InlineData("/a", "/a", true)]
    [InlineData("/ab", "/a", false)]      // a name that only starts the same / un nombre que solo empieza igual
    [InlineData("C:\\Users\\x", "c:/users", true)]
    [InlineData("/a", "/a/b", false)]
    public void Inside(string target, string folder, bool expected) => Assert.Equal(expected, FilesSession.IsInside(target, folder));
}
