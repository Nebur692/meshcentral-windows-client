using MeshCentralClient.Core;
using Xunit;

/// <summary>The second-factor page, full and minified: the key challenge and the encrypted state are read from both.
/// La página del segundo factor, entera y minificada: de las dos se leen el reto y el estado cifrado.</summary>
public class SecurityKeysPageTests
{
    private const string Hkey = "%7B%22type%22%3A%22webAuthn%22%2C%22keyIds%22%3A%5B%22AQID%22%5D%2C%22timeout%22%3A60000%2C%22challenge%22%3A%22AAEC%22%7D";

    [Theory]
    [InlineData("var hardwareKeyChallenge = decodeURIComponent('" + Hkey + "');\n<input type=hidden name=hwstate value=\"st-123_x\" />")]
    [InlineData("hardwareKeyChallenge=decodeURIComponent(\"" + Hkey + "\"),passRequirementsEx=null;<input type=hidden name=hwstate value=st-123_x>")]
    public void Both_forms_of_the_page_give_the_challenge_and_the_state(string html)
    {
        var p = SecurityKeys.ParseSecondFactorPage(html);
        Assert.NotNull(p);
        Assert.Equal("AAEC", p.Value.Challenge["challenge"]!.GetValue<string>());
        Assert.Equal("st-123_x", p.Value.State);
    }

    [Theory]
    [InlineData("hardwareKeyChallenge=decodeURIComponent(\"\"),x=1;<input name=hwstate value=st>")]
    [InlineData("<html>the website, signed in</html>")]
    public void No_key_challenge_is_no_key(string html) => Assert.Null(SecurityKeys.ParseSecondFactorPage(html));
}

/// <summary>A key's index is MeshCentral's keyIndex, up to 4 294 967 295: it must come through whole.
/// El índice de una llave llega hasta 4 294 967 295 y tiene que llegar entero.</summary>
public class SecurityKeyIndexTests
{
    [Fact]
    public void An_index_above_the_int_range_comes_through_whole()
    {
        var k = SecurityKey.From((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"i":4000000000,"name":"YubiKey","type":"3"}""")!);
        Assert.Equal(4_000_000_000L, k.Index);
    }

    [Fact]
    public void The_origin_is_the_one_a_browser_uses()
    {
        Assert.True(ServerAddress.TryParse("https://mesh.example.com/", out var a));
        Assert.Equal(("mesh.example.com", "https://mesh.example.com"), SecurityKeys.Site(a!));
        Assert.True(ServerAddress.TryParse("https://mesh.example.com:8443/", out var b));
        Assert.Equal(("mesh.example.com", "https://mesh.example.com:8443"), SecurityKeys.Site(b!));
        var data = System.Text.Json.Nodes.JsonNode.Parse(SecurityKeys.ClientData("webauthn.get", [0xfb, 0xff], "https://mesh.example.com"))!;
        Assert.Equal("-_8", data["challenge"]!.GetValue<string>());
    }
}
