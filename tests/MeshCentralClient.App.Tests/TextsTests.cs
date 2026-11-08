using System.IO;
using System.Text.RegularExpressions;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Every interface text the code asks for exists: a missing one shows its key to the person instead (L.T gives the key
/// back). The scan reads every source file of the app, so a new window cannot be left out.
/// Todo texto de la interfaz que pide el código existe: uno que falte enseña su clave a la persona. El barrido lee todos
/// los fuentes de la app, así que una ventana nueva no se puede quedar fuera.
/// </summary>
public class TextsTests
{
    private static readonly Regex Asked = new(@"L\.[TF]\(""([A-Za-z0-9]+)""|\{app:T ([A-Za-z0-9]+)\}");

    private static string RepoRoot() =>
        typeof(TextsTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().First(m => m.Key == "RepoRoot").Value!;

    internal static List<string> AskedKeys(string folder) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Asked.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
            .Distinct().ToList();

    [Fact]
    public void Every_text_the_app_asks_for_exists()
    {
        var keys = AskedKeys(Path.Combine(RepoRoot(), "src", "MeshCentralClient.App"));
        Assert.True(keys.Count > 250, $"only {keys.Count} keys found: is the scan looking at the right folder?");
        Assert.DoesNotContain(keys, k => !L.Has(k));
    }

    /// <summary>MENTIRA: an invented key in a source file must be caught. / Una clave inventada en un fuente tiene que cazarse.</summary>
    [Fact]
    public void Lie_an_invented_text_is_caught()
    {
        var tmp = Directory.CreateTempSubdirectory("texts-lie-");
        try
        {
            File.WriteAllText(Path.Combine(tmp.FullName, "Lie.cs"), "var a = L.T(\"Ok\"); var b = L.F(\"NoSuchTextAnywhere\", 1);");
            File.WriteAllText(Path.Combine(tmp.FullName, "Lie.xaml"), "<Button Content=\"{app:T AlsoNotThere}\" />");
            var keys = AskedKeys(tmp.FullName);
            Assert.Equal(3, keys.Count);
            Assert.Equal(new[] { "AlsoNotThere", "NoSuchTextAnywhere" }, keys.Where(k => !L.Has(k)).Order());
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void Every_server_right_has_its_text_and_none_is_missing()
    {
        foreach (var (_, key) in SiteRightsText.Items) Assert.True(L.Has(key), key);
        var expected = Enum.GetValues<SiteRight>().Where(r => r is not (SiteRight.None or SiteRight.FullAdministrator)).ToHashSet();
        Assert.Equal(expected, SiteRightsText.Items.Select(i => i.Right).ToHashSet());
        Assert.Equal(L.T("SiteFull"), SiteRightsText.Summary(0xFFFFFFFF));
        Assert.Equal(L.T("SiteNone"), SiteRightsText.Summary(0));
        Assert.Equal($"{L.T("SiteUsers")}, {L.T("SiteLocked")}", SiteRightsText.Summary((long)(SiteRight.ManageUsers | SiteRight.Locked)));
    }

    [Fact]
    public void The_authenticator_key_is_shown_in_groups_of_four()
    {
        Assert.Equal("ABCD EFGH IJ", AuthenticatorDialog.Grouped("ABCDEFGHIJ"));
    }
}
