using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

// The common replies (noauth, tokenrequired) are also checked against a real server in IntegrationTests;
// these cover the ones a test server cannot produce on demand.
// Las respuestas habituales (noauth, tokenrequired) se comprueban también contra un servidor real en
// IntegrationTests; estas cubren las que un servidor de pruebas no da a voluntad.
public class LoginFailureTests
{
    private static readonly PasswordCredentials NoCode = new("u", "p");
    private static readonly PasswordCredentials WithCode = new("u", "p", "123456");

    private static MeshClientException Map(string json, PasswordCredentials c) =>
        MeshServerSession.LoginFailure((JsonObject)JsonNode.Parse(json)!, c);

    [Theory]
    [InlineData("""{"action":"close","cause":"noauth","msg":"noauth-2d"}""", MeshFailure.InvalidCredentials)]
    [InlineData("""{"action":"close","cause":"banned","msg":"banned-1"}""", MeshFailure.TooManyAttempts)]
    [InlineData("""{"action":"close","cause":"locked","msg":"locked-1"}""", MeshFailure.TooManyAttempts)]
    [InlineData("""{"action":"close","cause":"notools","msg":"notools"}""", MeshFailure.ToolsNotAllowed)]
    [InlineData("""{"action":"close","cause":"emailvalidation","msg":"emailvalidationrequired"}""", MeshFailure.EmailValidationRequired)]
    [InlineData("""{"action":"close","cause":"something-new","msg":"x"}""", MeshFailure.Rejected)]
    [InlineData("""{"action":"close"}""", MeshFailure.Rejected)]
    public void Maps_close_reasons(string json, MeshFailure expected)
    {
        var e = Map(json, NoCode);
        Assert.Equal(expected, e.Failure);
        Assert.True(Burburito.IsKnown(e.BurburitoKey), e.BurburitoKey);
    }

    [Fact]
    public void Token_required_without_code_asks_for_it()
    {
        var e = Map("""{"action":"close","cause":"noauth","msg":"tokenrequired","email2fa":true,"sms2fa":false}""", NoCode);
        Assert.Equal(MeshFailure.TwoFactorRequired, e.Failure);
        Assert.True(e.EmailCodeAvailable);
        Assert.False(e.SmsCodeAvailable);
    }

    [Fact]
    public void Token_required_with_code_means_wrong_code() =>
        Assert.Equal(MeshFailure.InvalidTwoFactorCode, Map("""{"action":"close","cause":"noauth","msg":"tokenrequired"}""", WithCode).Failure);

    [Fact]
    public void Asking_for_an_email_code_is_not_a_wrong_code()
    {
        var e = Map("""{"action":"close","cause":"noauth","msg":"tokenrequired","email2fa":true,"email2fasent":true}""", new("u", "p", "**email**"));
        Assert.Equal(MeshFailure.TwoFactorRequired, e.Failure);
        Assert.True(e.EmailCodeSent);
    }

    [Fact]
    public void Credentials_never_print_the_password() =>
        Assert.DoesNotContain("secret-pw", new PasswordCredentials("u", "secret-pw").ToString());
}
