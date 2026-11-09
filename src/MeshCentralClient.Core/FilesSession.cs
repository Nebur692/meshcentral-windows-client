using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MeshCentralClient.Core;

public enum RemoteEntryKind { Drive = 1, Folder = 2, File = 3 }

/// <summary>One item of a remote folder (MeshCentral "ls": n, t, s, d, f). / Un elemento de una carpeta remota.</summary>
public sealed record RemoteEntry(string Name, RemoteEntryKind Kind, long Size, DateTimeOffset? Modified, long? Free);

public sealed record RemoteFolder(string Path, IReadOnlyList<RemoteEntry> Entries);

/// <summary>
/// The Files tunnel (protocol 5), as the website and meshctrl use it. The agent answers commands with JSON sent as
/// BINARY frames, and file data as binary frames with a 4-byte header, so frames are told apart by their first byte
/// ('{' = JSON). One operation at a time: download data carries no request id.
/// El túnel de ficheros (protocolo 5), como lo usan la web y meshctrl. El agente contesta las órdenes con JSON en
/// mensajes BINARIOS y los datos con mensajes binarios de 4 bytes de cabecera, así que se distinguen por el primer
/// byte («{» = JSON). Una operación cada vez: los datos de una descarga no llevan identificador.
/// </summary>
public sealed partial class FilesSession : IAsyncDisposable
{
    private const int UploadBlock = 65564;
    private const int UploadWindow = 32;

