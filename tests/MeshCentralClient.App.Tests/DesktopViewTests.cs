using System.Windows;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

/// <summary>A point on one display's window → the pixel of the stitched picture. / Un punto en la ventana de una pantalla → el píxel de la imagen cosida.</summary>
public class DesktopViewTests
{
    private static readonly DisplayRect Left = new(2, 0, 56, 1280, 1024);     // the monitor on the left / el monitor de la izquierda
    private static readonly DisplayRect Main = new(1, 1280, 0, 1920, 1080);

    [Theory]
    [InlineData(0, 0, 1280, 0)]
    [InlineData(100, 200, 1380, 200)]
    [InlineData(1919, 1079, 3199, 1079)]
    public void On_the_main_display(double x, double y, int rx, int ry) => Assert.Equal((rx, ry), DesktopWindow.Map(new Point(x, y), Main));

    [Fact]
    public void On_the_display_to_the_left_with_its_vertical_offset() => Assert.Equal((10, 66), DesktopWindow.Map(new Point(10, 10), Left));

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(1920, 10)]   // just outside / justo fuera
    [InlineData(10, 1080)]
    public void Outside_the_display_nothing_is_sent(double x, double y) => Assert.Null(DesktopWindow.Map(new Point(x, y), Main));
}
