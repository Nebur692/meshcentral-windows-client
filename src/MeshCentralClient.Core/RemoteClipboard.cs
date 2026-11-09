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
public sealed record RemoteClip(ClipKind Kind, string? Text, IReadOnlyList<string> Files, string? ImagePath, string? ImageHash, string? Temp, string? Error)
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
/// The full clipboard of a Windows device (text, images, files), not only MeshCentral's text one. The agent's own
/// getclip/setclip carry only a string (meshcore.js), so text goes with setclip and the rest with what the app already
/// has: a PowerShell of the signed-in user reads or fills the clipboard, and the files tunnel moves the bytes.
/// Measured on GitHub's Windows (08-11): runcommands with runAsUser never answers, not even to Write-Output, and while it
/// hangs the agent drops every later batch; the user PowerShell terminal tunnel (what the website opens) answers. So one
/// such terminal stays open for the viewer, and the script travels as one short line (gzip + base64, run with iex) in an
/// STA runspace (the Windows clipboard needs STA). A terminal is a screen, not a channel: it splits long lines, adds
/// escape sequences and, once it scrolls, leaves out what looks already painted (seen: the sixth answer never read). So
/// every answer goes to a file, written whole and then renamed, and comes through the files tunnel; on the screen goes
/// only a short note saying where, and if that note is lost the app sees the file in the folder.
/// El portapapeles completo de un equipo Windows (texto, imágenes, ficheros), no solo el de texto de MeshCentral.
/// getclip/setclip del agente solo llevan una cadena, así que el texto va con setclip y lo demás con lo que la app ya
/// tiene: un PowerShell del usuario con sesión lee o llena el portapapeles, y el túnel de ficheros mueve los bytes.
/// Medido en el Windows de GitHub (08-11): runcommands con runAsUser no contesta nunca, ni a Write-Output, y mientras se
/// cuelga el agente tira las tandas siguientes; el túnel de terminal PowerShell del usuario (el que abre la web) sí
/// contesta. Así que el visor deja abierta una terminal de esas, y el guion viaja en una línea corta (gzip + base64, con
/// iex) en un runspace STA (el portapapeles de Windows lo pide). Una terminal es una pantalla, no un canal: parte las
/// líneas largas, mete secuencias de escape y, al desplazarse, se salta lo que parece ya pintado (visto: la sexta
/// respuesta no se leyó nunca). Así que cada respuesta va a un fichero, escrito entero y luego renombrado, y llega por el
/// túnel de ficheros; a la pantalla va solo una nota corta con dónde está, y si esa nota se pierde la app ve el fichero en
/// la carpeta.
/// </summary>
public sealed class RemoteClipboard : IAsyncDisposable
{
    /// <summary>Files above this are not moved without the person asking. / Por encima, no se mueven sin que lo pida la persona.</summary>
    public const long AutomaticLimit = 500L * 1024 * 1024;

    internal const string Marker = "MCCCLIP:";
    internal const string EndMarker = ":PILCCCM";
    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly UserShell _user;

    public RemoteClipboard(MeshServerSession session, Device device)
    {
        _session = session;
        _device = device;
        _user = new UserShell(session, device, new UserShellCodes(
            "portapapeles/sincronizar/conexion-cortada", "portapapeles/sincronizar/respuesta-ilegible", "portapapeles/sincronizar/rechazo-remoto"));
    }

    /// <summary>Text, images and files: Windows, with remote control and neither terminal nor files taken away.
    /// Texto, imágenes y ficheros: Windows, con control remoto y sin quitar terminal ni ficheros.</summary>
    public bool CanAll => UserShell.Can(_session, _device) && !_user.Restricted;

    /// <summary>
    /// Full rights are 0xFFFFFFFF, every bit on, the "no terminal" and "no files" ones too: as the server does
    /// (isProtocolAllowedByRights), those only count when the rights are not full.
    /// Los permisos completos son 0xFFFFFFFF, todos los bits a uno, también «sin terminal» y «sin ficheros»: como hace el
    /// servidor, esos solo cuentan cuando los permisos no son completos.
    /// </summary>
    internal static bool RightsAllowAll(long rights) =>
        rights == 0xFFFFFFFF || ((rights & (long)MeshRights.RemoteControl) != 0 && (rights & (long)(MeshRights.NoTerminal | MeshRights.NoFiles)) == 0);

    /// <summary>The device's text with MeshCentral's own getclip, or null. / El texto del equipo con el getclip de MeshCentral, o null.</summary>
    public Task<string?> ReadTextAsync(CancellationToken ct = default) => _session.GetDeviceClipboardTextAsync(_device, ct);

    /// <summary>Text only, with MeshCentral's own setclip: remote control. / Solo texto, con el setclip de MeshCentral: control remoto.</summary>
    public bool CanText => _device.HasAgent && (_session.RightsOn(_device) & (long)MeshRights.RemoteControl) != 0;

    public static string TextSignature(string text) =>
        "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public static string FilesSignature(IEnumerable<string> paths) =>
        "files:" + string.Join("|", paths.Select(p => p.Replace('\\', '/').TrimEnd('/').ToUpperInvariant()));

