namespace MeshCentralClient.Core;

/// <summary>Username and password, plus the second factor when the server asks for it.
/// Usuario y contraseña, y el segundo factor cuando el servidor lo pide.</summary>
public sealed record PasswordCredentials(string Username, string Password, string? TwoFactorCode = null)
{
    internal string Header()
    {
        static string B(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
        var h = B(Username) + "," + B(Password);
        if (!string.IsNullOrEmpty(TwoFactorCode)) h += "," + B(TwoFactorCode.Trim());
        return h;
    }

    // Never print the password. / Nunca se escribe la contraseña.
    public override string ToString() => $"PasswordCredentials {{ Username = {Username} }}";
}
