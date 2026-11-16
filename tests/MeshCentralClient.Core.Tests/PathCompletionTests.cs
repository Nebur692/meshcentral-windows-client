using MeshCentralClient.Core;
using Xunit;

/// <summary>Completing a remote path as it is typed. / Completar una ruta remota mientras se escribe.</summary>
public class PathCompletionTests
{
    [Theory]
    [InlineData("C", true, "", "C")]
    [InlineData("C:/", true, "C:/", "")]
    [InlineData("C:\\Us", true, "C:/", "Us")]
    [InlineData("C:/Users/ad", true, "C:/Users", "ad")]
    [InlineData("/", false, "/", "")]
    [InlineData("/et", false, "/", "et")]
    [InlineData("/etc/ss", false, "/etc", "ss")]
    [InlineData("/etc/", false, "/etc", "")]
    [InlineData("ho", false, "/", "ho")]
    [InlineData("//us", false, "/", "us")]
    [InlineData("C:\\\\Users\\\\a", true, "C:/Users", "a")]
    public void The_folder_to_list_and_what_is_typed_of_the_next_name(string typed, bool windows, string folder, string prefix) =>
        Assert.Equal((folder, prefix), PathCompletion.Split(typed, windows));

    [Fact]
    public void Folders_and_drives_that_start_with_it_in_order_and_no_files()
    {
        RemoteEntry E(string n, RemoteEntryKind k) => new(n, k, 0, null, null);
        var entries = new[] { E("Users", RemoteEntryKind.Folder), E("usr.txt", RemoteEntryKind.File), E("updates", RemoteEntryKind.Folder), E("Windows", RemoteEntryKind.Folder) };
        Assert.Equal(["updates", "Users"], PathCompletion.Matches(entries, "u"));
        Assert.Equal(["C:"], PathCompletion.Matches([E("C:", RemoteEntryKind.Drive), E("D:", RemoteEntryKind.Drive)], "c"));
        Assert.Empty(PathCompletion.Matches(entries, "x"));
    }

    [Theory]
    [InlineData("", "C:", "C:/")]
    [InlineData("C:/", "Users", "C:/Users/")]
    [InlineData("/", "etc", "/etc/")]
    [InlineData("/etc", "ssh", "/etc/ssh/")]
    public void Taking_a_name_leaves_the_path_ready_for_the_next_level(string folder, string name, string path) =>
        Assert.Equal(path, PathCompletion.Accept(folder, name));
}
