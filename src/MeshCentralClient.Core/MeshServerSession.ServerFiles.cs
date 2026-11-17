using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshCentralClient.Core;

/// <summary>A file or folder kept on the server. / Un fichero o carpeta guardado en el servidor.</summary>
public sealed record ServerFileEntry(string Name, bool IsFolder, long Size, DateTimeOffset? Modified, IReadOnlyList<ServerFileEntry> Children)
{
    internal static IReadOnlyList<ServerFileEntry> ListOf(JsonNode? f) =>
        f is not JsonObject o ? [] : o.Where(kv => kv.Value is JsonObject).Select(kv =>
        {
            var e = (JsonObject)kv.Value!;
            var folder = e.Long("t") == 2;
            return new ServerFileEntry(kv.Key, folder, folder ? 0 : e.Long("s") ?? 0,
                !folder && e.Long("d") is long d and > 1000 ? DateTimeOffset.FromUnixTimeMilliseconds(d) : null,
                folder ? ListOf(e["f"]) : []);
        }).OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Bytes in this entry and below. / Bytes de esta entrada y lo que tiene dentro.</summary>
    public long TotalBytes => IsFolder ? Children.Sum(c => c.TotalBytes) : Size;
}

/// <summary>
/// One top folder of the server's files: the account's own ("My Files") or a device group's, when the account has the
/// group's server files right. / Una carpeta de arriba: la de la cuenta («Mis ficheros») o la de un grupo de equipos.
/// </summary>
/// <param name="Id">The path's first part: the account or group id. / La primera parte de la ruta.</param>
/// <param name="MaxBytes">The quota, null when there is none. / El cupo; null si no hay.</param>
public sealed record ServerFileRoot(string Id, string Name, bool IsGroup, long? MaxBytes, IReadOnlyList<ServerFileEntry> Entries)
{
    public long UsedBytes => Entries.Sum(e => e.TotalBytes);

    /// <summary>The folder at <paramref name="folder"/>, or null when it is not there. / La carpeta en esa ruta, o null.</summary>
    public IReadOnlyList<ServerFileEntry>? Find(IReadOnlyList<string> folder)
    {
        IReadOnlyList<ServerFileEntry> here = Entries;
        foreach (var part in folder)
        {
            if (here.FirstOrDefault(e => e.IsFolder && e.Name == part) is not { } next) return null;
            here = next.Children;
        }
        return here;
    }
}

/// <summary>
/// The server's own files, as the website's "My Files" (meshuser.js files and fileoperation). The server sends the whole
/// tree when asked and again after each change; changes have no answer of their own, so each waits for that new tree.
/// Downloads go through the same channel up to 200 KB (the server's "get"), as bigger ones need a website session; uploads
/// use "set". It needs the account's server files right (siteadmin 8).
/// Los ficheros del propio servidor, como «Mis ficheros» de la web. El servidor manda el árbol entero al pedirlo y otra vez
/// tras cada cambio; los cambios no tienen respuesta propia, así que cada uno espera ese árbol nuevo. Bajar va por el mismo
/// canal hasta 200 KB (más pide una sesión web); subir usa «set». Pide el permiso de ficheros del servidor (siteadmin 8).
/// </summary>
public sealed partial class MeshServerSession
{
    /// <summary>The server sends a file back only below this size. / El servidor solo devuelve ficheros por debajo de esto.</summary>
    public const long ServerFileDownloadLimit = 204_800;

    /// <summary>The app sends at most this much in one message (the server takes up to 100 MiB, in base64).
    /// / La app manda como mucho esto en un mensaje.</summary>
    public const long ServerFileUploadLimit = 20 * 1024 * 1024;

    public bool CanUseServerFiles => (User.SiteRights & (long)SiteRight.ServerFiles) != 0;

