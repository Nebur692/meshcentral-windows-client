using System.Diagnostics;
using System.Text;

/// <summary>
/// The Windows clipboard read and written from outside the app, as another program (Explorer, Notepad) would see it.
/// PowerShell runs in its own process, so nothing is shared with the app except the real clipboard.
/// El portapapeles de Windows leído y escrito desde fuera de la app, como lo vería otro programa (Explorador, Bloc de
/// notas). PowerShell corre en su propio proceso, así que con la app solo se comparte el portapapeles de verdad.
/// </summary>
internal static class ClipboardTool
{
    private static string Run(string script)
    {
        // -EncodedCommand: the script goes as UTF-16 Base64, so no quoting can break it. / El guion va en Base64 UTF-16: ninguna comilla lo rompe.
        var psi = new ProcessStartInfo("powershell", "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.TrimEnd('\r', '\n');
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static string Text() => Run("[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw");

    public static string[] Files() => Run("[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-Clipboard -Format FileDropList) | ForEach-Object { $_.FullName }")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void SetText(string text) => Run("Set-Clipboard -Value " + Quote(text));

    /// <summary>Like Ctrl+C on a file or folder in Explorer. / Como Ctrl+C sobre un fichero o carpeta en el Explorador.</summary>
    public static void SetFiles(string path) => Run("Set-Clipboard -LiteralPath " + Quote(path));
}
