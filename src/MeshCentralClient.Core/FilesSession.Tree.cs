using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>Where a transfer of several files is. / Por dónde va una transferencia de varios ficheros.</summary>
public sealed record TreeProgress(string Name, long Done, long Total);

/// <summary>
/// Whole selections, folders included: copy and move on the device itself, download to a local folder and upload a
/// local folder. The agent does not answer copy, move or mkdir, so each step is confirmed with a new listing.
/// Selecciones enteras, carpetas incluidas: copiar y mover en el propio equipo, bajar a una carpeta local y subir una
/// carpeta local. El agente no contesta a copy, move ni mkdir, así que cada paso se confirma volviendo a listar.
/// </summary>
public sealed partial class FilesSession
{
    /// <summary>"folder/name" as the agent writes it; "" is the list of drives. / «carpeta/nombre» como lo escribe el agente; "" es la lista de unidades.</summary>
    public static string JoinPath(string folder, string name) =>
        folder.Length == 0 ? name.TrimEnd('/', '\\') + "/" : folder.TrimEnd('/') + "/" + name;

    /// <summary>Copies files (not folders) inside the device: what the website's paste does. / Copia ficheros (no carpetas) dentro del equipo: lo que hace el pegar de la web.</summary>
    public Task CopyAsync(string fromFolder, string toFolder, IEnumerable<string> names, CancellationToken ct = default) =>
        CommandAsync(new JsonObject { ["action"] = "copy", ["reqid"] = NewId(), ["scpath"] = fromFolder, ["dspath"] = toFolder, ["names"] = Names(names) }, ct);

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

