using MeshCentralClient.App;
using Xunit;

public class FilesPathsTests
{
    [Theory]
    [InlineData("", "C:", "C:/")]                     // a Windows drive / una unidad de Windows
    [InlineData("C:/", "Users", "C:/Users")]
    [InlineData("C:/Users", "Ana", "C:/Users/Ana")]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/etc", "ssh", "/etc/ssh")]
    [InlineData("/etc/", "ssh", "/etc/ssh")]
    public void Join(string folder, string name, string expected) => Assert.Equal(expected, FilesWindow.Join(folder, name));

    [Theory]
    [InlineData("C:/Users/Ana", "C:/Users")]
    [InlineData("C:/Users", "C:/")]
    [InlineData("C:/", "")]                           // back to the drives / de vuelta a las unidades
    [InlineData("/etc/ssh", "/etc")]
    [InlineData("/etc", "/")]
    [InlineData("/", "/")]                            // the root has no parent / la raíz no tiene padre
    public void Parent(string path, string expected) => Assert.Equal(expected, FilesWindow.ParentOf(path));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Human_sizes(long bytes, string expected) =>
        Assert.Equal(expected, FilesWindow.Human(bytes).Replace(',', '.'));
}
