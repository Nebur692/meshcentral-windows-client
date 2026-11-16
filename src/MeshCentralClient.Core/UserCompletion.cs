namespace MeshCentralClient.Core;

/// <summary>
/// Completing account names as they are typed in a comma-separated list: the piece after the last comma is looked for in
/// the server's accounts by sign-in name, display name or email, and taking one writes its sign-in name (the last part of
/// its id: an account made by single sign-on is "~oidc:…", which nobody knows by heart). The same as the Android app's.
/// Completar nombres de cuenta en una lista separada por comas: lo que va tras la última coma se busca por usuario, nombre
/// o correo, y al tomar una se escribe su usuario de entrar (la última parte de su id: con inicio único es «~oidc:…»).
/// </summary>
public static class UserCompletion
{
    /// <summary>What the server takes as the user name ("usernames" of addmeshuser and addusertousergroup). / Lo que el servidor toma como nombre.</summary>
    public static string LoginName(UserInfo u) => u.Id.Split('/') is { Length: 3 } p && p[2].Length > 0 ? p[2] : u.Name;

    /// <summary>The text before the piece being typed (up to the last comma) and the piece. / Lo anterior y el trozo que se escribe.</summary>
    public static (string Before, string Typed) Split(string text)
    {
        var i = text.LastIndexOf(',');
        return i < 0 ? ("", text.Trim()) : (text[..(i + 1)], text[(i + 1)..].Trim());
    }

    /// <summary>The accounts that match the piece (beginning first), without those already in the list. / Las cuentas que encajan.</summary>
    public static IReadOnlyList<UserInfo> Matches(IEnumerable<UserInfo> users, string text, int max = 20)
    {
        var (before, typed) = Split(text);
        if (typed.Length == 0) return [];
        var already = before.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Fields(UserInfo u) => new[] { LoginName(u), u.Name, u.RealName, u.Email }.Where(f => !string.IsNullOrEmpty(f))!;
        return users.Where(u => !already.Contains(LoginName(u)))
            .Select(u => (u, rank: Fields(u).Any(f => f.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase)) ? 0
                : Fields(u).Any(f => f.Contains(typed, StringComparison.CurrentCultureIgnoreCase)) ? 1 : 2))
            .Where(x => x.rank < 2).OrderBy(x => x.rank).ThenBy(x => Label(x.u), StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.u).Take(max).ToList();
    }

    /// <summary>The list with the piece replaced by the account's sign-in name, ready for the next. / La lista con el trozo cambiado por su usuario.</summary>
    public static string Take(string text, UserInfo u)
    {
        var (before, _) = Split(text);
        return (before.Length == 0 ? "" : before.TrimEnd() + " ") + LoginName(u) + ", ";
    }

    /// <summary>How an account shows in the list: its name, its email and, if different, its sign-in name. / Cómo sale una cuenta.</summary>
    public static string Label(UserInfo u)
    {
        var name = string.IsNullOrEmpty(u.RealName) ? u.Name : u.RealName;
        var parts = new List<string> { name };
        if (!string.IsNullOrEmpty(u.Email) && !u.Email.Equals(name, StringComparison.OrdinalIgnoreCase)) parts.Add(u.Email);
        var login = LoginName(u);
        if (!login.Equals(name, StringComparison.OrdinalIgnoreCase)) parts.Add(login);
        return string.Join(" · ", parts);
    }
}