    /// <summary>
    /// The agent sends one block of 16 KB per ack, whatever "startack" says (meshcore.js), so a download waits a whole round trip per
    /// block. This many acks go ahead of time, and each block received pays for the next. Measured through a delayed relay, at 80 ms of
    /// round trip: 0.2 MB/s with one, 18 MB/s with 128, and no more with 256.
    /// El agente manda un bloque de 16 KB por confirmación, diga lo que diga «startack» (meshcore.js), así que una descarga espera un viaje
    /// de ida y vuelta por bloque. Se adelantan estas confirmaciones, y cada bloque recibido paga el siguiente. Medido con un relé con
    /// retardo, a 80 ms de ida y vuelta: 0,2 MB/s con una, 18 MB/s con 128, y no más con 256.
    /// </summary>
    private const int DownloadWindow = 128;
    private readonly MeshTunnel _tunnel;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly TimeSpan _timeout;
    private Channel<ReadOnlyMemory<byte>> _inbox = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    private FilesSession(MeshTunnel tunnel, TimeSpan timeout)
    {
        _tunnel = tunnel;
        _timeout = timeout;
        tunnel.DataReceived += (_, d) => _inbox.Writer.TryWrite(d.ToArray());
        tunnel.TextReceived += (_, t) => _inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(t));
        tunnel.Closed += (_, e) => _inbox.Writer.TryComplete(e);
    }

    public MeshTunnel Tunnel => _tunnel;

    public static async Task<FilesSession> OpenAsync(MeshServerSession session, string nodeId, CancellationToken ct = default) =>
        new(await MeshTunnel.OpenAsync(session, nodeId, TunnelProtocol.Files, null, ct).ConfigureAwait(false), session.Timeout);

    // ------------------------------------------------------------------ listing / listar

    /// <summary>"" lists the drives on Windows and "/" elsewhere. / «» lista las unidades en Windows y «/» en el resto.</summary>
    public async Task<RemoteFolder> ListAsync(string path, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reqid = NewId();
            await SendAsync(new JsonObject { ["action"] = "ls", ["reqid"] = reqid, ["path"] = path }, "ficheros/ver-pantalla/conexion-cortada", ct).ConfigureAwait(false);
            var reply = await ReceiveJsonAsync(o => o.Str("action") is null && o.Str("reqid") == reqid || (o.ContainsKey("dir") && o.Str("reqid") == reqid),
                "ficheros/ver-pantalla/tiempo-agotado", "ficheros/ver-pantalla/conexion-cortada", ct).ConfigureAwait(false);
            if (reply["dir"] is not JsonArray dir)
                throw new MeshClientException(MeshFailure.NotFound, "ficheros/ver-pantalla/no-encontrado", $"The folder \"{path}\" does not exist or cannot be read.");
            var entries = dir.OfType<JsonObject>().Select(e => new RemoteEntry(
                e.Str("n") ?? "",
                (RemoteEntryKind)(e.Long("t") ?? 3),
                e.Long("s") ?? 0,
                ParseDate(e["d"]),
                e.Long("f"))).Where(e => e.Name.Length > 0).ToList();
            return new RemoteFolder(reply.Str("path") ?? path, entries);
        }
        finally { _one.Release(); }
    }

    private static DateTimeOffset? ParseDate(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d;
        if (v.TryGetValue<long>(out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    // ------------------------------------------------------------------ changes / cambios

    // The agent does not answer these: whoever calls checks with a new listing (the app always does).
    // El agente no contesta a estas: quien llama lo comprueba volviendo a listar (la app lo hace siempre).

    public Task CreateFolderAsync(string folderPath, CancellationToken ct = default) =>
        CommandAsync(new JsonObject { ["action"] = "mkdir", ["reqid"] = NewId(), ["path"] = folderPath }, ct);

    public Task RenameAsync(string folder, string oldName, string newName, CancellationToken ct = default) =>
        CommandAsync(new JsonObject { ["action"] = "rename", ["reqid"] = NewId(), ["path"] = folder, ["oldname"] = oldName, ["newname"] = newName }, ct);

    public Task DeleteAsync(string folder, IEnumerable<string> names, bool recursive, CancellationToken ct = default) =>
        CommandAsync(new JsonObject { ["action"] = "rm", ["reqid"] = NewId(), ["path"] = folder, ["delfiles"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()), ["rec"] = recursive }, ct);

    private async Task CommandAsync(JsonObject cmd, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try { await SendAsync(cmd, "ficheros/gestionar-ficheros/conexion-cortada", ct).ConfigureAwait(false); }
        finally { _one.Release(); }
    }

    // ------------------------------------------------------------------ upload / subir

    /// <summary>Uploads <paramref name="source"/> as <paramref name="folder"/>/<paramref name="name"/> (overwrites). / Sube el contenido (sobrescribe).</summary>
    public async Task UploadAsync(string folder, string name, Stream source, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        var reqid = NewId();
        var started = false;
        try
        {
            await SendAsync(new JsonObject { ["action"] = "upload", ["reqid"] = reqid, ["path"] = folder, ["name"] = name, ["size"] = source.CanSeek ? source.Length - source.Position : 0 },
                "ficheros/subir/conexion-cortada", ct).ConfigureAwait(false);
            var first = await ReceiveJsonAsync(o => o.Str("action") is "uploadstart" or "uploaderror", "ficheros/subir/tiempo-agotado", "ficheros/subir/conexion-cortada", ct).ConfigureAwait(false);
            if (first.Str("action") == "uploaderror")
                throw new MeshClientException(MeshFailure.Rejected, "ficheros/subir/rechazo-remoto", $"The device cannot write \"{name}\" in \"{folder}\" (no permission, or the folder does not exist).");
            started = true;

            var buffer = new byte[UploadBlock + 1];
            long sent = 0;
            var inFlight = 0;
            var eof = false;
            while (true)
            {
                while (!eof && inFlight < UploadWindow)
                {
                    var n = await source.ReadAsync(buffer.AsMemory(1, UploadBlock), ct).ConfigureAwait(false);
                    if (n == 0) { eof = true; break; }
                    // A block that starts with 0 or '{' gets a 0 in front, or the agent would take it for a command.
                    // A un bloque que empieza por 0 o «{» se le pone un 0 delante, o el agente lo tomaría por una orden.
                    var escape = buffer[1] is 0 or (byte)'{';
                    var block = escape ? buffer.AsMemory(0, n + 1) : buffer.AsMemory(1, n);
                    if (escape) buffer[0] = 0;
                    await _tunnel.SendAsync(block, ct).ConfigureAwait(false);
                    inFlight++;
                    sent += n;
                    progress?.Report(sent);
                }
                if (inFlight == 0) break;
                var ack = await ReceiveJsonAsync(o => o.Str("action") is "uploadack" or "uploaderror", "ficheros/subir/tiempo-agotado", "ficheros/subir/conexion-cortada", ct).ConfigureAwait(false);
                if (ack.Str("action") == "uploaderror")
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/subir/rechazo-remoto", $"The device failed writing \"{name}\" (disk full or no permission).");
                inFlight--;
            }
            await SendAsync(new JsonObject { ["action"] = "uploaddone", ["reqid"] = reqid }, "ficheros/subir/conexion-cortada", ct).ConfigureAwait(false);
            await ReceiveJsonAsync(o => o.Str("action") == "uploaddone", "ficheros/subir/tiempo-agotado", "ficheros/subir/conexion-cortada", ct).ConfigureAwait(false);
            started = false;
        }
        finally
        {
            // Cancelled or failed half-way: the agent deletes the partial file. / Cancelada o fallida a medias: el agente borra el fichero a medias.
            if (started) { try { await _tunnel.SendTextAsync(new JsonObject { ["action"] = "uploadcancel", ["reqid"] = reqid }.ToJsonString(), CancellationToken.None).ConfigureAwait(false); } catch (MeshClientException) { } ResetInbox(); }
            _one.Release();
        }
    }

    // ------------------------------------------------------------------ download / bajar

    public async Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        var id = NewId();
        var started = false;
        try
        {
            await SendAsync(new JsonObject { ["action"] = "download", ["sub"] = "start", ["id"] = id, ["path"] = remotePath }, "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
            var first = await ReceiveJsonAsync(o => o.Str("action") == "download" && o.Str("id") == id, "ficheros/descargar/tiempo-agotado", "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
            if (first.Str("sub") != "start")
                throw new MeshClientException(MeshFailure.NotFound, "ficheros/descargar/no-encontrado", $"The device cannot open \"{remotePath}\" (it does not exist, it is a folder, or there is no permission).");
            started = true;
            await SendAsync(new JsonObject { ["action"] = "download", ["sub"] = "startack", ["id"] = id }, "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
            for (var i = 1; i < DownloadWindow; i++)
                await SendAsync(new JsonObject { ["action"] = "download", ["sub"] = "ack", ["id"] = id }, "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
            long received = 0;
            while (true)
            {
                var frame = await ReceiveAsync("ficheros/descargar/tiempo-agotado", "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
                if (frame.Length > 0 && frame.Span[0] == (byte)'{')
                {
                    var o = Parse(frame);
                    if (o?.Str("action") == "download" && o.Str("sub") == "cancel")
                        throw new MeshClientException(MeshFailure.Rejected, "ficheros/descargar/rechazo-remoto", $"The device stopped sending \"{remotePath}\".");
                    continue;
                }
                if (frame.Length < 4) continue;
                var header = BinaryPrimitives.ReadInt32BigEndian(frame.Span);
                if (frame.Length > 4)
                {
                    try { await destination.WriteAsync(frame[4..], ct).ConfigureAwait(false); }
                    catch (IOException ex) { throw new MeshClientException(MeshFailure.Rejected, "ficheros/descargar/permisos-fichero", "The downloaded file could not be written on this PC.", ex); }
                    received += frame.Length - 4;
                    progress?.Report(received);
                }
                if ((header & 1) != 0) { started = false; break; }
                await SendAsync(new JsonObject { ["action"] = "download", ["sub"] = "ack", ["id"] = id }, "ficheros/descargar/conexion-cortada", ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (started) { try { await _tunnel.SendTextAsync(new JsonObject { ["action"] = "download", ["sub"] = "stop", ["id"] = id }.ToJsonString(), CancellationToken.None).ConfigureAwait(false); } catch (MeshClientException) { } ResetInbox(); }
            _one.Release();
        }
    }

    // ------------------------------------------------------------------ plumbing / fontanería

    private async Task SendAsync(JsonObject cmd, string lostKey, CancellationToken ct)
    {
        try { await _tunnel.SendTextAsync(cmd.ToJsonString(), ct).ConfigureAwait(false); }
        catch (MeshClientException ex) when (ex.Failure == MeshFailure.ConnectionLost)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, lostKey, "The connection to the device was lost.", ex);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReceiveAsync(string timeoutKey, string lostKey, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try { return await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MeshClientException(MeshFailure.Timeout, timeoutKey, $"The device did not answer in {_timeout.TotalSeconds:0} s.");
        }
        catch (ChannelClosedException ex)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, lostKey, "The connection to the device was lost.", ex);
        }
    }

    /// <summary>Waits for a JSON reply that matches, skipping anything else. / Espera una respuesta JSON que case, saltándose lo demás.</summary>
    private async Task<JsonObject> ReceiveJsonAsync(Func<JsonObject, bool> match, string timeoutKey, string lostKey, CancellationToken ct)
    {
        var until = DateTime.UtcNow + _timeout;
        while (true)
        {
            if (DateTime.UtcNow > until) throw new MeshClientException(MeshFailure.Timeout, timeoutKey, $"The device did not answer in {_timeout.TotalSeconds:0} s.");
            var frame = await ReceiveAsync(timeoutKey, lostKey, ct).ConfigureAwait(false);
            if (frame.Length == 0 || frame.Span[0] != (byte)'{') continue;
            if (Parse(frame) is { } o && match(o)) return o;
        }
    }

    private static JsonObject? Parse(ReadOnlyMemory<byte> frame)
    {
        try { return JsonNode.Parse(frame.Span) as JsonObject; } catch (JsonException) { return null; }
    }

    /// <summary>After a cancelled transfer, late blocks must not reach the next operation. / Tras cancelar, los bloques tardíos no pueden llegar a la siguiente operación.</summary>
    private void ResetInbox()
    {
        var old = _inbox;
        _inbox = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        old.Writer.TryComplete();
    }

    private static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public ValueTask DisposeAsync() => _tunnel.DisposeAsync();
}
