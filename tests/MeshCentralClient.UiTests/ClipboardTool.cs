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

    /// <summary>A text too long for a command line goes through a file. / Un texto demasiado largo para una línea de órdenes va por un fichero.</summary>
    public static void SetLongText(string text)
    {
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, text); Run("Set-Clipboard -Value ([IO.File]::ReadAllText(" + Quote(file) + "))"); }
        finally { File.Delete(file); }
    }

    /// <summary>Like Ctrl+C on a file or folder in Explorer. / Como Ctrl+C sobre un fichero o carpeta en el Explorador.</summary>
    public static void SetFiles(string path) => Run("Set-Clipboard -LiteralPath " + Quote(path));

    /// <summary>An image of one colour, as a screenshot tool would copy it. / Una imagen de un color, como la copiaría una herramienta de capturas.</summary>
    public static void SetImage(int width, int height, int argb) => Run(
        "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; " +
        $"$b=New-Object Drawing.Bitmap({width},{height}); $g=[Drawing.Graphics]::FromImage($b); $g.Clear([Drawing.Color]::FromArgb({argb})); $g.Dispose(); " +
        "[Windows.Forms.Clipboard]::SetImage($b)");

    /// <summary>"WxH:argb of the centre", or "" without an image. / «AxA:argb del centro», o "" sin imagen.</summary>
    public static string Image() => Run(
        "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $i=[Windows.Forms.Clipboard]::GetImage(); " +
        "if($i){ $b=New-Object Drawing.Bitmap $i; '{0}x{1}:{2}' -f $b.Width,$b.Height,$b.GetPixel([int]($b.Width/2),[int]($b.Height/2)).ToArgb() }");
}
