using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>
/// Copy and paste of everything (text, images, files) between this PC and a Windows device while its desktop is open,
/// the way Remote Desktop does it: entering the viewer takes what this PC copied to the device (and Ctrl+V there waits for
/// it), leaving the viewer or copying inside it brings back what the device copied. Each side remembers the last thing it
/// moved, so nothing bounces back and forth; files above <see cref="RemoteClipboard.AutomaticLimit"/> wait for the person.
/// Copiar y pegar de todo (texto, imágenes, ficheros) entre este PC y un equipo Windows con su escritorio abierto, como lo
/// hace Escritorio remoto: al entrar en el visor se lleva al equipo lo copiado aquí (y Ctrl+V allí lo espera), al salir o
/// al copiar dentro se trae lo que copió el equipo. Cada lado recuerda lo último que movió, así que nada rebota; los
/// ficheros por encima del límite esperan a que la persona lo pida.
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

    public DesktopClipboard(MeshServerSession session, Device device)
    {
        _session = session;
        _device = device;
        _remote = new RemoteClipboard(session, device);
        // What is already on this PC's clipboard when the desktop opens counts as new. / Lo que ya hay al abrir cuenta como nuevo.
        _localSynced = 0;
    }

    /// <summary>A short line for the viewer's toolbar, raised on the UI thread. / Una línea corta para la barra del visor, en el hilo de la interfaz.</summary>
    public event Action<string>? Notice;

    public bool CanAll => _remote.CanAll;

    private static string LocalRoot => Path.Combine(AppPaths.Local, "clipboard", "desktop");

    private void Say(string text) => Application.Current?.Dispatcher.BeginInvoke(() => Notice?.Invoke(text));

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
        try
        {
            if (text is not null)
            {
                _remoteSynced = await _remote.SetTextAsync(text, _life.Token);
                Say(L.F("ClipTextToDevice", text.Length));
            }
            else if (!_remote.CanAll)
            {
                if (!_toldTextOnly) Say(L.T("ClipOnlyText"));
                _toldTextOnly = true;
            }
            else if (image is not null)
            {
                Say(L.T("ClipImageGoing"));
                _remoteSynced = await _remote.SetImageAsync(Png(image), _life.Token);
                Say(L.F("ClipImageToDevice", image.PixelWidth, image.PixelHeight));
            }
            else if (files is { Length: > 0 })
            {
                var size = LocalSize(files, RemoteClipboard.AutomaticLimit + 1);
                if (size > RemoteClipboard.AutomaticLimit && !force) { Say(L.F("ClipTooBigToDevice", ReportFormat.Bytes(size))); return; }
                var progress = new Progress<TreeProgress>(p => Say(L.F("ClipFilesGoing", p.Name)));
                _remoteSynced = await _remote.SetFilesAsync(files, progress, _life.Token);
                Say(L.F("ClipFilesToDevice", files.Length));
            }
            _localSynced = seq;
        }
        catch (MeshClientException ex) { Say(UserError.From(ex, _session.ServerInfo.Version).Message); }
        catch (OperationCanceledException) { }
        finally { _one.Release(); }
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

    /// <summary>The viewer was left, or something was copied inside it: brings what the device copied.
    /// Se salió del visor, o se copió algo dentro: trae lo que copió el equipo.</summary>
    public void Left() { if (_remote.CanAll) _ = PullAsync(force: false); }

    /// <summary>The toolbar's "bring it": files above the limit too. / El «traer» de la barra: también ficheros por encima del límite.</summary>
    public void PullNow() { if (_remote.CanAll) _ = PullAsync(force: true); }

    private async Task PullAsync(bool force)
    {
        try { await _push; } catch (Exception) { }
        await _one.WaitAsync(_life.Token);
        try
        {
            var clip = await _remote.ReadAsync(force ? long.MaxValue : RemoteClipboard.AutomaticLimit, _life.Token);
            if (clip.Kind == ClipKind.None || clip.Signature == _remoteSynced) return;
            switch (clip.Kind)
            {
                case ClipKind.Text:
                    await OnUi(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, clip.Text ?? ""), copy: true));
                    Say(L.F("ClipTextFromDevice", (clip.Text ?? "").Length));
                    break;
                case ClipKind.Image:
                    Say(L.T("ClipImageComing"));
                    var png = await _remote.DownloadImageAsync(clip, _life.Token);
                    await OnUi(() =>
                    {
                        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        Clipboard.SetImage(frame);
                    });
                    Say(L.T("ClipImageFromDevice"));
                    break;
                case ClipKind.Files when clip.SizeOverLimit && !force:
                    Say(L.F("ClipTooBigFromDevice", ReportFormat.Bytes(clip.Size)));
                    return;
                case ClipKind.Files:
                    var folder = Path.Combine(LocalRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                    Clean(LocalRoot);
                    var progress = new Progress<TreeProgress>(p => Say(L.F("ClipFilesComing", p.Name)));
                    var local = await _remote.DownloadFilesAsync(clip, folder, progress, _life.Token);
                    await OnUi(() =>
                    {
                        var drop = new StringCollection();
                        drop.AddRange(local.ToArray());
                        var data = new DataObject();
                        data.SetFileDropList(drop);
                        Clipboard.SetDataObject(data, copy: true);
                    });
                    Say(L.F("ClipFilesFromDevice", local.Count));
                    break;
            }
            _remoteSynced = clip.Signature;
            _localSynced = await OnUi(GetClipboardSequenceNumber);
        }
        catch (MeshClientException ex) { Say(UserError.From(ex, _session.ServerInfo.Version).Message); }
        catch (COMException) { Say(L.T("ClipBusy")); }
        catch (OperationCanceledException) { }
        finally { _one.Release(); }
    }

    /// <summary>Removes earlier copies from the device: a new copy replaces them on the clipboard.
    /// Quita las copias anteriores del equipo: una copia nueva las sustituye en el portapapeles.</summary>
    private static void Clean(string root)
    {
        try { if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root)) Directory.Delete(d, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
