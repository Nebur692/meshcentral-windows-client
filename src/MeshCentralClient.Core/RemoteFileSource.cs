using System.IO.Pipelines;

namespace MeshCentralClient.Core;

/// <summary>One thing inside what was copied on the device. / Una cosa de lo que se copió en el equipo.</summary>
/// <param name="RelativePath">Where it goes under the paste target, with backslashes. / Dónde va bajo el destino de pegar, con barras invertidas.</param>
/// <param name="RemotePath">Its path on the device. / Su ruta en el equipo.</param>
public sealed record RemoteClipEntry(string RelativePath, string RemotePath, bool IsFolder, long Size, DateTimeOffset? Modified);

/// <summary>
/// Files of a device read only when somebody asks for them, as Remote Desktop does: copying costs one listing, and the
/// bytes travel while they are pasted. A files tunnel carries one transfer at a time, so each stream takes a free tunnel or
/// opens one; a stream abandoned half-way loses its tunnel, whose frames could no longer be told from the next file's.
/// Ficheros de un equipo que solo se leen cuando alguien los pide, como hace Escritorio remoto: copiar cuesta un listado,
/// y los bytes viajan mientras se pegan. Un túnel de ficheros lleva una transferencia a la vez, así que cada flujo coge un
/// túnel libre o abre otro; un flujo abandonado a medias pierde su túnel, cuyos mensajes ya no se distinguirían de los del
/// fichero siguiente.
/// </summary>
public sealed class RemoteFileSource(MeshServerSession session, string nodeId) : IAsyncDisposable
{
    private readonly Stack<FilesSession> _free = new();
    private readonly CancellationTokenSource _life = new();

    /// <summary>A transfer failed while pasting. / Una transferencia falló mientras se pegaba.</summary>
    public event Action<MeshClientException>? Failed;

    /// <summary>Everything under <paramref name="paths"/> ("C:\a\b" or "C:/a/b"), parents before what they hold.
    /// Todo lo que hay bajo esas rutas, cada carpeta antes de lo que contiene.</summary>
    public async Task<IReadOnlyList<RemoteClipEntry>> ListAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var files = await TakeAsync(ct).ConfigureAwait(false);
        try
        {
            var all = new List<RemoteClipEntry>();
            foreach (var group in paths.Select(p => p.Replace('\\', '/').TrimEnd('/')).GroupBy(RemoteClipboard.Parent))
            {
                var listing = await files.ListAsync(group.Key, ct).ConfigureAwait(false);
                var names = group.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
                foreach (var e in listing.Entries.Where(e => names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
                    await AddAsync(files, group.Key, "", e, all, ct).ConfigureAwait(false);
            }
            Give(files);
            return all;
        }
        catch (Exception) { await files.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task AddAsync(FilesSession files, string folder, string under, RemoteEntry e, List<RemoteClipEntry> all, CancellationToken ct)
    {
        var remote = FilesSession.JoinPath(folder, e.Name);
        var relative = under.Length == 0 ? e.Name : under + "\\" + e.Name;
        var isFolder = e.Kind != RemoteEntryKind.File;
        all.Add(new RemoteClipEntry(relative, remote, isFolder, isFolder ? 0 : e.Size, e.Modified));
        if (!isFolder) return;
        foreach (var inside in (await files.ListAsync(remote, ct).ConfigureAwait(false)).Entries)
            await AddAsync(files, remote, relative, inside, all, ct).ConfigureAwait(false);
    }

    /// <summary>The bytes of a file, which start travelling at the first read. / Los bytes de un fichero, que empiezan a viajar con la primera lectura.</summary>
    public RemoteFileStream Open(RemoteClipEntry entry) => new(this, entry.RemotePath, entry.Size);

    internal async Task PumpAsync(string remotePath, Stream to, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var files = await TakeAsync(both.Token).ConfigureAwait(false);
        try { await files.DownloadAsync(remotePath, to, null, both.Token).ConfigureAwait(false); }
        catch (Exception) { await files.DisposeAsync().ConfigureAwait(false); throw; }
        Give(files);
    }

    internal void Report(MeshClientException failure) => Failed?.Invoke(failure);

    private async Task<FilesSession> TakeAsync(CancellationToken ct)
    {
        lock (_free)
        {
            while (_free.TryPop(out var f)) if (f.Tunnel.IsOpen) return f;
        }
        return await FilesSession.OpenAsync(session, nodeId, ct).ConfigureAwait(false);
    }

    private void Give(FilesSession files) { lock (_free) _free.Push(files); }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        FilesSession[] free;
        lock (_free) { free = _free.ToArray(); _free.Clear(); }
        foreach (var f in free) await f.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A file of the device as a stream to read, one way only. At most a few MB wait here, so a reader that pauses pauses the
/// device too; a reader that disappears without saying so (Windows only lets go of its stream whenever it likes) stops the
/// transfer after <see cref="Abandoned"/>.
/// Un fichero del equipo como flujo de lectura, en un solo sentido. Aquí esperan como mucho unos MB, así que quien lee y
/// se para, para también al equipo; quien desaparece sin decirlo (Windows suelta su flujo cuando quiere) detiene la
/// transferencia pasado el tiempo de abandono.
/// </summary>
public sealed class RemoteFileStream : Stream
{
    internal static readonly TimeSpan DefaultAbandoned = TimeSpan.FromMinutes(5);
    private readonly RemoteFileSource _source;
    private readonly string _path;
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 4 * 1024 * 1024, useSynchronizationContext: false));
    private readonly CancellationTokenSource _stop = new();
    private Timer? _watch;
    private long _lastRead;
    private long _read;
    private bool _started, _done;

    internal RemoteFileStream(RemoteFileSource source, string path, long size)
    {
        _source = source;
        _path = path;
        Length = size;
    }

    internal TimeSpan Abandoned { get; init; } = DefaultAbandoned;

    public override long Length { get; }
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    private void Start()
    {
        _started = true;
        Volatile.Write(ref _lastRead, Environment.TickCount64);
        var every = TimeSpan.FromTicks(Math.Max(Abandoned.Ticks / 4, TimeSpan.FromMilliseconds(50).Ticks));
        _watch = new Timer(_ => { if (Environment.TickCount64 - Volatile.Read(ref _lastRead) > Abandoned.TotalMilliseconds) _stop.Cancel(); }, null, every, every);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await using var writer = _pipe.Writer.AsStream(leaveOpen: true);
                await _source.PumpAsync(_path, writer, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; }
            if (failure is MeshClientException m) _source.Report(m);
            await _pipe.Writer.CompleteAsync(failure).ConfigureAwait(false);
            _watch?.Dispose();
        });
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_done || count == 0) return 0;
        if (!_started) Start();
        Volatile.Write(ref _lastRead, Environment.TickCount64);
        var r = _pipe.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Volatile.Write(ref _lastRead, Environment.TickCount64);
        var n = (int)Math.Min(r.Buffer.Length, count);
        var taken = r.Buffer.Slice(0, n);
        System.Buffers.BuffersExtensions.CopyTo(taken, buffer.AsSpan(offset, n));
        _pipe.Reader.AdvanceTo(taken.End);
        _read += n;
        if (n == 0 && r.IsCompleted) _done = true;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _done = true;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            _watch?.Dispose();
            try { _pipe.Reader.Complete(); } catch (InvalidOperationException) { }
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