    // ------------------------------------------------------------------ scripts / guiones

    /// <summary><paramref name="body"/> run in an STA runspace of the same PowerShell, its output written out.
    /// El cuerpo ejecutado en un runspace STA del mismo PowerShell, con su salida escrita.</summary>
    internal static string Wrap(string body) =>
        "$b=@'\n" + body + "\n'@\n"
        + "$rs=[runspacefactory]::CreateRunspace(); $rs.ApartmentState='STA'; $rs.ThreadOptions='ReuseThread'; $rs.Open()\n"
        + "$ps=[powershell]::Create(); $ps.Runspace=$rs; [void]$ps.AddScript($b)\n"
        + "foreach($x in $ps.Invoke()){ Write-Output $x }\n"
        + "$ps.Dispose(); $rs.Close()\n";

    /// <summary>One short line that runs <paramref name="body"/> as <see cref="Wrap"/> does. / Una línea corta que ejecuta el cuerpo como Wrap.</summary>
    internal static string OneLine(string body)
    {
        using var packed = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(Wrap(body)));
        return "iex ([IO.StreamReader]::new([IO.Compression.GZipStream]::new([IO.MemoryStream]::new([Convert]::FromBase64String('"
               + Convert.ToBase64String(packed.ToArray()) + "')),[IO.Compression.CompressionMode]::Decompress))).ReadToEnd()";
    }

    /// <summary>The script inside a line of <see cref="OneLine"/>. / El guion de dentro de una línea de OneLine.</summary>
    internal static string Unpack(string line)
    {
        var b64 = line[(line.IndexOf("String('", StringComparison.Ordinal) + 8)..line.IndexOf("')", StringComparison.Ordinal)];
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(b64)), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A PowerShell single-quoted literal. / Un literal de PowerShell entre comillas simples.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Makes folder $d readable by this user for whatever lands in it. Measured on a real service agent (Pc-Salon, 08-11):
    /// folders there have no inheritable permissions, so what the agent (SYSTEM) uploads gets none for the user, and pasting
    /// it would say access denied. The user owns $d, so can add it, for SYSTEM too: with an inheritable permission only for
    /// the user, what the user writes there stops being readable by the agent (seen). In a connect-mode agent both are the
    /// same account. Always, not only on creating it: an earlier version may have left the folder without it.
    /// Hace que lo que caiga en la carpeta $d lo pueda leer este usuario. Medido en un agente de servicio real: allí las
    /// carpetas no tienen permisos heredables, así que lo que sube el agente (SYSTEM) no le da ninguno al usuario, y pegarlo
    /// daría acceso denegado. El usuario es dueño de $d, así que puede añadirlo, también para SYSTEM: con un heredable solo
    /// para el usuario, lo que escribe él deja de poder leerlo el agente (visto). Siempre, no solo al crearla: una versión
    /// anterior pudo dejar la carpeta sin él.
    /// </summary>
    internal const string GrantToUser = "icacls $d /grant ([Security.Principal.WindowsIdentity]::GetCurrent().Name+':(OI)(CI)F') '*S-1-5-18:(OI)(CI)F' | Out-Null";

    internal const string Answer =
        "$o.id=$rid; $d=Join-Path $env:TEMP 'MeshCentralClient\\answers'; New-Item -ItemType Directory -Force $d | Out-Null; " + GrantToUser + "\n"
        + "$p=Join-Path $d ($rid+'.json'); [IO.File]::WriteAllText($p+'.tmp',(ConvertTo-Json $o -Compress -Depth 3)); [IO.File]::Move($p+'.tmp',$p)\n"
        + "'" + Marker + "'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json ([ordered]@{id=$rid;path=$p}) -Compress)))+'" + EndMarker + "'";

    private const string ImageHash = "$g=[Windows.Forms.Clipboard]::GetImage(); $m=New-Object IO.MemoryStream; $g.Save($m,[Drawing.Imaging.ImageFormat]::Png); $b=$m.ToArray(); $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');";

    internal const string AnswerForTests = Answer;

    /// <summary>The only script ever typed: it makes the folder the others go through. / El único guion que se teclea: crea la carpeta por la que van los demás.</summary>
    internal const string HelloScript = "$o=[ordered]@{kind='hello'}\n" + Answer;

    internal static string ReadScript() => $$"""
        $ErrorActionPreference='Stop'
        $o=[ordered]@{kind='none';temp=$env:TEMP}
        try {
          Add-Type -AssemblyName System.Windows.Forms,System.Drawing
          if([Windows.Forms.Clipboard]::ContainsFileDropList()){
            $o.kind='files'; $o.files=@([Windows.Forms.Clipboard]::GetFileDropList())
          } elseif([Windows.Forms.Clipboard]::ContainsImage()){
            {{ImageHash}}
            $d=Join-Path $env:TEMP 'MeshCentralClient'; New-Item -ItemType Directory -Force $d | Out-Null
            $p=Join-Path $d ('image-'+$h.Substring(0,16)+'.png'); [IO.File]::WriteAllBytes($p,$b)
            $o.kind='image'; $o.hash=$h; $o.image=$p
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
          $d=Join-Path $root {{Quote(id)}}; New-Item -ItemType Directory -Force $d | Out-Null; {{GrantToUser}}; $o.folder=$d
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

    internal static readonly System.Text.RegularExpressions.Regex Escapes = new(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reads the script's answer out of everything the terminal printed: escape sequences and line breaks (the terminal
    /// splits long lines) are dropped first, then the base64 between the two marks is decoded.
    /// Saca la respuesta del guion de todo lo que escribió la terminal: primero se quitan secuencias de escape y saltos
    /// de línea (la terminal parte las líneas largas), y luego se decodifica el base64 entre las dos marcas.
    /// </summary>
    internal static JsonObject? ParseAnswer(string output, string? id = null)
    {
        var flat = string.Concat(Escapes.Replace(output, "").Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
        for (var at = flat.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = flat.IndexOf(Marker, at + 1, StringComparison.Ordinal))
        {
            var start = at + Marker.Length;
            var end = flat.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < 0) return null;
            try
            {
                // An answer to another request (the terminal repaints) is not this one. / La respuesta a otra petición (la terminal repinta) no es esta.
                if (JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(flat[start..end]))) is JsonObject o && (id is null || o.Str("id") == id)) return o;
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { }
        }
        return null;
    }

    internal static RemoteClip ToClip(JsonObject o)
    {
        var kind = o.Str("kind") switch { "text" => ClipKind.Text, "image" => ClipKind.Image, "files" => ClipKind.Files, "error" => ClipKind.Error, _ => ClipKind.None };
        var files = o["files"] switch
        {
            JsonArray a => a.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList(),
            JsonValue v when v.TryGetValue<string>(out var one) => new List<string> { one },
            _ => new List<string>(),
        };
        return new RemoteClip(kind, o.Str("text"), files, o.Str("image"), o.Str("hash"), o.Str("temp"), o.Str("error"));
    }

    private Task<FilesSession> FilesAsync(CancellationToken ct) => _user.FilesAsync(ct);

    private Task<JsonObject> RunAsync(string script, string what, CancellationToken ct) => _user.RunAsync(script, what, ct);

    // ------------------------------------------------------------------ device → this PC / equipo → este PC

    /// <summary>What the device's clipboard holds now. / Lo que tiene ahora el portapapeles del equipo.</summary>
    public async Task<RemoteClip> ReadAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try { return ToClip(await RunAsync(ReadScript(), "read its clipboard", ct).ConfigureAwait(false)); }
        finally { _one.Release(); }
    }

    /// <summary>"C:/a/b.txt" → "C:/a"; "C:/b.txt" → "C:/". / La carpeta de una ruta del equipo.</summary>
    internal static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        var parent = i <= 0 ? path : path[..i];
        return parent.EndsWith(':') ? parent + "/" : parent;
    }

    /// <summary>The PNG the device saved for its clipboard image; the copy on the device is removed. / El PNG que guardó el equipo; se borra allí.</summary>
    public async Task<byte[]> DownloadImageAsync(RemoteClip clip, CancellationToken ct = default)
    {
        var files = await FilesAsync(ct).ConfigureAwait(false);
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
    public async Task<string> SetFilesAsync(IReadOnlyList<string> localPaths, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = await FilesAsync(ct).ConfigureAwait(false);
            var folder = await PrepareAsync(ct).ConfigureAwait(false);
            var items = localPaths.Select(p => (p, Path.GetFileName(p.TrimEnd('\\', '/')))).ToList();
            await files.UploadTreeAsync(items, folder.Replace('\\', '/'), progress, ct).ConfigureAwait(false);
            var remote = items.Select(i => folder.TrimEnd('\\') + "\\" + i.Item2).ToList();
            var o = await RunAsync(SetFilesScript(remote), "put the files on its clipboard", ct).ConfigureAwait(false);
            return ToClip(o).Signature;
        }
        finally { _one.Release(); }
    }

    /// <summary>Puts a PNG on the device's clipboard as an image; returns its signature. / Pone un PNG como imagen en el portapapeles del equipo; devuelve su firma.</summary>
    public async Task<string> SetImageAsync(byte[] png, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = await FilesAsync(ct).ConfigureAwait(false);
            var folder = await PrepareAsync(ct).ConfigureAwait(false);
            await files.UploadAsync(folder.Replace('\\', '/'), "image.png", new MemoryStream(png), null, ct).ConfigureAwait(false);
            var o = await RunAsync(SetImageScript(folder.TrimEnd('\\') + "\\image.png"), "put the image on its clipboard", ct).ConfigureAwait(false);
            return ToClip(o).Signature;
        }
        finally { _one.Release(); }
    }

    private async Task<string> PrepareAsync(CancellationToken ct)
    {
        var o = await RunAsync(PrepareScript(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)), "prepare a folder for the copy", ct).ConfigureAwait(false);
        return o.Str("folder") ?? throw new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible", "The device did not say where to put the copy.");
    }

    public ValueTask DisposeAsync() => _user.DisposeAsync();
}
