using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MeshCentralClient.Core;
using MeshCentralClient.Core.Updates;
using Xunit;

public class UpdateFeedTests
{
    private static readonly Version Current = new(0, 2, 0, 0);

    // Field names checked against the real GitHub API (releases/latest). / Campos comprobados contra la API real de GitHub.
    private static string Release(string tag, bool draft = false, bool pre = false, bool exe = true, bool sha = true) => $$"""
        { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(pre ? "true" : "false")}},
          "html_url": "https://github.com/o/r/releases/tag/{{tag}}",
          "assets": [
            {{(exe ? "{ \"name\": \"MeshCentralClient.exe\", \"browser_download_url\": \"https://github.com/o/r/releases/download/" + tag + "/MeshCentralClient.exe\" }," : "")}}
            {{(sha ? "{ \"name\": \"MeshCentralClient.exe.sha256\", \"browser_download_url\": \"https://github.com/o/r/releases/download/" + tag + "/MeshCentralClient.exe.sha256\" }," : "")}}
            { "name": "other.zip", "browser_download_url": "https://github.com/o/r/releases/download/x/other.zip" }
          ] }
        """;

    [Theory]
    [InlineData("v0.3.0", true)]
    [InlineData("0.3.0", true)]
    [InlineData("v1.0", true)]
    [InlineData("v0.2.0", false)] // same version / la misma
    [InlineData("v0.1.9", false)] // older / más vieja
    public void Only_newer_versions_are_offered(string tag, bool offered)
    {
        var u = UpdateChecker.Parse(Release(tag), Current);
        Assert.Equal(offered, u is not null);
        if (u is not null) Assert.EndsWith("/MeshCentralClient.exe", u.ExeUrl.ToString());
    }

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(UpdateChecker.Parse(Release("v9.0.0", draft: true), Current));
        Assert.Null(UpdateChecker.Parse(Release("v9.0.0", pre: true), Current));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_release_without_exe_or_fingerprint_is_never_installed(bool exe, bool sha)
    {
        var e = Assert.Throws<MeshClientException>(() => UpdateChecker.Parse(Release("v9.0.0", exe: exe, sha: sha), Current));
        Assert.Equal("actualizar/desplegar/respuesta-ilegible", e.BurburitoKey);
    }

    [Theory]
    [InlineData("""{ "tag_name": "nightly", "assets": [] }""")]
    [InlineData("""{ "assets": [] }""")]
    [InlineData("not json")]
    public void Malformed_feeds_fail_with_their_code(string json) =>
        Assert.Equal("actualizar/desplegar/respuesta-ilegible", Assert.Throws<MeshClientException>(() => UpdateChecker.Parse(json, Current)).BurburitoKey);

    [Theory]
    [InlineData(null, "https://api.github.com/repos/Nebur692/meshcentral-windows-client/releases/latest")]
    [InlineData("", "https://api.github.com/repos/Nebur692/meshcentral-windows-client/releases/latest")]
    [InlineData("off", null)]
    [InlineData("OFF", null)]
    [InlineData("https://example.com/feed.json", "https://example.com/feed.json")]
    [InlineData("http://127.0.0.1:8099/latest.json", "http://127.0.0.1:8099/latest.json")]
    [InlineData("http://example.com/feed.json", "https://api.github.com/repos/Nebur692/meshcentral-windows-client/releases/latest")] // plain http outside loopback / http sin cifrar fuera de local
    [InlineData("file:///c:/feed.json", "https://api.github.com/repos/Nebur692/meshcentral-windows-client/releases/latest")]
    public void Feed_override_is_restricted(string? value, string? expected) =>
        Assert.Equal(expected, UpdateChecker.FeedFromEnvironment(value)?.ToString());
}

/// <summary>
/// One real local HTTP server for all the download tests, on a port the system says is free (and another one if it was
/// taken in between): a random port failed now and then on Windows.
/// Un único servidor HTTP local de verdad para todas las pruebas de descarga, en un puerto que el sistema da por libre
/// (y otro si lo ocuparon entretanto): un puerto al azar fallaba de vez en cuando en Windows.
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    public Dictionary<string, (int Status, byte[] Body)> Routes { get; } = new();
    public string Base { get; }

    public LocalHttpServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); _listener = l; Base = $"http://127.0.0.1:{port}/"; break; }
            catch (HttpListenerException) when (attempt < 10) { l.Close(); }
        }
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch (Exception) { return; }
                (int, byte[]) r;
                lock (Routes) r = Routes.TryGetValue(c.Request.Url!.AbsolutePath, out var x) ? x : (404, Array.Empty<byte>());
                c.Response.StatusCode = r.Item1;
                c.Response.ContentLength64 = r.Item2.Length;
                try { await c.Response.OutputStream.WriteAsync(r.Item2); c.Response.Close(); } catch (Exception) { }
            }
        });
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

