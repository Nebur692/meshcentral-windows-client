using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Input;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

namespace MeshCentralClient.App.Tests;

public class VirtualFilesTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 9, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_descriptor_is_laid_out_as_windows_reads_it()
    {
        var bytes = VirtualFilesData.DescriptorBytes(new RemoteClipEntry[]
        {
            new("carpeta", "C:/carpeta", true, 0, null),
            new("carpeta\\ñandú.bin", "C:/carpeta/ñandú.bin", false, 0x1_0000_0002, When),
        });
        Assert.Equal(4 + 2 * 592, bytes.Length);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 0));

        const int folder = 4, file = 4 + 592;
        Assert.Equal(0x10, BitConverter.ToInt32(bytes, folder + 36)); // FILE_ATTRIBUTE_DIRECTORY
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, folder) & 0x20); // no date, no flag / sin fecha, sin marca
        Assert.Equal("carpeta", Encoding.Unicode.GetString(bytes, folder + 72, 520).TrimEnd('\0'));

        var flags = BitConverter.ToUInt32(bytes, file);
        foreach (var flag in new[] { 0x4u, 0x20u, 0x40u, 0x4000u, 0x80000000u }) Assert.Equal(flag, flags & flag);
        Assert.Equal(0x80, BitConverter.ToInt32(bytes, file + 36));
        Assert.Equal(When.ToFileTime(), BitConverter.ToInt64(bytes, file + 56));
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, file + 64));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, file + 68));
        Assert.Equal("carpeta\\ñandú.bin", Encoding.Unicode.GetString(bytes, file + 72, 520).TrimEnd('\0'));
    }

    private static readonly RemoteClipEntry Entry = new("d\\a.bin", "C:/d/a.bin", false, 5, When);

    private static (int Read, byte[] Data) Read(IStream s, int count)
    {
        var data = new byte[count];
        var p = Marshal.AllocHGlobal(4);
        try { s.Read(data, count, p); return (Marshal.ReadInt32(p), data); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void The_stream_gives_the_bytes_says_its_size_and_only_goes_forwards()
    {
        IStream s = new VirtualFilesData.ComStream(new MemoryStream("12345"u8.ToArray()), Entry);
        s.Stat(out var stat, 0);
        Assert.Equal(5, stat.cbSize);
        Assert.Equal("a.bin", stat.pwcsName);
        s.Stat(out var noName, 1);
        Assert.Null(noName.pwcsName);

        var (n, data) = Read(s, 2);
        Assert.Equal(2, n);
        Assert.Equal("12"u8.ToArray(), data);

        var p = Marshal.AllocHGlobal(8);
        try
        {
            s.Seek(0, 1, p); // where am I / dónde estoy
            Assert.Equal(2, Marshal.ReadInt64(p));
            Assert.ThrowsAny<Exception>(() => s.Seek(0, 0, p)); // back to the start / volver al principio
        }
        finally { Marshal.FreeHGlobal(p); }

        // A read bigger than what is left gets what is left, then nothing. / Una lectura mayor que lo que queda da lo que queda, y luego nada.
        Assert.Equal(3, Read(s, 100).Read);
        Assert.Equal(0, Read(s, 100).Read);
    }

    /// <summary>Hands out a few bytes at a time, as the network does. / Da unos pocos bytes cada vez, como la red.</summary>
    private sealed class Trickle(byte[] all) : MemoryStream(all)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 2));
    }

    [Fact]
    public void A_read_is_filled_though_the_network_hands_little_at_a_time()
    {
        IStream s = new VirtualFilesData.ComStream(new Trickle("12345"u8.ToArray()), Entry);
        // A short read means the end to Windows, not "come back later". / Una lectura corta significa el final para Windows, no «vuelve luego».
        Assert.Equal(5, Read(s, 5).Read);
    }

    [Fact]
    public void A_folder_has_no_bytes_to_ask_for_and_a_stranger_format_is_refused()
    {
        var entries = new RemoteClipEntry[] { new("d", "C:/d", true, 0, null), Entry };
        var opened = new List<string>();
        var data = new VirtualFilesData(entries, e => { opened.Add(e.RemotePath); return new MemoryStream(); });
        var folder = new FORMATETC { cfFormat = VirtualFilesData.Contents, lindex = 0, tymed = TYMED.TYMED_ISTREAM, dwAspect = DVASPECT.DVASPECT_CONTENT };
        Assert.ThrowsAny<Exception>(() => data.GetData(ref folder, out _));
        var past = folder; past.lindex = 2;
        Assert.ThrowsAny<Exception>(() => data.GetData(ref past, out _));
        Assert.Empty(opened);

        var file = folder; file.lindex = 1;
        data.GetData(ref file, out var medium);
        Assert.Equal(TYMED.TYMED_ISTREAM, medium.tymed);
        Assert.Equal(new[] { "C:/d/a.bin" }, opened);
        Marshal.Release(medium.unionmember);

        var text = new FORMATETC { cfFormat = 13, lindex = -1, tymed = TYMED.TYMED_HGLOBAL, dwAspect = DVASPECT.DVASPECT_CONTENT };
        Assert.NotEqual(0, data.QueryGetData(ref text));
        Assert.Equal(0, data.QueryGetData(ref file));
    }

    [Fact]
    public void The_meter_counts_every_file_and_a_second_file_of_the_same_name()
    {
        var m = new TransferMeter(100);
        m.Report(new("a", 10, 40));
        Assert.Equal(0.10, m.Fraction, 3);
        m.Report(new("a", 40, 40));
        m.Report(new("b", 5, 30));
        Assert.Equal(0.45, m.Fraction, 3);
        m.Report(new("b", 30, 30));
        m.Report(new("b", 10, 30)); // another "b" in another folder / otra «b» en otra carpeta
        Assert.Equal(0.80, m.Fraction, 3);
        m.Report(new("c", 500, 500));
        Assert.Equal(1, m.Fraction, 3); // never past the end / nunca más allá del final
        Assert.Equal(0, new TransferMeter(0).Fraction);
    }

    [Theory]
    [InlineData(Key.Pause, ModifierKeys.Control | ModifierKeys.Alt, true)]
    [InlineData(Key.Cancel, ModifierKeys.Control | ModifierKeys.Alt, true)]
    [InlineData(Key.Pause, ModifierKeys.Control, false)]
    [InlineData(Key.Pause, ModifierKeys.None, false)]
    [InlineData(Key.F11, ModifierKeys.Control | ModifierKeys.Alt, false)]
    public void What_counts_as_full_screen(Key key, ModifierKeys mods, bool expected) =>
        Assert.Equal(expected, DesktopKeys.IsFullScreen(key, mods));
}
