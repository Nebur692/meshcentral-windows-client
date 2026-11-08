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
/// The full clipboard of a Windows device (text, images, files), not only MeshCentral's text one. The agent's own
/// getclip/setclip carry only a string (meshcore.js), so text goes with setclip and the rest with what the app already
/// has: a PowerShell of the signed-in user reads or fills the clipboard, and the files tunnel moves the bytes.
/// Measured on GitHub's Windows (08-11): runcommands with runAsUser never answers, not even to Write-Output, and while it
/// hangs the agent drops every later batch; the user PowerShell terminal tunnel (what the website opens) answers. So one
/// such terminal stays open for the viewer. A terminal repaints, splits long lines at its width and adds escape
/// sequences, so the script travels as one short line (gzip + base64, run with iex) in an STA runspace (the Windows
/// clipboard needs STA), each answer carries the id of its request, goes in base64 between two marks and is read across
/// lines; a large one (a long copied text) goes to a file and comes through the files tunnel.
/// El portapapeles completo de un equipo Windows (texto, imágenes, ficheros), no solo el de texto de MeshCentral.
/// getclip/setclip del agente solo llevan una cadena, así que el texto va con setclip y lo demás con lo que la app ya
/// tiene: un PowerShell del usuario con sesión lee o llena el portapapeles, y el túnel de ficheros mueve los bytes.
/// Medido en el Windows de GitHub (08-11): runcommands con runAsUser no contesta nunca, ni a Write-Output, y mientras se
/// cuelga el agente tira las tandas siguientes; el túnel de terminal PowerShell del usuario (el que abre la web) sí
/// contesta. Así que el visor deja abierta una terminal de esas. Una terminal repinta, parte las líneas largas a su ancho
/// y mete secuencias de escape, así que el guion viaja en una línea corta (gzip + base64, con iex) en un runspace STA (el
/// portapapeles de Windows lo pide), cada respuesta lleva el id de su petición, va en base64 entre dos marcas y se lee a
/// través de las líneas; una grande (un texto largo copiado) va a un fichero y llega por el túnel de ficheros.
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
    private readonly StringBuilder _screen = new();
    private TerminalSession? _shell;
    private FilesSession? _files;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shellEnded;

    public RemoteClipboard(MeshServerSession session, Device device)
    {
        _session = session;
        _device = device;
    }

    /// <summary>Text, images and files: Windows, with remote control and neither terminal nor files taken away.
    /// Texto, imágenes y ficheros: Windows, con control remoto y sin quitar terminal ni ficheros.</summary>
    public bool CanAll
    {
        get
        {
            var r = _session.RightsOn(_device);
            return _device.IsWindows && _device.CanOpenTerminal && _device.CanOpenFiles
                   && (r & (long)MeshRights.RemoteControl) != 0 && (r & (long)(MeshRights.NoTerminal | MeshRights.NoFiles)) == 0;
        }
    }

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

    private const string Answer =
        "$o.id=$rid; $j=ConvertTo-Json $o -Compress -Depth 3\n"
        + "if($j.Length -gt 3000){ $d=Join-Path $env:TEMP 'MeshCentralClient'; New-Item -ItemType Directory -Force $d | Out-Null; $p=Join-Path $d ('answer-'+$rid+'.json'); [IO.File]::WriteAllText($p,$j); $j=ConvertTo-Json ([ordered]@{kind='answerfile';id=$rid;path=$p}) -Compress }\n"
        + "'" + Marker + "'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j))+'" + EndMarker + "'";

    private const string ImageHash = "$g=[Windows.Forms.Clipboard]::GetImage(); $m=New-Object IO.MemoryStream; $g.Save($m,[Drawing.Imaging.ImageFormat]::Png); $b=$m.ToArray(); $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');";

    internal const string AnswerForTests = Answer;

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

    private static readonly System.Text.RegularExpressions.Regex Escapes = new(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    private async Task<FilesSession> FilesAsync(CancellationToken ct)
    {
        if (_files is { Tunnel.IsOpen: true }) return _files;
        if (_files is not null) await _files.DisposeAsync().ConfigureAwait(false);
        return _files = await FilesSession.OpenAsync(_session, _device.Id, ct).ConfigureAwait(false);
    }

    /// <summary>The user's PowerShell, opened once and waited for until it shows its prompt. / El PowerShell del usuario, abierto una vez y esperado hasta que enseña su indicador.</summary>
    private async Task<TerminalSession> ShellAsync(CancellationToken ct)
    {
        if (_shell is not null && !_shellEnded) return _shell;
        await CloseShellAsync().ConfigureAwait(false);
        lock (_screen) { _screen.Clear(); _shellEnded = false; }
        var shell = await TerminalSession.OpenAsync(_session, _device.Id, TunnelProtocol.UserPowerShell, 200, 50, ct).ConfigureAwait(false);
        shell.Ended += (_, _) => { lock (_screen) { _shellEnded = true; _changed.TrySetResult(); } };
        shell.Output += (_, d) => { lock (_screen) { _screen.Append(Encoding.UTF8.GetString(d.Span)); _changed.TrySetResult(); } };
        _shell = shell;
        await WaitAsync(() => Escapes.Replace(_screen.ToString(), "").Contains("PS ", StringComparison.Ordinal), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return shell;
    }

    /// <summary>Until <paramref name="done"/> holds on what the terminal has shown, the terminal ends or the time passes. / Hasta que se cumpla, acabe la terminal o pase el tiempo.</summary>
    private async Task<bool> WaitAsync(Func<bool> done, TimeSpan limit, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        while (true)
        {
            Task changed;
            lock (_screen)
            {
                if (done()) return true;
                if (_shellEnded) return false;
                if (_changed.Task.IsCompleted) _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = _changed.Task;
            }
            try { await changed.WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
    }

    private async Task CloseShellAsync()
    {
        var s = _shell;
        _shell = null;
        if (s is not null) { try { await s.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is MeshClientException or ObjectDisposedException) { } }
    }

    private async Task<JsonObject> RunAsync(string script, string what, CancellationToken ct)
    {
        var shell = await ShellAsync(ct).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N")[..12];
        lock (_screen) _screen.Clear();
        JsonObject? o = null;
        await shell.WriteAsync(OneLine("$rid=" + Quote(id) + "\n" + script) + "\r", ct).ConfigureAwait(false);
        var answered = await WaitAsync(() => (o = ParseAnswer(_screen.ToString(), id)) is not null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!answered)
        {
            string seen;
            lock (_screen) seen = Escapes.Replace(_screen.ToString(), "");
            var ended = _shellEnded;
            await CloseShellAsync().ConfigureAwait(false); // whatever it was doing, the next request starts clean / haga lo que hiciera, la siguiente empieza limpia
            throw ended
                ? new MeshClientException(MeshFailure.ConnectionLost, "portapapeles/sincronizar/conexion-cortada", $"The device's PowerShell closed while it was going to {what} (is anyone signed in there?).")
                : new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible", $"The device's PowerShell did not answer while it was going to {what} (is Windows PowerShell blocked there?): {Short(seen)}");
        }
        var answer = o!;
        if (answer.Str("kind") == "answerfile") answer = await ReadAnswerFileAsync(answer, ct).ConfigureAwait(false);
        if (answer.Str("kind") == "error")
            throw new MeshClientException(MeshFailure.Rejected, "portapapeles/sincronizar/rechazo-remoto", $"The device could not {what}: {answer.Str("error")}") { ServerMessage = answer.Str("error") };
        return answer;
    }

    /// <summary>An answer too large for the terminal's screen, from its file; the file is removed. / Una respuesta demasiado grande para la pantalla de la terminal, de su fichero; se borra.</summary>
    private async Task<JsonObject> ReadAnswerFileAsync(JsonObject note, CancellationToken ct)
    {
        var path = (note.Str("path") ?? "").Replace('\\', '/');
        var files = await FilesAsync(ct).ConfigureAwait(false);
        using var json = new MemoryStream();
        await files.DownloadAsync(path, json, null, ct).ConfigureAwait(false);
        try { await files.DeleteAsync(Parent(path), new[] { path[(path.LastIndexOf('/') + 1)..] }, recursive: false, ct).ConfigureAwait(false); }
        catch (MeshClientException) { }
        try { if (JsonNode.Parse(json.ToArray()) is JsonObject o) return o; }
        catch (JsonException) { }
        throw new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible", "The device's answer file could not be read.");
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
    public async Task<IReadOnlyList<string>> DownloadFilesAsync(RemoteClip clip, string localFolder, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        var files = await FilesAsync(ct).ConfigureAwait(false);
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
            return ToClip(o, long.MaxValue).Signature;
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
            return ToClip(o, long.MaxValue).Signature;
        }
        finally { _one.Release(); }
    }

    private async Task<string> PrepareAsync(CancellationToken ct)
    {
        var o = await RunAsync(PrepareScript(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)), "prepare a folder for the copy", ct).ConfigureAwait(false);
        return o.Str("folder") ?? throw new MeshClientException(MeshFailure.UnexpectedResponse, "portapapeles/sincronizar/respuesta-ilegible", "The device did not say where to put the copy.");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseShellAsync().ConfigureAwait(false);
        if (_files is not null) { try { await _files.DisposeAsync().ConfigureAwait(false); } catch (MeshClientException) { } }
    }
}