/// <summary>Download, check and swap against a real local HTTP server. / Descargar, comprobar y cambiar contra un servidor HTTP local de verdad.</summary>
public sealed class SelfUpdaterTests : IDisposable, IClassFixture<LocalHttpServer>
{
    private readonly LocalHttpServer _server;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-update-");
    private readonly HttpClient _http = new();
    private readonly string _exe;
    private static readonly byte[] OldExe = Encoding.ASCII.GetBytes("old exe contents");
    private static readonly byte[] NewExe = RandomNumberGenerator.GetBytes(300_000);

    public SelfUpdaterTests(LocalHttpServer server)
    {
        _server = server;
        _exe = Path.Combine(_dir.FullName, "MeshCentralClient.exe");
        File.WriteAllBytes(_exe, OldExe);
    }

    public void Dispose() { _http.Dispose(); _dir.Delete(true); }

    private AvailableUpdate Serve(string shaText, int exeStatus = 200)
    {
        lock (_server.Routes)
        {
            _server.Routes["/MeshCentralClient.exe"] = (exeStatus, NewExe);
            _server.Routes["/MeshCentralClient.exe.sha256"] = (200, Encoding.ASCII.GetBytes(shaText));
        }
        return new AvailableUpdate(new Version(9, 0, 0), "v9.0.0", new Uri(_server.Base + "MeshCentralClient.exe"), new Uri(_server.Base + "MeshCentralClient.exe.sha256"), null);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_checks_and_swaps()
    {
        var u = Serve(Sha(NewExe) + "  MeshCentralClient.exe\n"); // sha256sum format / formato de sha256sum
        var progress = new List<double>();
        var path = await SelfUpdater.DownloadAsync(_http, u, _exe, new Progress<double>(progress.Add));
        Assert.Equal(SelfUpdater.NewPath(_exe), path);
        Assert.Equal(OldExe, File.ReadAllBytes(_exe)); // nothing touched yet / aún no se ha tocado nada

        SelfUpdater.Swap(_exe, path);
        Assert.Equal(NewExe, File.ReadAllBytes(_exe));
        Assert.Equal(OldExe, File.ReadAllBytes(SelfUpdater.OldPath(_exe)));
        Assert.False(File.Exists(path));

        await SelfUpdater.CleanUpAsync(_exe, attempts: 1);
        Assert.False(File.Exists(SelfUpdater.OldPath(_exe)));
        Assert.Equal(NewExe, File.ReadAllBytes(_exe));
    }

    /// <summary>MENTIRA: a file that does not match its fingerprint is never installed.
    /// Un fichero que no coincide con su huella no se instala nunca.</summary>
    [Fact]
    public async Task Lie_a_wrong_fingerprint_changes_nothing()
    {
        var u = Serve(Sha(OldExe));
        var e = await Assert.ThrowsAsync<MeshClientException>(() => SelfUpdater.DownloadAsync(_http, u, _exe));
        Assert.Equal("actualizar/desplegar/dato-corrupto", e.BurburitoKey);
        Assert.Equal(OldExe, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(SelfUpdater.NewPath(_exe)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("ABCD")]
    public async Task A_fingerprint_file_that_is_not_sha256_is_refused(string text)
    {
        var e = await Assert.ThrowsAsync<MeshClientException>(() => SelfUpdater.DownloadAsync(_http, Serve(text), _exe));
        Assert.Equal("actualizar/desplegar/respuesta-ilegible", e.BurburitoKey);
        Assert.Equal(OldExe, File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task A_failed_download_leaves_no_partial_file()
    {
        var e = await Assert.ThrowsAsync<MeshClientException>(() => SelfUpdater.DownloadAsync(_http, Serve(Sha(NewExe), exeStatus: 500), _exe));
        Assert.Equal("actualizar/desplegar/sin-conexion", e.BurburitoKey);
        Assert.False(File.Exists(SelfUpdater.NewPath(_exe)));
        Assert.Equal(OldExe, File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task Clean_up_never_removes_the_only_copy()
    {
        File.Move(_exe, SelfUpdater.OldPath(_exe)); // as if a swap failed half-way / como si un cambio fallara a medias
        await SelfUpdater.CleanUpAsync(_exe, attempts: 1);
        Assert.True(File.Exists(SelfUpdater.OldPath(_exe)));
    }

    [Fact]
    public void Swap_without_a_download_changes_nothing()
    {
        Assert.Throws<FileNotFoundException>(() => SelfUpdater.Swap(_exe, SelfUpdater.NewPath(_exe)));
        Assert.Equal(OldExe, File.ReadAllBytes(_exe));
    }
}
