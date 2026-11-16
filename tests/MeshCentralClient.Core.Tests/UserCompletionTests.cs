using MeshCentralClient.Core;
using Xunit;

/// <summary>Completing account names in a comma-separated list. / Completar nombres de cuenta en una lista con comas.</summary>
public class UserCompletionTests
{
    private static UserInfo U(string id, string name, string? real = null, string? email = null) => new(id, name, false) { RealName = real, Email = email };

    private static readonly UserInfo[] Users =
    [
        U("user//~oidc:383761494229738557", "julian@example.com", "Julián Pérez", "julian@example.com"),
        U("user//juana", "juana", null, "juana@example.com"),
        U("user//admin", "admin", "Administrador", null),
        U("user//pablo", "pablo", "Pablo Julio", null),
    ];

    [Fact]
    public void A_single_sign_on_account_is_found_by_name_or_email_and_its_sign_in_name_goes_in()
    {
        var m = UserCompletion.Matches(Users, "jul");
        Assert.Equal("~oidc:383761494229738557", UserCompletion.LoginName(m[0]));   // begins with it / empieza por ello
        Assert.Contains(m, u => u.Name == "pablo");                                  // "Pablo Julio" contains it / lo contiene
        Assert.Equal("~oidc:383761494229738557, ", UserCompletion.Take("jul", m[0]));
        Assert.Equal("Julián Pérez · julian@example.com · ~oidc:383761494229738557", UserCompletion.Label(m[0]));
    }

    [Fact]
    public void Only_the_piece_after_the_last_comma_and_not_those_already_listed()
    {
        Assert.Equal(["juana"], UserCompletion.Matches(Users, "admin, jua").Select(UserCompletion.LoginName));
        Assert.Equal("admin, juana, ", UserCompletion.Take("admin, jua", Users[1]));
        Assert.DoesNotContain(UserCompletion.Matches(Users, "juana, ju"), u => u.Name == "juana");
        Assert.Empty(UserCompletion.Matches(Users, "admin, "));
        Assert.Empty(UserCompletion.Matches(Users, "zzz"));
    }
}
