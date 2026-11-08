using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>What a clipboard holds, as far as the app moves it. / Lo que tiene un portapapeles, en lo que la app mueve.</summary>
public enum ClipKind { None, Text, Image, Files, Error }

/// <summary>
/// The device's clipboard as its PowerShell read it. <see cref="Signature"/> tells two readings apart without moving
/// anything. / El portapapeles del equipo tal como lo leyó su PowerShell; la firma distingue dos lecturas sin mover nada.
/// </summary>
public sealed record RemoteClip(ClipKind Kind, string? Text, IReadOnlyList<string> Files, string? ImagePath, string? ImageHash, long Size, bool SizeOverLimit, string? Temp, string? Error)
{
    public string Signature => Kind switch
    {
        ClipKind.Text => RemoteClipboard.TextSignature(Text ?? ""),
        ClipKind.Image => "image:" + ImageHash,
        ClipKind.Files => RemoteClipboard.FilesSignature(Files),
        _ => "none",
    };
}

/// <summary>
/// The full clipboard of a Windows device (text, images, files), not only MeshCentral's text one. Measured on MeshCentral
/// 1.3.1: the agent's own getclip/setclip carry only a string, so text goes with setclip and the rest with what the app
/// already has: a PowerShell run as the signed-in user (runcommands, runAsUser 1) reads or fills the clipboard, and the
/// files tunnel moves the bytes. The agent passes runcommands to PowerShell on its standard input, so the script travels
/// as one line (-EncodedCommand) and in its own STA process (the Windows clipboard needs it); its answer comes back in
/// base64 so no console code page can spoil it.
/// El portapapeles completo de un equipo Windows (texto, imágenes, ficheros), no solo el de texto de MeshCentral. Medido
/// en 1.3.1: getclip/setclip del agente solo llevan una cadena, así que el texto va con setclip y lo demás con lo que la
/// app ya tiene: un PowerShell como el usuario con sesión lee o llena el portapapeles, y el túnel de ficheros mueve los
/// bytes. El agente pasa runcommands a PowerShell por su entrada estándar, así que el guion viaja en una línea
/// (-EncodedCommand) y en su propio proceso STA (el portapapeles de Windows lo pide); su respuesta vuelve en base64 para
/// que ninguna página de códigos de la consola la estropee.
/// </summary>
public sealed class RemoteClipboard
{
    /// <summary>Files above this are not moved without the person asking. / Por encima, no se mueven sin que lo pida la persona.</summary>
    public const long AutomaticLimit = 500L * 1024 * 1024;

    internal const string Marker = "MCCCLIP:";
    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly SemaphoreSlim _one = new(1, 1);

    public RemoteClipboard(MeshServerSession session, Device device)
    {
        _session = session;
        _device = device;
    }

    /// <summary>Text, images and files: Windows, run commands and files rights. / Texto, imágenes y ficheros: Windows, permiso de ejecutar órdenes y de ficheros.</summary>
    public bool CanAll => _device.IsWindows && _device.HasAgent && (_session.RightsOn(_device) & (long)MeshRights.RemoteCommand) != 0 && _device.CanOpenFiles;

    /// <summary>Text only, with MeshCentral's own setclip: remote control. / Solo texto, con el setclip de MeshCentral: control remoto.</summary>
    public bool CanText => _device.HasAgent && (_session.RightsOn(_device) & (long)MeshRights.RemoteControl) != 0;

    public static string TextSignature(string text) =>
        "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public static string FilesSignature(IEnumerable<string> paths) =>
        "files:" + string.Join("|", paths.Select(p => p.Replace('\\', '/').TrimEnd('/').ToUpperInvariant()));

    // ------------------------------------------------------------------ scripts / guiones

