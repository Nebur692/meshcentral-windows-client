using System.IO;

namespace MeshCentralClient.App;

/// <summary>
/// Where the app keeps its data. Environment.GetFolderPath asks Windows and ignores the APPDATA and LOCALAPPDATA
/// variables, so they are read first: that lets a portable copy (or a test) keep its data elsewhere.
/// Dónde guarda la app sus datos. Environment.GetFolderPath pregunta a Windows y no hace caso a las variables APPDATA y
/// LOCALAPPDATA, así que se leen primero: así una copia portable (o una prueba) puede guardar sus datos en otra parte.
/// </summary>
public static class AppPaths
{
    public static string Roaming => Path.Combine(Folder("APPDATA", Environment.SpecialFolder.ApplicationData), "MeshCentralClient");

    public static string Local => Path.Combine(Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData), "MeshCentralClient");

    private static string Folder(string variable, Environment.SpecialFolder fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && Path.IsPathFullyQualified(v) ? v : Environment.GetFolderPath(fallback);
}
