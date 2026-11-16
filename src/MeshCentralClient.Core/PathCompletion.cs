namespace MeshCentralClient.Core;

/// <summary>
/// Completing a remote path as it is typed, as Windows' Explorer does: the folder already typed is listed and its
/// subfolders that start with the rest are offered. Paths are the app's ("/" separators; on Windows "C:/Users", and ""
/// for the list of drives). The same as the Android app's PathCompletion.
/// Completar una ruta remota mientras se escribe, como el Explorador: se lista la carpeta ya escrita y se ofrecen sus
/// subcarpetas que empiezan por el resto.
/// </summary>
public static class PathCompletion
{
    /// <summary>The folder to list and what is typed of the next name. / La carpeta a listar y lo escrito del siguiente nombre.</summary>
    public static (string Folder, string Prefix) Split(string typed, bool windows)
    {
        // Repeated separators count as one ("//us" is "/us"). / Las barras repetidas cuentan como una.
        var t = System.Text.RegularExpressions.Regex.Replace(typed.Trim().Replace('\\', '/'), "/{2,}", "/");
        var i = t.LastIndexOf('/');
        if (i < 0) return windows ? ("", t) : ("/", t);
        var folder = t[..(i + 1)];
        // "/" and "C:/" stay as they are; "C:/Users/" is the folder "C:/Users". / «/» y «C:/» se quedan; «C:/Users/» es «C:/Users».
        if (folder.Length > 1 && !(windows && folder.Length == 3 && folder[1] == ':')) folder = folder.TrimEnd('/');
        return (folder, t[(i + 1)..]);
    }

    /// <summary>The folders (and drives) whose name starts with the prefix, in order. / Las carpetas que empiezan por lo escrito.</summary>
    public static IReadOnlyList<string> Matches(IEnumerable<RemoteEntry> entries, string prefix) =>
        entries.Where(e => e.Kind != RemoteEntryKind.File && e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The path once a name is taken, ending in "/" so the next level can be typed. / La ruta al aceptar un nombre, acabada en «/».</summary>
    public static string Accept(string folder, string name)
    {
        var p = FilesSession.JoinPath(folder, name);
        return p.EndsWith('/') ? p : p + "/";
    }
}
