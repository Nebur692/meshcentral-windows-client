using System.Net.Http;
using System.Security.Cryptography;

namespace MeshCentralClient.Core.Updates;

/// <summary>
/// Downloads a new exe next to the running one, checks its SHA-256 and swaps them. Windows does not let a running exe
/// be overwritten, but it can be renamed: the old one becomes *.old.exe and the new one takes its name; if the second
/// step fails, the first is undone, so there is always a working exe.
/// Descarga un exe nuevo junto al que corre, comprueba su SHA-256 y los cambia. Windows no deja sobrescribir un exe en
/// marcha, pero sí renombrarlo: el viejo pasa a *.old.exe y el nuevo ocupa su nombre; si falla el segundo paso se
/// deshace el primero, así que siempre queda un exe que funciona.
/// </summary>
public static class SelfUpdater
{
    public static string NewPath(string exePath) => exePath + ".new";

    public static string OldPath(string exePath) => Path.ChangeExtension(exePath, null) + ".old.exe";

    /// <summary>Whether the exe's folder can be written (not Program Files without admin). / Si se puede escribir en su carpeta.</summary>
    public static bool CanReplace(string exePath)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exePath)!, $".mcc-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task<string> DownloadAsync(HttpClient http, AvailableUpdate update, string exePath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!CanReplace(exePath))
            throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/permisos-fichero", "The app's folder cannot be written, so it cannot replace itself.");
        return await DownloadCheckedAsync(http, update.ExeUrl, update.Sha256Url, NewPath(exePath), progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The installer of the new version, checked against its fingerprint, into [folder] (emptied first). For a copy
    /// installed for all users: the installer asks for administrator and replaces it.
    /// El instalador de la versión nueva, comprobado con su huella, en [folder] (vaciada antes). Para la copia instalada
    /// para todos los usuarios: el instalador pide administrador y la reemplaza.
    /// </summary>
    public static async Task<string> DownloadSetupAsync(HttpClient http, AvailableUpdate update, string folder,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!update.HasSetup)
            throw new MeshClientException(MeshFailure.UnexpectedResponse, "actualizar/desplegar/respuesta-ilegible", "The release has no installer with its fingerprint.");
        CleanUpSetup(folder);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/permisos-fichero", "The installer could not be written to the temporary folder.", ex);
        }
        return await DownloadCheckedAsync(http, update.SetupUrl!, update.SetupSha256Url!, Path.Combine(folder, UpdateChecker.SetupName), progress, ct).ConfigureAwait(false);
    }

    /// <summary>What a previous installer download left; never in the way. / Lo que dejó una descarga del instalador; nunca estorba.</summary>
    public static void CleanUpSetup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<string> DownloadCheckedAsync(HttpClient http, Uri fileUrl, Uri shaUrl, string target,
        IProgress<double>? progress, CancellationToken ct)
    {
        string expected;
        try
        {
            var text = await http.GetStringAsync(shaUrl, ct).ConfigureAwait(false);
            expected = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";
        }
        catch (HttpRequestException ex) { throw Download(ex); }
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new MeshClientException(MeshFailure.UnexpectedResponse, "actualizar/desplegar/respuesta-ilegible", "The release's fingerprint file is not a SHA-256.");

        try
        {
            using var resp = await http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }
            var got = Convert.ToHexString(sha.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(got), System.Text.Encoding.ASCII.GetBytes(expected)))
                throw new MeshClientException(MeshFailure.UnexpectedResponse, "actualizar/desplegar/dato-corrupto",
                    $"The downloaded file does not match its published fingerprint (expected {expected[..12]}…, got {got[..12]}…). Nothing was changed.");
            return target;
        }
        catch (Exception ex)
        {
            TryDelete(target);
            if (ex is MeshClientException) throw;
            if (ex is HttpRequestException hre) throw Download(hre);
            if (ex is IOException or UnauthorizedAccessException)
                throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/permisos-fichero", "The new version could not be written to disk.", ex);
            throw;
        }
    }

    /// <summary>Puts the downloaded exe in place of the running one; on failure the old one is back. / Pone el exe descargado en lugar del que corre; si falla, vuelve el viejo.</summary>
    public static void Swap(string exePath, string newPath)
    {
        if (!File.Exists(newPath)) throw new FileNotFoundException("downloaded update not found", newPath);
        var old = OldPath(exePath);
        TryDelete(old);
        try { File.Move(exePath, old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/permisos-fichero", "The running app could not be set aside. Nothing was changed.", ex);
        }
        try { File.Move(newPath, exePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Move(old, exePath); } catch (Exception) { /* left as *.old.exe: CleanUp never deletes it while the exe is missing */ }
            throw new MeshClientException(MeshFailure.Rejected, "actualizar/desplegar/permisos-fichero", "The new version could not be put in place; the previous one was restored.", ex);
        }
    }

    /// <summary>
    /// After a restart: removes what an update left (the old exe may still be closing, so it retries).
    /// Tras reabrir: quita lo que dejó una actualización (el exe viejo puede estar cerrándose aún, así que reintenta).
    /// </summary>
    public static async Task CleanUpAsync(string exePath, int attempts = 20, int delayMs = 500)
    {
        if (!File.Exists(exePath)) return; // never remove the only copy / nunca se borra la única copia
        TryDelete(NewPath(exePath));
        var old = OldPath(exePath);
        for (var i = 0; i < attempts && File.Exists(old); i++)
        {
            if (TryDelete(old)) return;
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static MeshClientException Download(HttpRequestException ex) =>
        new(MeshFailure.ServerUnreachable, "actualizar/desplegar/sin-conexion", "The new version could not be downloaded.", ex);
}