    // As the server's IsFilenameValid. / Como IsFilenameValid del servidor.
    private static readonly Regex BadName = new(@"[\\/:*?""<>|]|^\.|^(nul|prn|con|lpt[0-9]|com[0-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidServerFileName(string name) => name.Length is > 0 and <= 4096 && !BadName.IsMatch(name);

    public async Task<IReadOnlyList<ServerFileRoot>> GetServerFilesAsync(CancellationToken ct = default)
    {
        if (!CanUseServerFiles) throw ServerFilesDenied();
        return Roots(await RequestAsync(new JsonObject { ["action"] = "files" }, IsFileTree,
            "ficheros-servidor/ver-pantalla/tiempo-agotado", "ficheros-servidor/ver-pantalla/conexion-cortada", ct).ConfigureAwait(false));
    }

    public Task<IReadOnlyList<ServerFileRoot>> CreateServerFolderAsync(ServerFileRoot root, IReadOnlyList<string> folder, string name, CancellationToken ct = default) =>
        ChangeAsync(root, folder, [name], new JsonObject { ["fileop"] = "createfolder", ["newfolder"] = name.Trim() },
            here => here.Any(e => e.IsFolder && e.Name == name.Trim()), ct);

    public Task<IReadOnlyList<ServerFileRoot>> RenameServerFileAsync(ServerFileRoot root, IReadOnlyList<string> folder, string oldName, string newName, CancellationToken ct = default) =>
        ChangeAsync(root, folder, [oldName, newName], new JsonObject { ["fileop"] = "rename", ["oldname"] = oldName, ["newname"] = newName.Trim() },
            here => here.Any(e => e.Name == newName.Trim()) && here.All(e => e.Name != oldName), ct);

    /// <summary>Deletes files and folders with all they hold. / Borra ficheros y carpetas con todo lo que tienen.</summary>
    public Task<IReadOnlyList<ServerFileRoot>> DeleteServerFilesAsync(ServerFileRoot root, IReadOnlyList<string> folder, IReadOnlyList<string> names, CancellationToken ct = default) =>
        ChangeAsync(root, folder, names, new JsonObject { ["fileop"] = "delete", ["delfiles"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()), ["rec"] = true },
            here => here.All(e => !names.Contains(e.Name)), ct);

    /// <summary>Puts a file in the folder, replacing one with its name. / Deja un fichero en la carpeta, sustituyendo al del mismo nombre.</summary>
    public async Task<IReadOnlyList<ServerFileRoot>> UploadServerFileAsync(ServerFileRoot root, IReadOnlyList<string> folder, string name, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length > ServerFileUploadLimit)
            throw new MeshClientException(MeshFailure.Rejected, "ficheros-servidor/subir/no-implementado", $"The app sends files of up to {ServerFileUploadLimit / 1048576} MB.");
        var replaced = root.Find(folder)?.FirstOrDefault(e => !e.IsFolder && e.Name == name)?.Size ?? 0;
        if (root.MaxBytes is long max && root.UsedBytes - replaced + data.Length > max)
            throw new MeshClientException(MeshFailure.Rejected, "ficheros-servidor/subir/estado-imposible", "The file does not fit in what is left of the quota.");
        return await ChangeAsync(root, folder, [name], new JsonObject { ["fileop"] = "set", ["file"] = name.Trim(), ["data"] = Convert.ToBase64String(data.Span) },
            here => here.Any(e => !e.IsFolder && e.Name == name.Trim() && e.Size == data.Length), ct,
            "ficheros-servidor/subir", TimeSpan.FromSeconds(Math.Max(30, data.Length / 200_000))).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadServerFileAsync(ServerFileRoot root, IReadOnlyList<string> folder, ServerFileEntry file, CancellationToken ct = default)
    {
        if (!CanUseServerFiles) throw ServerFilesDenied();
        if (file.IsFolder || file.Size >= ServerFileDownloadLimit)
            throw new MeshClientException(MeshFailure.Rejected, "ficheros-servidor/descargar/no-implementado", "The server only sends files below 200 KB to an app; bigger ones are downloaded from its website.");
        var id = NewId();
        var r = await RequestAsync(new JsonObject
            {
                ["action"] = "fileoperation", ["fileop"] = "get", ["path"] = PathOf(root, folder), ["file"] = file.Name, ["responseid"] = id,
            }, o => o.Str("action") == "fileoperation" && o.Str("responseid") == id && o["data"] is JsonValue,
            "ficheros-servidor/descargar/tiempo-agotado", "ficheros-servidor/descargar/conexion-cortada", ct).ConfigureAwait(false);
        try { return Convert.FromBase64String(r.Str("data") ?? ""); }
        catch (FormatException ex) { throw new MeshClientException(MeshFailure.UnexpectedResponse, "ficheros-servidor/descargar/respuesta-ilegible", "The server sent the file unreadable.", ex); }
    }

    private async Task<IReadOnlyList<ServerFileRoot>> ChangeAsync(ServerFileRoot root, IReadOnlyList<string> folder, IReadOnlyList<string> names, JsonObject op,
        Func<IReadOnlyList<ServerFileEntry>, bool> done, CancellationToken ct, string process = "ficheros-servidor/gestionar-ficheros", TimeSpan? timeout = null)
    {
        if (!CanUseServerFiles) throw ServerFilesDenied();
        if (names.Count == 0 || names.Any(n => !IsValidServerFileName(n.Trim())) || folder.Any(f => !IsValidServerFileName(f)))
            throw new MeshClientException(MeshFailure.Rejected, $"{process}/dato-invalido", "A name is empty, starts with a dot or has one of \\ / : * ? \" < > |.");
        op["action"] = "fileoperation";
        op["path"] = PathOf(root, folder);
        // No answer of its own: the server sends the whole tree again, sometimes twice (a new file first empty, then
        // written), so it waits for the tree that shows the change done; one that never does ends in the time limit.
        // Sin respuesta propia: el árbol llega de nuevo, a veces dos veces (un fichero nuevo primero vacío y luego escrito),
        // así que espera al árbol que ya muestra el cambio hecho; si nunca llega, acaba en el tiempo límite.
        var id = root.Id;
        bool Done(JsonObject o) => IsFileTree(o) && Roots(o).FirstOrDefault(r => r.Id == id)?.Find(folder) is { } here && done(here);
        return Roots(await RequestAsync(op, Done, $"{process}/tiempo-agotado", $"{process}/conexion-cortada", ct, timeout).ConfigureAwait(false));
    }

    private static JsonArray PathOf(ServerFileRoot root, IReadOnlyList<string> folder) =>
        new([(JsonNode)root.Id, .. folder.Select(f => (JsonNode)f)]);

    private static bool IsFileTree(JsonObject o) => o.Str("action") == "files" && o["filetree"] is JsonObject;

    private static IReadOnlyList<ServerFileRoot> Roots(JsonObject files) =>
        files["filetree"]?["f"] is not JsonObject tops ? [] : tops.Where(kv => kv.Value is JsonObject).Select(kv =>
        {
            var t = (JsonObject)kv.Value!;
            return new ServerFileRoot(kv.Key, t.Str("n") ?? kv.Key, t.Long("t") == 4, t.Long("maxbytes") is long m and > 0 ? m : null, ServerFileEntry.ListOf(t["f"]));
        }).OrderBy(r => r.IsGroup).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static MeshClientException ServerFilesDenied() =>
        new(MeshFailure.Rejected, "ficheros-servidor/ver-pantalla/no-autorizado", "This account has no rights to the server's files.");
}
