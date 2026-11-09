using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Threading;
using MeshCentralClient.Core;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace MeshCentralClient.App;

/// <summary>
/// What Remote Desktop puts on the clipboard: not files but a promise of them (virtual files). Windows asks for each one's
/// bytes only when something is pasted, so Explorer shows its own copy window, with its bar and its Cancel.
/// Lo que Escritorio remoto pone en el portapapeles: no ficheros sino la promesa de ellos (ficheros virtuales). Windows
/// pide los bytes de cada uno solo al pegar, así que el Explorador enseña su propia ventana de copia, con su barra y su
/// Cancelar.
/// </summary>
internal sealed class VirtualFilesData(IReadOnlyList<RemoteClipEntry> entries, Func<RemoteClipEntry, Stream> open) : IDataObject
{
    internal const int MaxPath = 259; // cFileName holds 260 with its end / cFileName caben 260 con su final
    private const int DescriptorSize = 592;
    private const uint FdAttributes = 0x4, FdWritesTime = 0x20, FdFileSize = 0x40, FdProgressUi = 0x4000, FdUnicode = 0x80000000;
    private const int DvEFormatEtc = unchecked((int)0x80040064), DvELIndex = unchecked((int)0x80040068), OleEAdviseNotSupported = unchecked((int)0x80040003), ENotImpl = unchecked((int)0x80004001), DataSSameFormatEtc = 0x00040130;

    internal static readonly short Descriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    internal static readonly short Contents = (short)RegisterClipboardFormat("FileContents");
    private static readonly short PreferredEffect = (short)RegisterClipboardFormat("Preferred DropEffect");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, [In] FORMATETC[] formats, out IEnumFORMATETC e);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    private static FORMATETC Format(short id, TYMED tymed, int index = -1) =>
        new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed };

    private static bool Is(in FORMATETC f, short id, TYMED tymed) => f.cfFormat == id && (f.tymed & tymed) != 0;

    /// <summary>FILEGROUPDESCRIPTORW: how many, and a fixed block for each. / Cuántos, y un bloque fijo por cada uno.</summary>
    internal static byte[] DescriptorBytes(IReadOnlyList<RemoteClipEntry> entries)
    {
        var data = new byte[4 + entries.Count * DescriptorSize];
        BitConverter.TryWriteBytes(data, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var at = data.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            var flags = FdAttributes | FdFileSize | FdProgressUi | FdUnicode | (e.Modified is null ? 0 : FdWritesTime);
            BitConverter.TryWriteBytes(at, flags);
            BitConverter.TryWriteBytes(at[36..], e.IsFolder ? 0x10 : 0x80); // FILE_ATTRIBUTE_DIRECTORY / NORMAL
            if (e.Modified is { } m) BitConverter.TryWriteBytes(at[56..], m.ToFileTime());
            BitConverter.TryWriteBytes(at[64..], (uint)(e.Size >> 32));
            BitConverter.TryWriteBytes(at[68..], (uint)e.Size);
            System.Text.Encoding.Unicode.GetBytes(e.RelativePath, at[72..]);
        }
        return data;
    }

    private static STGMEDIUM Global(byte[] bytes)
    {
        var h = Marshal.AllocHGlobal(bytes.Length); // GMEM_FIXED: GlobalLock gives this same address / GlobalLock da esta misma dirección
        Marshal.Copy(bytes, 0, h, bytes.Length);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (Is(format, Descriptor, TYMED.TYMED_HGLOBAL)) { medium = Global(DescriptorBytes(entries)); return; }
        if (Is(format, PreferredEffect, TYMED.TYMED_HGLOBAL)) { medium = Global(BitConverter.GetBytes(1)); return; } // DROPEFFECT_COPY
        if (Is(format, Contents, TYMED.TYMED_ISTREAM))
        {
            if (format.lindex < 0 || format.lindex >= entries.Count || entries[format.lindex].IsFolder) Marshal.ThrowExceptionForHR(DvELIndex);
            var e = entries[format.lindex];
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject<ComStream, IStream>(new ComStream(open(e), e)) };
            return;
        }
        Marshal.ThrowExceptionForHR(DvEFormatEtc);
        medium = default;
    }

    public int QueryGetData(ref FORMATETC format) =>
        Is(format, Descriptor, TYMED.TYMED_HGLOBAL) || Is(format, PreferredEffect, TYMED.TYMED_HGLOBAL) || Is(format, Contents, TYMED.TYMED_ISTREAM) ? 0 : DvEFormatEtc;

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) Marshal.ThrowExceptionForHR(ENotImpl);
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc(3, new[] { Format(Descriptor, TYMED.TYMED_HGLOBAL), Format(Contents, TYMED.TYMED_ISTREAM), Format(PreferredEffect, TYMED.TYMED_HGLOBAL) }, out var e));
        return e;
    }

    // Explorer tells how the paste went ("Performed DropEffect", "Paste Succeeded"): nothing here depends on it.
    // El Explorador cuenta cómo fue el pegado: aquí nada depende de eso.
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Marshal.ThrowExceptionForHR(DvEFormatEtc);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; formatOut.ptd = IntPtr.Zero; return DataSSameFormatEtc; }
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OleEAdviseNotSupported; }
    public void DUnadvise(int connection) => Marshal.ThrowExceptionForHR(OleEAdviseNotSupported);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return OleEAdviseNotSupported; }

    /// <summary>A read-only stream as COM wants it; what fails here reaches Explorer, which says so. / Un flujo de solo lectura como lo quiere COM; lo que falla aquí llega al Explorador, que lo dice.</summary>
    internal sealed class ComStream(Stream inner, RemoteClipEntry entry) : IStream
    {
        private const int StatFlagNoName = 1, StgTyStream = 2;
        private long _position;

        ~ComStream() => inner.Dispose();

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            // Explorer may ask for less than a block is worth: reads go on until cb, or the end. / Se lee hasta cb o hasta el final.
            var total = 0;
            while (total < cb)
            {
                var n = inner.Read(pv, total, cb - total);
                if (n == 0) break;
                total += n;
            }
            _position += total;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            var target = dwOrigin switch { 0 => dlibMove, 1 => _position + dlibMove, _ => entry.Size + dlibMove };
            if (target != _position) Marshal.ThrowExceptionForHR(ENotImpl); // it only goes forwards / solo va hacia delante
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG
        {
            type = StgTyStream, cbSize = entry.Size, grfMode = 0,
            pwcsName = (grfStatFlag & StatFlagNoName) != 0 ? null! : Path.GetFileName(entry.RelativePath),
        };

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => Marshal.ThrowExceptionForHR(ENotImpl);
        public void Clone(out IStream ppstm) { ppstm = null!; Marshal.ThrowExceptionForHR(ENotImpl); }
        public void Commit(int grfCommitFlags) { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => Marshal.ThrowExceptionForHR(ENotImpl);
        public void Revert() { }
        public void SetSize(long libNewSize) => Marshal.ThrowExceptionForHR(ENotImpl);
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => Marshal.ThrowExceptionForHR(ENotImpl);
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => Marshal.ThrowExceptionForHR(ENotImpl);
    }
}