    /// <summary>One line that runs <paramref name="script"/> in a new STA Windows PowerShell. / Una línea que ejecuta el guion en un PowerShell STA nuevo.</summary>
    internal static string OneLine(string script) =>
        "& \"$env:windir\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -STA -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>A PowerShell single-quoted literal. / Un literal de PowerShell entre comillas simples.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private const string Answer = "'" + Marker + "'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $o -Compress -Depth 3)))";

    private const string ImageHash = "$g=[Windows.Forms.Clipboard]::GetImage(); $m=New-Object IO.MemoryStream; $g.Save($m,[Drawing.Imaging.ImageFormat]::Png); $b=$m.ToArray(); $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');";

    internal static string ReadScript(long limit) => $$"""
        $ErrorActionPreference='Stop'
        $o=[ordered]@{kind='none';temp=$env:TEMP}
        try {
          Add-Type -AssemblyName System.Windows.Forms,System.Drawing
          if([Windows.Forms.Clipboard]::ContainsFileDropList()){
            $o.kind='files'; $o.files=@([Windows.Forms.Clipboard]::GetFileDropList()); $s=0
            foreach($f in $o.files){ if($s -gt {{limit}}){break}; $i=Get-Item -LiteralPath $f -Force; if($i.PSIsContainer){ foreach($x in Get-ChildItem -LiteralPath $f -Recurse -File -Force -ErrorAction SilentlyContinue){ $s+=$x.Length; if($s -gt {{limit}}){break} } } else { $s+=$i.Length } }
            $o.size=$s
          } elseif([Windows.Forms.Clipboard]::ContainsImage()){
            {{ImageHash}}
            $d=Join-Path $env:TEMP 'MeshCentralClient'; New-Item -ItemType Directory -Force $d | Out-Null
            $p=Join-Path $d ('image-'+$h.Substring(0,16)+'.png'); [IO.File]::WriteAllBytes($p,$b)
            $o.kind='image'; $o.hash=$h; $o.image=$p; $o.size=$b.Length
          } elseif([Windows.Forms.Clipboard]::ContainsText()){
            $o.kind='text'; $o.text=[Windows.Forms.Clipboard]::GetText()
          }
        } catch { $o.kind='error'; $o.error=$_.Exception.Message }
        {{Answer}}
        """;

    /// <summary>A fresh folder for what goes to the device; the previous ones (no longer on its clipboard) go away.
    /// Una carpeta nueva para lo que va al equipo; las anteriores (que ya no están en su portapapeles) se borran.</summary>
    internal static string PrepareScript(string id) => $$"""
        $ErrorActionPreference='Stop'
        $o=[ordered]@{kind='folder'}
        try {
          $root=Join-Path $env:TEMP 'MeshCentralClient\clip'; New-Item -ItemType Directory -Force $root | Out-Null
          Get-ChildItem -LiteralPath $root -Directory -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
          $d=Join-Path $root {{Quote(id)}}; New-Item -ItemType Directory -Force $d | Out-Null; $o.folder=$d
        } catch { $o.kind='error'; $o.error=$_.Exception.Message }
        {{Answer}}
        """;

    internal static string SetFilesScript(IReadOnlyList<string> paths) => $$"""
        $ErrorActionPreference='Stop'
        $o=[ordered]@{kind='files'}
        try {
          Add-Type -AssemblyName System.Windows.Forms
          $c=New-Object Collections.Specialized.StringCollection
          foreach($p in @({{string.Join(",", paths.Select(Quote))}})){ [void]$c.Add($p) }
          [Windows.Forms.Clipboard]::SetFileDropList($c)
          $o.files=@([Windows.Forms.Clipboard]::GetFileDropList())
        } catch { $o.kind='error'; $o.error=$_.Exception.Message }
        {{Answer}}
        """;

    internal static string SetImageScript(string path) => $$"""
        $ErrorActionPreference='Stop'
        $o=[ordered]@{kind='image'}
        try {
          Add-Type -AssemblyName System.Windows.Forms,System.Drawing
          $p={{Quote(path)}}; $in=New-Object IO.MemoryStream(,[IO.File]::ReadAllBytes($p)); $img=[Drawing.Image]::FromStream($in)
          [Windows.Forms.Clipboard]::SetImage($img); $img.Dispose(); Remove-Item -LiteralPath $p -Force
          {{ImageHash}}
          $o.hash=$h
        } catch { $o.kind='error'; $o.error=$_.Exception.Message }
        {{Answer}}
        """;

    /// <summary>Reads the script's answer out of everything the shell printed. / Saca la respuesta del guion de todo lo que escribió el intérprete.</summary>
    internal static JsonObject? ParseAnswer(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0) continue;
            try { return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(line[(at + Marker.Length)..]))) as JsonObject; }
            catch (Exception ex) when (ex is FormatException or JsonException) { }
        }
        return null;
    }

    internal static RemoteClip ToClip(JsonObject o, long limit)
    {
        var kind = o.Str("kind") switch { "text" => ClipKind.Text, "image" => ClipKind.Image, "files" => ClipKind.Files, "error" => ClipKind.Error, _ => ClipKind.None };
        var files = o["files"] switch
        {
            JsonArray a => a.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList(),
            JsonValue v when v.TryGetValue<string>(out var one) => new List<string> { one },
            _ => new List<string>(),
        };
        long size = 0;
        if (o["size"] is JsonValue sv) { if (sv.TryGetValue<long>(out var l)) size = l; else if (sv.TryGetValue<double>(out var d)) size = (long)d; }
        return new RemoteClip(kind, o.Str("text"), files, o.Str("image"), o.Str("hash"), size, size > limit, o.Str("temp"), o.Str("error"));
    }

    private async Task<JsonObject> RunAsync(string script, string what, CancellationToken ct)
    {
        var output = await _session.RunCommandsAsync(_device, OneLine(script), CommandShell.PowerShell, asUser: true, timeout: TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
        var o = ParseAnswer(output) ?? throw new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible",
            $"The device's PowerShell did not answer while {what} (is Windows PowerShell blocked there?): {Short(output)}");
        if (o.Str("kind") == "error")
            throw new MeshClientException(MeshFailure.Rejected, "portapapeles/sincronizar/rechazo-remoto", $"The device could not {what}: {o.Str("error")}") { ServerMessage = o.Str("error") };
        return o;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s.Trim();

    // ------------------------------------------------------------------ device → this PC / equipo → este PC

    /// <summary>What the device's clipboard holds now. / Lo que tiene ahora el portapapeles del equipo.</summary>
    public async Task<RemoteClip> ReadAsync(long limit = AutomaticLimit, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try { return ToClip(await RunAsync(ReadScript(limit), "read its clipboard", ct).ConfigureAwait(false), limit); }
        finally { _one.Release(); }
    }

    /// <summary>Downloads the files of <paramref name="clip"/> into <paramref name="localFolder"/>; returns the local paths, in order.
    /// Baja los ficheros a la carpeta local; devuelve las rutas locales, en orden.</summary>
    public async Task<IReadOnlyList<string>> DownloadFilesAsync(FilesSession files, RemoteClip clip, string localFolder, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(localFolder);
        var local = new List<string>();
        foreach (var group in clip.Files.Select(p => p.Replace('\\', '/').TrimEnd('/')).GroupBy(Parent))
        {
            var listing = await files.ListAsync(group.Key, ct).ConfigureAwait(false);
            var names = group.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
            var entries = listing.Entries.Where(e => names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            await files.DownloadTreeAsync(group.Key, entries, localFolder, progress, ct).ConfigureAwait(false);
            local.AddRange(entries.Select(e => Path.Combine(localFolder, e.Name)));
        }
        return local;
    }

    /// <summary>"C:/a/b.txt" → "C:/a"; "C:/b.txt" → "C:/". / La carpeta de una ruta del equipo.</summary>
    internal static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        var parent = i <= 0 ? path : path[..i];
        return parent.EndsWith(':') ? parent + "/" : parent;
    }

    /// <summary>The PNG the device saved for its clipboard image; the copy on the device is removed. / El PNG que guardó el equipo; se borra allí.</summary>
    public async Task<byte[]> DownloadImageAsync(FilesSession files, RemoteClip clip, CancellationToken ct = default)
    {
        var path = (clip.ImagePath ?? throw new ArgumentException("no image", nameof(clip))).Replace('\\', '/');
        using var png = new MemoryStream();
        await files.DownloadAsync(path, png, null, ct).ConfigureAwait(false);
        try { await files.DeleteAsync(Parent(path), new[] { path[(path.LastIndexOf('/') + 1)..] }, recursive: false, ct).ConfigureAwait(false); }
        catch (MeshClientException) { }
        return png.ToArray();
    }

    // ------------------------------------------------------------------ this PC → device / este PC → equipo

    /// <summary>
    /// Text with MeshCentral's own setclip (the agent answers success, measured). Returns the signature the device now has.
    /// Texto con el setclip de MeshCentral (el agente contesta success, medido). Devuelve la firma que tiene ahora el equipo.
    /// </summary>
    public async Task<string> SetTextAsync(string text, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _session.SetDeviceClipboardTextAsync(_device, text, ct).ConfigureAwait(false);
            return TextSignature(text);
        }
        finally { _one.Release(); }
    }

    /// <summary>Uploads local files and folders and puts them on the device's clipboard; returns its signature.
    /// Sube ficheros y carpetas y los pone en el portapapeles del equipo; devuelve su firma.</summary>
    public async Task<string> SetFilesAsync(FilesSession files, IReadOnlyList<string> localPaths, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = await PrepareAsync(ct).ConfigureAwait(false);
            var items = localPaths.Select(p => (p, Path.GetFileName(p.TrimEnd('\\', '/')))).ToList();
            await files.UploadTreeAsync(items, folder.Replace('\\', '/'), progress, ct).ConfigureAwait(false);
            var remote = items.Select(i => folder.TrimEnd('\\') + "\\" + i.Item2).ToList();
            var o = await RunAsync(SetFilesScript(remote), "put the files on its clipboard", ct).ConfigureAwait(false);
            return ToClip(o, long.MaxValue).Signature;
        }
        finally { _one.Release(); }
    }

    /// <summary>Puts a PNG on the device's clipboard as an image; returns its signature. / Pone un PNG como imagen en el portapapeles del equipo; devuelve su firma.</summary>
    public async Task<string> SetImageAsync(FilesSession files, byte[] png, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = await PrepareAsync(ct).ConfigureAwait(false);
            await files.UploadAsync(folder.Replace('\\', '/'), "image.png", new MemoryStream(png), null, ct).ConfigureAwait(false);
            var o = await RunAsync(SetImageScript(folder.TrimEnd('\\') + "\\image.png"), "put the image on its clipboard", ct).ConfigureAwait(false);
            return ToClip(o, long.MaxValue).Signature;
        }
        finally { _one.Release(); }
    }

    private async Task<string> PrepareAsync(CancellationToken ct)
    {
        var o = await RunAsync(PrepareScript(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)), "prepare a folder for the copy", ct).ConfigureAwait(false);
        return o.Str("folder") ?? throw new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible", "The device did not say where to put the copy.");
    }
}
