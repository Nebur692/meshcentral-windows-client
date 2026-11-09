using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>A transfer going on: what, how far (null: unknown) and whether it can stop. / Una transferencia en marcha: qué, por dónde va (null: no se sabe) y si se puede parar.</summary>
public sealed record ClipTransfer(string Text, double? Fraction, bool CanCancel);

/// <summary>
/// Copy and paste of everything (text, images, files) between this PC and a Windows device while its desktop is open,
/// the way Remote Desktop does it: entering the viewer takes what this PC copied to the device (and Ctrl+V there waits for
/// it), leaving the viewer or copying inside it offers here what the device copied, whose files only travel when pasted.
/// Each side remembers the last thing it moved, so nothing bounces back and forth; files going to the device above
/// <see cref="RemoteClipboard.AutomaticLimit"/> wait for the person.
/// Copiar y pegar de todo (texto, imágenes, ficheros) entre este PC y un equipo Windows con su escritorio abierto, como lo
/// hace Escritorio remoto: al entrar en el visor se lleva al equipo lo copiado aquí (y Ctrl+V allí lo espera), al salir o
/// al copiar dentro se ofrece aquí lo que copió el equipo, cuyos ficheros solo viajan al pegar. Cada lado recuerda lo
/// último que movió, así que nada rebota; los ficheros que van al equipo por encima del límite esperan a la persona.
/// </summary>
public sealed class DesktopClipboard : IAsyncDisposable
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly RemoteClipboard _remote;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private uint _localSynced;
    private string? _remoteSynced;
    private Task _push = Task.CompletedTask;
    private bool _toldTextOnly;
    private CancellationTokenSource? _pushing;

    public DesktopClipboard(MeshServerSession session, Device device)
    {
        _session = session;
        _device = device;
        _remote = new RemoteClipboard(session, device);
        // What is already on this PC's clipboard when the desktop opens counts as new. / Lo que ya hay al abrir cuenta como nuevo.
        _localSynced = 0;
        // Versions before 0.13 downloaded here; nothing reads it any more. / Hasta la 0.13 se bajaba aquí; ya nada lo lee.
        _ = Task.Run(() => { try { if (Directory.Exists(OldLocalRoot)) Directory.Delete(OldLocalRoot, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } });
    }

    /// <summary>A short line for the viewer's toolbar, raised on the UI thread. / Una línea corta para la barra del visor, en el hilo de la interfaz.</summary>
    public event Action<string>? Notice;

    /// <summary>A transfer going on, for the viewer's bar, on the UI thread; null when it ends. / Una transferencia en marcha, para la barra del visor, en el hilo de la interfaz; null al acabar.</summary>
    public event Action<ClipTransfer?>? Transfer;

    /// <summary>It turned out that only text can go, on the UI thread. / Resultó que solo puede ir el texto, en el hilo de la interfaz.</summary>
    public event Action? OnlyText;

    public bool CanAll => _remote.CanAll;

    private static string OldLocalRoot => Path.Combine(AppPaths.Local, "clipboard");

    private void Say(string text) => Application.Current?.Dispatcher.BeginInvoke(() => Notice?.Invoke(text));

    private void Show(ClipTransfer? t) => Application.Current?.Dispatcher.BeginInvoke(() => Transfer?.Invoke(t));

    /// <summary>The toolbar's Cancel. / El Cancelar de la barra.</summary>
    public void CancelTransfer() => _pushing?.Cancel();

    // ------------------------------------------------------------------ this PC → device / este PC → equipo

    /// <summary>The viewer became active: takes this PC's clipboard to the device if it changed. Called on the UI thread.
    /// El visor se activó: lleva al equipo el portapapeles de este PC si cambió. Se llama en el hilo de la interfaz.</summary>
    public void Entered()
    {
        if (!_remote.CanText || GetClipboardSequenceNumber() == _localSynced || !_push.IsCompleted) return;
        _push = PushAsync(force: false);
    }

    /// <summary>Ctrl+V on the device waits for this, so it pastes what this PC copied. / Ctrl+V en el equipo espera a esto, para pegar lo copiado aquí.</summary>
    public Task BeforePasteAsync() => _push;

    /// <summary>The toolbar's "take it": files above the limit too. / El «llevar» de la barra: también ficheros por encima del límite.</summary>
    public void PushNow()
    {
        if (!_push.IsCompleted) return;
        _push = PushAsync(force: true);
    }

    private async Task PushAsync(bool force)
    {
        // The clipboard is read here, on the UI thread, before anything awaits. / El portapapeles se lee aquí, en el hilo de la interfaz.
        var seq = GetClipboardSequenceNumber();
        string? text = null; string[]? files = null; BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsFileDropList()) files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            else if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
            else if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (COMException) { return; } // another program holds the clipboard right now / otro programa lo tiene ahora mismo
        if (text is null && files is null && image is null) { _localSynced = seq; return; }

        await _one.WaitAsync(_life.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _pushing = cancel;
        try
        {
            if (text is not null)
            {
                _remoteSynced = await _remote.SetTextAsync(text, cancel.Token);
                Say(L.F("ClipTextToDevice", text.Length));
            }
            else if (!_remote.CanAll)
            {
                if (!_toldTextOnly) Say(L.T("ClipOnlyText"));
                _toldTextOnly = true;
            }
            else if (image is not null)
            {
                Show(new ClipTransfer(L.T("ClipImageGoing"), null, CanCancel: true));
                _remoteSynced = await _remote.SetImageAsync(Png(image), cancel.Token);
                Say(L.F("ClipImageToDevice", image.PixelWidth, image.PixelHeight));
            }
            else if (files is { Length: > 0 })
            {
                var size = LocalSize(files, force ? long.MaxValue : RemoteClipboard.AutomaticLimit + 1);
                if (size > RemoteClipboard.AutomaticLimit && !force) { Say(L.F("ClipTooBigToDevice", ReportFormat.Bytes(size))); return; }
                var meter = new TransferMeter(size);
                var lastShown = 0L;
                var progress = new Reports(p =>
                {
                    meter.Report(p);
                    // A few times a second is all a bar can show. / Unas veces por segundo es lo que una barra puede enseñar.
                    if (Environment.TickCount64 - lastShown < 150) return;
                    lastShown = Environment.TickCount64;
                    Show(new ClipTransfer(L.F("ClipFilesGoing", p.Name), meter.Fraction, CanCancel: true));
                });
                Show(new ClipTransfer(L.F("ClipFilesGoing", Path.GetFileName(files[0].TrimEnd('\\', '/'))), 0, CanCancel: true));
                _remoteSynced = await _remote.SetFilesAsync(files, progress, cancel.Token);
                Say(L.F("ClipFilesToDevice", files.Length));
            }
            _localSynced = seq;
        }
        catch (MeshClientException ex) { Told(ex); }
        // Asked for by the person: it is not offered again until something else is copied. / Lo pidió la persona: no se vuelve a ofrecer hasta que se copie otra cosa.
        catch (OperationCanceledException) when (!_life.IsCancellationRequested) { _localSynced = seq; Say(L.T("ClipCancelled")); }
        catch (OperationCanceledException) { }
        finally { _pushing = null; Show(null); _one.Release(); }
    }

    private sealed class Reports(Action<TreeProgress> report) : IProgress<TreeProgress>
    {
        public void Report(TreeProgress value) => report(value);
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static long LocalSize(IEnumerable<string> paths, long stopAt)
    {
        long size = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) size += new FileInfo(p).Length;
                else if (Directory.Exists(p))
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)) { size += new FileInfo(f).Length; if (size > stopAt) return size; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (size > stopAt) return size;
        }
        return size;
    }

    // ------------------------------------------------------------------ device → this PC / equipo → este PC

    /// <summary>The viewer was left, or something was copied inside it, or the toolbar's "bring it": offers here what the device copied.
    /// Se salió del visor, o se copió algo dentro, o el «traer» de la barra: ofrece aquí lo que copió el equipo.</summary>
    public void Left()
    {
        if (_remote.CanAll) _ = PullAsync();
        else if (_remote.CanText) _ = PullTextAsync();
    }

    /// <summary>Only text, which needs nothing of the user's: rights without terminal or files, or a restricted PowerShell.
    /// Solo texto, que no necesita nada del usuario: permisos sin terminal o ficheros, o un PowerShell restringido.</summary>
    private async Task PullTextAsync()
    {
        try { await _push; } catch (Exception) { }
        await _one.WaitAsync(_life.Token);
        try
        {
            var text = await _remote.ReadTextAsync(_life.Token);
            if (text is null || RemoteClipboard.TextSignature(text) == _remoteSynced) return;
            await OnUi(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));
            Say(L.F("ClipTextFromDevice", text.Length));
            _remoteSynced = RemoteClipboard.TextSignature(text);
            _localSynced = await OnUi(GetClipboardSequenceNumber);
        }
        catch (MeshClientException ex) { Say(UserError.From(ex, _session.ServerInfo.Version).Message); }
        catch (COMException) { Say(L.T("ClipBusy")); }
        catch (OperationCanceledException) { }
        finally { _one.Release(); }
    }

    /// <summary>What a failure says; a PowerShell found restricted is said once and leaves text only.
    /// Lo que dice un fallo; un PowerShell que resulta restringido se dice una vez y deja solo el texto.</summary>
    private void Told(MeshClientException ex)
    {
        if (_remote.CanAll || !_remote.CanText) { Say(UserError.From(ex, _session.ServerInfo.Version).Message); return; }
        if (!_toldTextOnly) Say(L.T("ClipOnlyText"));
        _toldTextOnly = true;
        Application.Current?.Dispatcher.BeginInvoke(() => OnlyText?.Invoke());
    }

    private async Task PullAsync()
    {
        try { await _push; } catch (Exception) { }
        await _one.WaitAsync(_life.Token);
        var leftForText = false;
        try
        {
            var clip = await _remote.ReadAsync(_life.Token);
            if (clip.Kind == ClipKind.None || clip.Signature == _remoteSynced) return;
            switch (clip.Kind)
            {
                case ClipKind.Text:
                    await OnUi(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, clip.Text ?? ""), copy: true));
                    Say(L.F("ClipTextFromDevice", (clip.Text ?? "").Length));
                    break;
                case ClipKind.Image:
                    Show(new ClipTransfer(L.T("ClipImageComing"), null, CanCancel: false));
                    var png = await _remote.DownloadImageAsync(clip, _life.Token);
                    await OnUi(() =>
                    {
                        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        Clipboard.SetImage(frame);
                    });
                    Say(L.T("ClipImageFromDevice"));
                    break;
                case ClipKind.Files:
                    Show(new ClipTransfer(L.T("ClipFilesListing"), null, CanCancel: false));
                    var source = new RemoteFileSource(_session, _device.Id);
                    source.Failed += ex => Say(UserError.From(ex, _session.ServerInfo.Version).Message);
                    try
                    {
                        var entries = await source.ListAsync(clip.Files, _life.Token);
                        if (entries.Any(e => e.RelativePath.Length > VirtualFilesData.MaxPath)) { await source.DisposeAsync(); Say(L.T("ClipPathTooLong")); return; }
                        await VirtualFileClipboard.SetAsync(entries, source);
                    }
                    catch (Exception) { await source.DisposeAsync(); throw; }
                    Say(L.F("ClipFilesFromDevice", clip.Files.Count));
                    break;
            }
            _remoteSynced = clip.Signature;
            _localSynced = await OnUi(GetClipboardSequenceNumber);
        }
        catch (MeshClientException ex)
        {
            Told(ex);
            // What leaves text only still brings the text of this time. / Lo que deja solo el texto aún trae el texto de esta vez.
            if (!_remote.CanAll && _remote.CanText) leftForText = true;
        }
        catch (COMException) { Say(L.T("ClipBusy")); }
        catch (OperationCanceledException) { }
        finally { Show(null); _one.Release(); }
        if (leftForText) await PullTextAsync();
    }

    // The clipboard belongs to the UI thread; it may be busy a moment, so it is tried a few times.
    // El portapapeles es del hilo de la interfaz; puede estar ocupado un momento, así que se reintenta.
    private static Task OnUi(Action a) => OnUi(() => { a(); return 0; });

    private static async Task<T> OnUi<T>(Func<T> f)
    {
        for (var i = 0; ; i++)
        {
            try { return await Application.Current.Dispatcher.InvokeAsync(f); }
            catch (COMException) when (i < 5) { await Task.Delay(100); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        await _remote.DisposeAsync();
    }
}
