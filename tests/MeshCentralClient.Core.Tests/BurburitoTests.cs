using System.Text.RegularExpressions;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

public class BurburitoTests(ITestOutputHelper output)
{
    private static readonly string[] Modules = { "conexion", "equipos", "ajustes", "app", "tunel", "actualizar", "ficheros", "ficha", "acciones", "grupos" };

    internal static string RepoRoot()
    {
        // The build output may live outside the repo (--artifacts-path): first the path recorded at build time.
        // Lo compilado puede estar fuera del repo (--artifacts-path): primero la ruta apuntada al compilar.
        var built = typeof(BurburitoTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == "RepoRoot")?.Value;
        foreach (var start in new[] { built, AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.OfType<string>())
        {
            var d = new DirectoryInfo(start);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "burburito.json"))) d = d.Parent;
            if (d is not null) return d.FullName;
        }
        throw new InvalidOperationException("burburito.json not found / no encuentro burburito.json");
    }

    /// <summary>Every "module/action/cause" string in src/ must be in the catalogue.
    /// Toda cadena «módulo/acción/causa» de src/ tiene que estar en el catálogo.</summary>
    internal static List<string> UnknownKeysIn(string root, out int checkedCount)
    {
        var rx = new Regex("\"(" + string.Join('|', Modules) + @")/[a-z-]+/[a-z-]+""");
        var keys = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => rx.Matches(File.ReadAllText(f)).Select(m => m.Value.Trim('"')))
            .Distinct().ToList();
        checkedCount = keys.Count;
        return keys.Where(k => !Burburito.IsKnown(k)).ToList();
    }

    [Fact]
    public void Every_key_in_the_code_is_catalogued()
    {
        var unknown = UnknownKeysIn(RepoRoot(), out var total);
        output.WriteLine($"TOTAL: {total} keys checked / claves comprobadas");
        Assert.True(total >= 20, $"only {total} keys found: is the scan looking at the right folder?");
        Assert.Empty(unknown);
    }

    /// <summary>MENTIRA: an invented key in a source file must be caught. / Una clave inventada en un fuente tiene que cazarse.</summary>
    [Fact]
    public void Lie_an_invented_key_is_caught()
    {
        var tmp = Directory.CreateTempSubdirectory("burburito-lie-");
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp.FullName, "src"));
            File.WriteAllText(Path.Combine(tmp.FullName, "src", "Lie.cs"), "var k = \"conexion/iniciar-sesion/no-existe-esta-causa\";");
            var unknown = UnknownKeysIn(tmp.FullName, out var total);
            Assert.Equal(1, total);
            Assert.Equal(new[] { "conexion/iniciar-sesion/no-existe-esta-causa" }, unknown);
            Assert.Equal(Burburito.Unknown, Burburito.Code(unknown[0]));
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void Codes_have_the_project_prefix_and_shape()
    {
        Assert.NotEmpty(Burburito.Keys);
        foreach (var k in Burburito.Keys) Assert.Matches(@"^035-\d{3}-\d{3}-\d{3}$", Burburito.Code(k));
    }

    [Fact]
    public void Broken_lock_degrades_without_throwing() => Assert.Empty(Burburito.Parse("{ not json").Codes);

    [Theory]
    [InlineData(true, "Código burburito: ")]
    [InlineData(false, "Burburito code: ")]
    public void Footer_has_a_phrase_and_the_code(bool spanish, string label)
    {
        const string key = "conexion/iniciar-sesion/sin-conexion";
        var footer = Burburito.Footer(key, spanish);
        Assert.EndsWith(label + Burburito.Code(key), footer);
        Assert.Contains("burburito", footer[..footer.IndexOf(label, StringComparison.Ordinal)], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The catalogue has a phrase naming an organisation; a public app for everyone must never show it.
    /// El catálogo tiene una frase que nombra una organización; una app pública para todos no la puede enseñar nunca.</summary>
    [Fact]
    public void No_phrase_names_an_organisation()
    {
        var all = Burburito.AllPhrases().ToList();
        Assert.True(all.Count >= 20, $"only {all.Count} phrases left");
        Assert.DoesNotContain(all, Burburito.NamesAnOrganisation);
        // MENTIRA: the filter does catch it. / El filtro sí la caza.
        Assert.True(Burburito.NamesAnOrganisation("Something got burnt in the kitchens of Burbujalandia."));
    }

    [Fact]
    public void Footer_of_an_unknown_key_still_answers() =>
        Assert.EndsWith("Burburito code: " + Burburito.Unknown, Burburito.Footer("x/y/z", false));
}