    /// <summary>Lists <paramref name="folder"/> until <paramref name="done"/> holds, or fails with its code. / Lista hasta que se cumpla, o falla con su código.</summary>
    public async Task<RemoteFolder> WaitForAsync(string folder, Func<RemoteFolder, bool> done, string what, CancellationToken ct = default)
    {
        for (var i = 0; i < 25; i++)
        {
            var f = await ListAsync(folder, ct).ConfigureAwait(false);
            if (done(f)) return f;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new MeshClientException(MeshFailure.Rejected, "ficheros/gestionar-ficheros/rechazo-remoto", $"The device did not {what}.");
    }

    /// <summary>True when <paramref name="target"/> is <paramref name="folder"/> or inside it. / Si el destino es la carpeta o está dentro.</summary>
    public static bool IsInside(string target, string folder)
    {
        string N(string p) => p.Replace('\\', '/').TrimEnd('/') + "/";
        return N(target).StartsWith(N(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies the entries of <paramref name="fromFolder"/> into <paramref name="toFolder"/> on the device, folders and all.
    /// Copia las entradas de una carpeta a otra en el equipo, carpetas incluidas.
    /// </summary>
    public async Task CopyTreeAsync(string fromFolder, IReadOnlyList<RemoteEntry> entries, string toFolder, CancellationToken ct = default)
    {
        foreach (var e in entries.Where(e => e.Kind == RemoteEntryKind.Folder))
            if (IsInside(toFolder, JoinPath(fromFolder, e.Name)))
                throw new InvalidOperationException($"\"{e.Name}\" cannot be copied inside itself.");

        var files = entries.Where(e => e.Kind == RemoteEntryKind.File).ToList();
        if (files.Count > 0) await CopyAsync(fromFolder, toFolder, files.Select(f => f.Name), ct).ConfigureAwait(false);
        foreach (var dir in entries.Where(e => e.Kind == RemoteEntryKind.Folder))
        {
            var target = JoinPath(toFolder, dir.Name);
            await CreateFolderAsync(target, ct).ConfigureAwait(false);
            await WaitForAsync(toFolder, f => f.Entries.Any(x => x.Name == dir.Name && x.Kind == RemoteEntryKind.Folder), $"create the folder \"{dir.Name}\"", ct).ConfigureAwait(false);
            var inside = await ListAsync(JoinPath(fromFolder, dir.Name), ct).ConfigureAwait(false);
            await CopyTreeAsync(JoinPath(fromFolder, dir.Name), inside.Entries, target, ct).ConfigureAwait(false);
        }
        // A copy is done when every file is there with its size. / Una copia acaba cuando cada fichero está con su tamaño.
        await WaitForAsync(toFolder, f => files.All(s => f.Entries.Any(x => x.Name == s.Name && x.Size == s.Size)), "copy everything", ct).ConfigureAwait(false);
    }

    /// <summary>Moves entries inside the device: a checked copy, then the originals are deleted. / Mueve dentro del equipo: copia comprobada y después se borran los originales.</summary>
    public async Task MoveTreeAsync(string fromFolder, IReadOnlyList<RemoteEntry> entries, string toFolder, CancellationToken ct = default)
    {
        if (IsInside(fromFolder, toFolder) && IsInside(toFolder, fromFolder)) return; // same folder / la misma carpeta
        await CopyTreeAsync(fromFolder, entries, toFolder, ct).ConfigureAwait(false);
        var names = entries.Select(e => e.Name).ToList();
        await DeleteAsync(fromFolder, names, recursive: true, ct).ConfigureAwait(false);
        await WaitForAsync(fromFolder, f => f.Entries.All(e => !names.Contains(e.Name)), "remove the originals", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads entries (folders included) into <paramref name="localFolder"/>. Each file is written as ".part" and
    /// renamed at the end, so a cut copy never looks whole.
    /// Baja entradas (carpetas incluidas) a una carpeta local. Cada fichero se escribe como «.part» y se renombra al
    /// final, para que una copia cortada nunca parezca entera.
    /// </summary>
    public async Task DownloadTreeAsync(string remoteFolder, IReadOnlyList<RemoteEntry> entries, string localFolder, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var e in entries)
        {
            var local = Path.Combine(localFolder, e.Name);
            var remote = JoinPath(remoteFolder, e.Name);
            if (e.Kind == RemoteEntryKind.File)
            {
                var part = local + ".part";
                try
                {
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                        await DownloadAsync(remote, dst, progress is null ? null : new Progress<long>(n => progress.Report(new(e.Name, n, e.Size))), ct).ConfigureAwait(false);
                    File.Move(part, local, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(part);
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/descargar/permisos-fichero", $"\"{local}\" could not be written.", ex);
                }
                catch { TryDelete(part); throw; }
            }
            else
            {
                try { Directory.CreateDirectory(local); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/descargar/permisos-fichero", $"\"{local}\" could not be created.", ex);
                }
                var inside = await ListAsync(remote, ct).ConfigureAwait(false);
                await DownloadTreeAsync(remote, inside.Entries, local, progress, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Uploads local files and folders into <paramref name="remoteFolder"/>, each under the name given (overwrites).
    /// Sube ficheros y carpetas locales, cada uno con el nombre dado (sobrescribe).
    /// </summary>
    public async Task UploadTreeAsync(IReadOnlyList<(string Local, string Name)> items, string remoteFolder, IProgress<TreeProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var (local, name) in items)
        {
            if (Directory.Exists(local))
            {
                var target = JoinPath(remoteFolder, name);
                await CreateFolderAsync(target, ct).ConfigureAwait(false);
                await WaitForAsync(remoteFolder, f => f.Entries.Any(x => x.Name == name && x.Kind == RemoteEntryKind.Folder), $"create the folder \"{name}\"", ct).ConfigureAwait(false);
                string[] inside;
                try { inside = Directory.GetFileSystemEntries(local); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/subir/permisos-fichero", $"\"{local}\" could not be read.", ex);
                }
                await UploadTreeAsync(inside.Select(p => (p, Path.GetFileName(p))).ToList(), target, progress, ct).ConfigureAwait(false);
                continue;
            }
            FileStream src;
            try { src = File.OpenRead(local); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new MeshClientException(MeshFailure.Rejected, "ficheros/subir/permisos-fichero", $"\"{local}\" could not be read.", ex);
            }
            await using (src)
            {
                var total = src.Length;
                await UploadAsync(remoteFolder, name, src, progress is null ? null : new Progress<long>(n => progress.Report(new(name, n, total))), ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }
}
