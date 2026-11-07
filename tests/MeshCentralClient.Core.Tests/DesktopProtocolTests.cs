using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// The desktop stream and input commands, byte for byte against what agent-desktop-0.0.2.js / agent-redir-ws-0.1.1.js
/// of MeshCentral 1.3.1 send and accept. The real thing is tested on Windows (tests/ui), where there is a screen.
/// El flujo de escritorio y las órdenes de entrada, byte a byte contra lo que mandan y aceptan agent-desktop-0.0.2.js /
/// agent-redir-ws-0.1.1.js de MeshCentral 1.3.1. Lo de verdad se prueba en Windows (tests/ui), donde hay pantalla.
/// </summary>
public class DesktopProtocolTests
{
    private static (List<(int Cmd, byte[] Data)> Got, DesktopFrameParser P) Parser()
    {
        var got = new List<(int, byte[])>();
        return (got, new DesktopFrameParser((c, d) => got.Add((c, d))));
    }

    private static byte[] Cmd(int cmd, params byte[] payload)
    {
        var size = 4 + payload.Length;
        return new byte[] { (byte)(cmd >> 8), (byte)cmd, (byte)(size >> 8), (byte)size }.Concat(payload).ToArray();
    }

    [Fact]
    public void A_whole_command_and_its_padding()
    {
        var (got, p) = Parser();
        p.Feed(Cmd(7, 0x04, 0x00, 0x03, 0x00).Concat(new byte[] { 0, 0, 0, 0 }).ToArray()); // 8 + 4 padding / 8 + 4 de relleno
        var (cmd, data) = Assert.Single(got);
        Assert.Equal(7, cmd);
        Assert.Equal(8, data.Length); // padding dropped / sin el relleno
    }

    [Fact]
    public void A_command_cut_across_frames()
    {
        var (got, p) = Parser();
        var tile = Cmd(3, Enumerable.Range(0, 30000).Select(i => (byte)i).ToArray());
        p.Feed(tile.AsSpan(0, 1000));
        p.Feed(tile.AsSpan(1000, 20000));
        Assert.Empty(got);
        p.Feed(tile.AsSpan(21000));
        var (cmd, data) = Assert.Single(got);
        Assert.Equal(3, cmd);
        Assert.Equal(tile, data);
    }

    [Fact]
    public void A_jumbo_command_over_64_kb()
    {
        var (got, p) = Parser();
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var size = 8 + payload.Length; // the inner command: cmd(2) size(2, unused) x(2) y(2) + jpeg
        var inner = new byte[] { 0, 3, 0, 0, 0, 10, 0, 20 }.Concat(payload).ToArray();
        var jumbo = new byte[] { 0, 27, 0, 8, 0, (byte)(size >> 16), (byte)(size >> 8), (byte)size }.Concat(inner).ToArray();
        p.Feed(jumbo.AsSpan(0, 50_000));
        p.Feed(jumbo.AsSpan(50_000));
        var (cmd, data) = Assert.Single(got);
        Assert.Equal(3, cmd);
        Assert.Equal(inner, data);
    }

    [Fact]
    public void Too_short_frames_are_ignored()
    {
        var (got, p) = Parser();
        p.Feed(new byte[] { 0, 3 });
        p.Feed(new byte[] { 0, 3, 0, 2 }); // size below the header / tamaño menor que la cabecera
        Assert.Empty(got);
    }

    [Theory]
    [InlineData(65, true, false, new byte[] { 0, 1, 0, 6, 0, 65 })]    // A down / A abajo
    [InlineData(65, false, false, new byte[] { 0, 1, 0, 6, 1, 65 })]   // A up / A arriba
    [InlineData(37, true, true, new byte[] { 0, 1, 0, 6, 4, 37 })]     // ← extended down / ← extendida abajo
    [InlineData(37, false, true, new byte[] { 0, 1, 0, 6, 3, 37 })]    // ← extended up / ← extendida arriba
    public void Key_commands(int vk, bool down, bool extended, byte[] expected) =>
        Assert.Equal(expected, DesktopSession.KeyCommand(vk, down, extended));

    [Theory]
    [InlineData(MouseButton.Left, true, new byte[] { 0, 2, 0, 10, 0, 0x02, 0x01, 0x2C, 0x00, 0xC8 })]  // left down at 300,200
    [InlineData(MouseButton.Left, false, new byte[] { 0, 2, 0, 10, 0, 0x04, 0x01, 0x2C, 0x00, 0xC8 })] // left up = ×2
    [InlineData(MouseButton.Right, false, new byte[] { 0, 2, 0, 10, 0, 0x10, 0x01, 0x2C, 0x00, 0xC8 })]
    [InlineData(MouseButton.None, null, new byte[] { 0, 2, 0, 10, 0, 0x00, 0x01, 0x2C, 0x00, 0xC8 })]  // move / mover
    public void Mouse_commands(MouseButton b, bool? down, byte[] expected) =>
        Assert.Equal(expected, DesktopSession.MouseCommand(b, down, 300, 200));

    [Theory]
    [InlineData(120, 0x00, 0x78)]
    [InlineData(-120, 0xFF, 0x87)] // 255 - 0, 255 - 120: what the website sends / lo que manda la web
    public void Wheel_commands(int delta, byte hi, byte lo) =>
        Assert.Equal(new byte[] { 0, 2, 0, 12, 0, 0, 0, 10, 0, 20, hi, lo }, DesktopSession.WheelCommand(10, 20, delta));
}