/// <summary>
/// Owns the virtual files on the clipboard, from a thread of its own: pasted bytes are asked for on the thread that put them
/// there, and the viewer must not wait for them. It outlives the viewer, as what was copied can be pasted after closing it.
/// Tiene los ficheros virtuales del portapapeles, en un hilo propio: los bytes se piden en el hilo que los puso, y el visor
/// no debe esperarlos. Dura más que el visor, porque lo copiado se puede pegar después de cerrarlo.
/// </summary>
internal static class VirtualFileClipboard
{
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(IDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(IDataObject data);

    private static readonly Lazy<Dispatcher> Thread_ = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var t = new Thread(() => { OleInitialize(IntPtr.Zero); ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "clipboard" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    private static IDataObject? _current;
    private static RemoteFileSource? _source;

    /// <summary>Puts them on the clipboard, in place of the earlier ones. / Los pone en el portapapeles, en lugar de los anteriores.</summary>
    public static async Task SetAsync(IReadOnlyList<RemoteClipEntry> entries, RemoteFileSource source)
    {
        RemoteFileSource? old = null;
        await Thread_.Value.InvokeAsync(() =>
        {
            var data = new VirtualFilesData(entries, source.Open);
            var hr = 0;
            for (var i = 0; i < 10; i++)
            {
                hr = OleSetClipboard(data);
                if (hr == 0) break;
                Thread.Sleep(100); // another program holds it for a moment / otro programa lo tiene un momento
            }
            if (hr != 0) throw new COMException("The clipboard is busy.", hr);
            old = _source;
            _current = data;
            _source = source;
        });
        if (old is not null) await old.DisposeAsync();
    }

    /// <summary>Signing out: what can no longer be read leaves the clipboard. / Al salir: lo que ya no se puede leer sale del portapapeles.</summary>
    public static async Task ClearAsync()
    {
        if (!Thread_.IsValueCreated) return;
        RemoteFileSource? old = null;
        await Thread_.Value.InvokeAsync(() =>
        {
            if (_current is not null && OleIsCurrentClipboard(_current) == 0) OleSetClipboard(null);
            old = _source;
            _current = null;
            _source = null;
        });
        if (old is not null) await old.DisposeAsync();
    }
}

/// <summary>
/// How far a transfer of several files is, out of the size of them all. A new name, or a count going back to zero, means the
/// previous file ended.
/// Por dónde va una transferencia de varios ficheros, sobre el tamaño de todos. Un nombre nuevo, o una cuenta que vuelve a
/// cero, significa que el fichero anterior acabó.
/// </summary>
internal sealed class TransferMeter(long total)
{
    private long _finished, _current;
    private string? _name;

    public double Fraction { get; private set; }

    public void Report(TreeProgress p)
    {
        if (p.Name != _name || p.Done < _current) { _finished += _current; _current = 0; _name = p.Name; }
        _current = p.Done;
        Fraction = total <= 0 ? 0 : Math.Min(1, (double)(_finished + _current) / total);
    }
}
