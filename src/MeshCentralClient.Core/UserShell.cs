using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>The catalog codes a <see cref="UserShell"/> fails with, written whole so the catalog's sweep sees them.
/// Los códigos del catálogo con los que falla, escritos enteros para que los vea el barrido del catálogo.</summary>
internal sealed record UserShellCodes(string Lost, string Unreadable, string Refused);

/// <summary>
/// A PowerShell of the device's signed-in user, driven from the app: the terminal tunnel opens it, scripts travel as files
/// and each answer comes back as a file through the files tunnel. It is the one way measured to run something as that user
/// on both kinds of agent (see <see cref="RemoteClipboard"/>); it needs someone signed in there and, if the group asks for
/// consent for terminal sessions, the person there is asked.
/// Un PowerShell del usuario con sesión del equipo, manejado desde la app: lo abre el túnel de terminal, los guiones viajan
/// como ficheros y cada respuesta vuelve como fichero por el túnel de ficheros. Es la única forma medida de ejecutar algo
/// como ese usuario en los dos tipos de agente; pide que haya alguien con sesión y, si el grupo pide consentimiento para
/// las sesiones de terminal, se le pregunta a la persona de allí.
/// </summary>
internal sealed class UserShell(MeshServerSession session, Device device, UserShellCodes codes) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAnswerLimit = TimeSpan.FromSeconds(60);
    private readonly StringBuilder _screen = new();
    private TerminalSession? _shell;
    private FilesSession? _files;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shellEnded;
    private string? _answers;

    /// <summary>Windows, with remote control and neither terminal nor files taken away.
    /// Windows, con control remoto y sin quitar terminal ni ficheros.</summary>
    internal static bool Can(MeshServerSession session, Device device) =>
        device.IsWindows && device.CanOpenTerminal && device.CanOpenFiles && RemoteClipboard.RightsAllowAll(session.RightsOn(device));

    internal async Task<FilesSession> FilesAsync(CancellationToken ct)
    {
        if (_files is { Tunnel.IsOpen: true }) return _files;
        if (_files is not null) await _files.DisposeAsync().ConfigureAwait(false);
        return _files = await FilesSession.OpenAsync(session, device.Id, ct).ConfigureAwait(false);
    }

    /// <summary>The user's PowerShell, opened once and waited for until it shows its prompt. / El PowerShell del usuario, abierto una vez y esperado hasta que enseña su indicador.</summary>
    private async Task<TerminalSession> ShellAsync(CancellationToken ct)
    {
        if (_shell is not null && !_shellEnded) return _shell;
        await CloseShellAsync().ConfigureAwait(false);
        lock (_screen) { _screen.Clear(); _shellEnded = false; }
        var shell = await TerminalSession.OpenAsync(session, device.Id, TunnelProtocol.UserPowerShell, 200, 50, ct).ConfigureAwait(false);
        shell.Ended += (_, _) => { lock (_screen) { _shellEnded = true; _changed.TrySetResult(); } };
        shell.Output += (_, d) => { lock (_screen) { _screen.Append(Encoding.UTF8.GetString(d.Span)); _changed.TrySetResult(); } };
        _shell = shell;
        await WaitAsync(() => RemoteClipboard.Escapes.Replace(_screen.ToString(), "").Contains("PS ", StringComparison.Ordinal), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
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

    /// <summary>Runs a script that fills $o and ends with the answer's lines; returns $o. / Ejecuta un guion que llena $o y acaba con las líneas de la respuesta; devuelve $o.</summary>
    /// <param name="answerLimit">How long the script may take; 60 s if null. / Cuánto puede tardar el guion; 60 s si es null.</param>
    internal async Task<JsonObject> RunAsync(string script, string what, CancellationToken ct, TimeSpan? answerLimit = null)
    {
        var shell = await ShellAsync(ct).ConfigureAwait(false);
        if (_answers is null && script != RemoteClipboard.HelloScript) await RunAsync(RemoteClipboard.HelloScript, "start", ct).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N")[..12];
        lock (_screen) _screen.Clear();
        var body = "$rid=" + RemoteClipboard.Quote(id) + "\n" + script;
        string? uploaded = null;
        if (_answers is null)
        {
            // No folder yet: only the hello gets here. / Aún sin carpeta: aquí solo llega el saludo.
            await shell.WriteAsync("Clear-Host; " + RemoteClipboard.OneLine(body) + "\r", ct).ConfigureAwait(false);
        }
        else
        {
            // PSReadLine repaints the whole line at each key: a long line took 8 s on Pc-Salon, and without PSReadLine it was
            // not even taken. So the script goes as a file and a very short line runs it.
            // PSReadLine repinta la línea entera a cada tecla: una línea larga tardaba 8 s en Pc-Salon, y sin PSReadLine ni se
            // aceptaba. Así que el guion va como fichero y lo ejecuta una línea muy corta.
            var files = await FilesAsync(ct).ConfigureAwait(false);
            uploaded = id + ".ps1";
            await files.UploadAsync(_answers, uploaded, new MemoryStream(Encoding.UTF8.GetBytes(RemoteClipboard.Wrap(body))), null, ct).ConfigureAwait(false);
            await shell.WriteAsync("Clear-Host; iex ([IO.File]::ReadAllText(" + RemoteClipboard.Quote((_answers + "/" + uploaded).Replace('/', '\\')) + "))\r", ct).ConfigureAwait(false);
        }
        var path = await WaitForAnswerAsync(id, answerLimit ?? DefaultAnswerLimit, ct).ConfigureAwait(false);
        if (uploaded is not null) { try { await (await FilesAsync(ct).ConfigureAwait(false)).DeleteAsync(_answers!, new[] { uploaded }, recursive: false, ct).ConfigureAwait(false); } catch (MeshClientException) { } }
        if (path is null)
        {
            string seen;
            lock (_screen) seen = RemoteClipboard.Escapes.Replace(_screen.ToString(), "");
            var ended = _shellEnded;
            await CloseShellAsync().ConfigureAwait(false); // whatever it was doing, the next request starts clean / haga lo que hiciera, la siguiente empieza limpia
            throw ended
                ? new MeshClientException(MeshFailure.ConnectionLost, codes.Lost, $"The device's PowerShell closed while it was going to {what} (is anyone signed in there?).")
                : new MeshClientException(MeshFailure.UnexpectedResponse, codes.Unreadable, $"The device's PowerShell did not answer while it was going to {what} (is Windows PowerShell blocked there?): {Short(seen)}");
        }
        var answer = await ReadAnswerFileAsync(path, ct).ConfigureAwait(false);
        if (answer.Str("kind") == "error")
            throw new MeshClientException(MeshFailure.Rejected, codes.Refused, $"The device could not {what}: {answer.Str("error")}") { ServerMessage = answer.Str("error") };
        return answer;
    }

    /// <summary>Where the answer is: said on the screen or, once the folder is known, seen in it. The folder is looked at
    /// 150 times at most, so a script that may take minutes does not ask every moment.
    /// Dónde está la respuesta: dicho en la pantalla o, ya conocida la carpeta, visto en ella. La carpeta se mira 150 veces
    /// como mucho, para que un guion que puede tardar minutos no pregunte a cada momento.</summary>
    private async Task<string?> WaitForAnswerAsync(string id, TimeSpan limit, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        var every = limit / 150;
        while (DateTime.UtcNow < until)
        {
            JsonObject? note = null;
            if (await WaitAsync(() => (note = RemoteClipboard.ParseAnswer(_screen.ToString(), id)) is not null, every, ct).ConfigureAwait(false))
            {
                var said = note!.Str("path")?.Replace('\\', '/');
                if (said is not null) _answers = RemoteClipboard.Parent(said);
                return said;
            }
            if (_shellEnded) return null;
            if (_answers is null) continue;
            var files = await FilesAsync(ct).ConfigureAwait(false);
            if ((await files.ListAsync(_answers, ct).ConfigureAwait(false)).Entries.Any(e => e.Name == id + ".json")) return _answers + "/" + id + ".json";
        }
        return null;
    }

    /// <summary>An answer, from its file; the file is removed. / Una respuesta, de su fichero; se borra.</summary>
    private async Task<JsonObject> ReadAnswerFileAsync(string path, CancellationToken ct)
    {
        var files = await FilesAsync(ct).ConfigureAwait(false);
        using var json = new MemoryStream();
        await files.DownloadAsync(path, json, null, ct).ConfigureAwait(false);
        try { await files.DeleteAsync(RemoteClipboard.Parent(path), new[] { path[(path.LastIndexOf('/') + 1)..] }, recursive: false, ct).ConfigureAwait(false); }
        catch (MeshClientException) { }
        try { if (JsonNode.Parse(json.ToArray()) is JsonObject o) return o; }
        catch (JsonException) { }
        throw new MeshClientException(MeshFailure.UnexpectedResponse, codes.Unreadable, "The device's answer file could not be read.");
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s.Trim();

    public async ValueTask DisposeAsync()
    {
        await CloseShellAsync().ConfigureAwait(false);
        if (_files is not null) { try { await _files.DisposeAsync().ConfigureAwait(false); } catch (MeshClientException) { } }
    }
}
