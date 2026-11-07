using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace MeshCentralClient.App;

/// <summary>
/// The embedded web pages, written once per version to %LOCALAPPDATA%\MeshCentralClient\web\&lt;hash&gt; and served
/// to WebView2 under https://app.local/, plus the shared WebView2 environment.
/// Las páginas web incrustadas, escritas una vez por versión en %LOCALAPPDATA%\MeshCentralClient\web\&lt;huella&gt; y
/// servidas a WebView2 en https://app.local/, y el entorno compartido de WebView2.
/// </summary>
public static class WebAssets
{
    public const string Host = "app.local";
    private static readonly string Root = AppPaths.Local;
    private static readonly Lazy<string> Folder = new(Extract);
    private static Task<CoreWebView2Environment>? _env;

    public static string FolderPath => Folder.Value;

    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Root, "WebView2"));

    /// <summary>Prepares a WebView2 to show our pages. / Prepara un WebView2 para enseñar nuestras páginas.</summary>
    public static async Task InitAsync(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        await view.EnsureCoreWebView2Async(await EnvironmentAsync());
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(Host, FolderPath, CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // Our pages never leave app.local. / Nuestras páginas nunca salen de app.local.
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
    }

    private static string Extract()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blobs = new Dictionary<string, byte[]>();
        foreach (var n in names)
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            blobs[n] = ms.ToArray();
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(n));
            sha.AppendData(blobs[n]);
        }
        var dir = Path.Combine(Root, "web", Convert.ToHexString(sha.GetHashAndReset())[..16]);
        if (!File.Exists(Path.Combine(dir, ".complete")))
        {
            foreach (var (n, b) in blobs)
            {
                var path = Path.Combine(dir, n["web/".Length..].Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)); // RecursiveDir uses backslashes on Windows / RecursiveDir usa barras invertidas en Windows
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, b);
            }
            File.WriteAllText(Path.Combine(dir, ".complete"), "");
        }
        return dir;
    }
}
