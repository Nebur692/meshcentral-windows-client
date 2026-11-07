using MeshCentralClient.Core;
using Xunit;

public class SignInMethodsTests
{
    // The shape MeshCentral 1.3.1 writes in its login page. / La forma que escribe MeshCentral 1.3.1 en su página de entrada.
    [Theory]
    [InlineData("var authStrategies = 'oidc'.split(',');", new[] { "oidc" })]
    [InlineData("authStrategies = 'azure,google,saml'.split(',')", new[] { "azure", "google", "saml" })]
    [InlineData("authStrategies = ''.split(',')", new string[0])]
    // The same page from a server with "minify": true (real capture, MeshCentral 1.3.1).
    // La misma página de un servidor con «minify»: true (captura real, MeshCentral 1.3.1).
    [InlineData("twoFactorCookieDays=parseInt(\"30\"),authStrategies=\"oidc\".split(\",\"),tokenTimeout=parseInt(\"300000\")", new[] { "oidc" })]
    [InlineData("authStrategies=\"\".split(\",\")", new string[0])]
    [InlineData("authStrategies='oidc\".split(',')", new string[0])] // mismatched quotes / comillas que no casan
    [InlineData("authStrategies = 'oidc,something-new'.split(',')", new[] { "oidc" })] // unknown ones are ignored / las desconocidas se ignoran
    [InlineData("<a href=\"auth-github\">", new string[0])] // the page names every method; only the list counts / la página las nombra todas; solo cuenta la lista
    [InlineData("", new string[0])]
    public void Reads_only_the_active_list(string html, string[] expected) =>
        Assert.Equal(expected, SignInMethods.ParseLoginPage(html).Select(s => s.Strategy));

    [Fact]
    public void Names_do_not_assume_a_company_for_generic_oidc() =>
        Assert.Equal("OpenID Connect", new SingleSignOn("oidc").DisplayName);
}
