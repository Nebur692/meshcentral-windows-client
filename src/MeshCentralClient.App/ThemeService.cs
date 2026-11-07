using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace MeshCentralClient.App;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Light, dark or follow Windows; remembered in %APPDATA%\MeshCentralClient\settings.json.
/// Claro, oscuro o como Windows; se recuerda en %APPDATA%\MeshCentralClient\settings.json.
/// </summary>
public static class ThemeService
{
    private static readonly string File = Path.Combine(AppPaths.Roaming, "settings.json");

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Raised on the UI thread when the effective theme may have changed. / Se lanza en el hilo de la interfaz cuando puede haber cambiado el tema.</summary>
    public static event EventHandler? Changed;

    /// <summary>Whether what is on screen is dark right now. / Si lo que se ve ahora mismo es oscuro.</summary>
    public static bool IsDark => Current == AppTheme.Dark || (Current == AppTheme.System && WindowsUsesDark());

    public static void LoadAndApply()
    {
        try
        {
            if (System.IO.File.Exists(File)
                && JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) is { } s
                && Enum.TryParse<AppTheme>(s.Theme, true, out var t))
                Current = t;
        }
        catch (Exception) { /* bad settings: follow Windows / ajustes malos: como Windows */ }
        Apply(Current, save: false);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(null, EventArgs.Empty));
        };
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        Current = theme;
#pragma warning disable WPF0001
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        if (save)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new Settings { Theme = theme.ToString() }));
            }
            catch (Exception) { /* not worth an error: it applies anyway / no merece un error: se aplica igual */ }
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    private sealed class Settings { public string? Theme { get; set; } }
}
