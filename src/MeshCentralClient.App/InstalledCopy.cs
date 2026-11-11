using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace MeshCentralClient.App;

/// <summary>
/// The copy the installer (installer/MeshCentralClient.iss) put in place. When the app updates itself, the version that
/// "Installed apps" and winget read stays the installer's, so the app writes its own there at each start.
/// La copia que dejó el instalador. Cuando la app se actualiza sola, la versión que leen «Aplicaciones instaladas» y
/// winget sigue siendo la del instalador, así que la app escribe allí la suya en cada arranque.
/// </summary>
internal static class InstalledCopy
{
    /// <summary>The installer's AppId plus Inno Setup's suffix. / El AppId del instalador más el sufijo de Inno Setup.</summary>
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C2F50A12-AB55-4901-A024-D09CB431EBE4}_is1";

    /// <summary>This build's version as people read it ("0.13.0", "0.14.0-beta.1"). / La versión de esta compilación tal como se lee.</summary>
    internal static string Version
    {
        get
        {
            var v = typeof(InstalledCopy).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(InstalledCopy).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+', StringComparison.Ordinal); // the commit the SDK appends / el commit que añade el SDK
            return plus < 0 ? v : v[..plus];
        }
    }

    /// <summary>
    /// Where the installer of a new version is downloaded, for a copy installed for all users; emptied at each start.
    /// Donde se baja el instalador de una versión nueva, para la copia instalada para todos; se vacía en cada arranque.
    /// </summary>
    internal static string SetupFolder => Path.Combine(Path.GetTempPath(), "MeshCentralClient-update");

    /// <summary>
    /// Whether this exe is the copy installed for all users (Program Files, entry in HKLM): it cannot replace itself, so
    /// it updates by running the new installer, which asks for administrator.
    /// Si este exe es la copia instalada para todos (Archivos de programa, entrada en HKLM): no puede reemplazarse, así
    /// que se actualiza con el instalador nuevo, que pide administrador.
    /// </summary>
    public static bool IsForAllUsers(string exePath)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location && SameFolder(location, exePath);
        }
        catch (Exception) { return false; } // then it updates as before / entonces se actualiza como antes
    }

    private static bool SameFolder(string location, string exePath) =>
        string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(Path.GetDirectoryName(exePath)!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Only when this exe is the installed one: a copy run from elsewhere leaves the entry alone.
    /// Solo si este exe es el instalado: una copia que se abre desde otro sitio no toca la entrada.</summary>
    public static void SyncVersion(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            if (!SameFolder(location, exePath)) return;
            if (key.GetValue("DisplayVersion") as string != Version) key.SetValue("DisplayVersion", Version);
        }
        catch (Exception) { } // never in the way of starting / nunca estorba al arrancar
    }
}
