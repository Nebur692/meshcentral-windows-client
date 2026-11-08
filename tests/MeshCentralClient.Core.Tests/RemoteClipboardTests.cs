using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

/// <summary>The device clipboard's scripts and answers, without a device. / Los guiones y respuestas del portapapeles del equipo, sin equipo.</summary>
public class RemoteClipboardTests
{
    private static string Answer(JsonObject o) => RemoteClipboard.Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString()));

    [Fact]
    public void The_answer_is_found_among_whatever_the_shell_printed()
    {
        var o = new JsonObject { ["kind"] = "text", ["text"] = "ñandú € «hola»\r\nsegunda" };
        var output = "Windows PowerShell\r\nCopyright (C) Microsoft\r\n\r\nPS C:\\> " + Answer(o) + "\r\nPS C:\\> exit\r\n";
        var clip = RemoteClipboard.ToClip(RemoteClipboard.ParseAnswer(output)!, RemoteClipboard.AutomaticLimit);
        Assert.Equal(ClipKind.Text, clip.Kind);
        Assert.Equal("ñandú € «hola»\r\nsegunda", clip.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PS C:\\> exit")]
    [InlineData("MCCCLIP:not-base64!")]
    public void No_answer_or_a_spoiled_one_is_null(string output) => Assert.Null(RemoteClipboard.ParseAnswer(output));

    [Fact]
    public void One_file_many_files_and_no_files_read_alike()
    {
        Assert.Equal(new[] { "C:\\a.txt" }, RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["files"] = "C:\\a.txt" }, 10).Files);
        Assert.Equal(new[] { "C:\\a", "D:\\b" }, RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["files"] = new JsonArray("C:\\a", "D:\\b") }, 10).Files);
        Assert.Empty(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "none" }, 10).Files);
    }

    [Fact]
    public void A_size_over_the_limit_is_marked_whether_it_comes_as_a_whole_or_a_decimal_number()
    {
        Assert.True(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["size"] = 11L }, 10).SizeOverLimit);
        Assert.True(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["size"] = 11.0 }, 10).SizeOverLimit);
        Assert.False(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["size"] = 10L }, 10).SizeOverLimit);
        Assert.False(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files" }, 10).SizeOverLimit);
    }

    [Fact]
    public void Signatures_ignore_line_ends_and_path_spelling_but_not_content()
    {
        Assert.Equal(RemoteClipboard.TextSignature("a\r\nb"), RemoteClipboard.TextSignature("a\nb"));
        Assert.NotEqual(RemoteClipboard.TextSignature("a"), RemoteClipboard.TextSignature("b"));
        Assert.Equal(RemoteClipboard.FilesSignature(new[] { "C:\\Users\\x\\A.txt" }), RemoteClipboard.FilesSignature(new[] { "c:/users/x/a.txt" }));
        Assert.NotEqual(RemoteClipboard.FilesSignature(new[] { "C:\\a" }), RemoteClipboard.FilesSignature(new[] { "C:\\a", "C:\\b" }));
        Assert.Equal("none", RemoteClipboard.ToClip(new JsonObject { ["kind"] = "none" }, 10).Signature);
    }

    [Theory]
    [InlineData("C:/a/b.txt", "C:/a")]
    [InlineData("C:/b.txt", "C:/")]
    [InlineData("D:/x/y/z", "D:/x/y")]
    public void The_folder_of_a_device_path(string path, string parent) => Assert.Equal(parent, RemoteClipboard.Parent(path));

    [Fact]
    public void A_quote_inside_a_name_cannot_end_the_powershell_literal() =>
        Assert.Equal("'it''s; Remove-Item x'", RemoteClipboard.Quote("it's; Remove-Item x"));

    [Fact]
    public void The_one_line_carries_the_whole_script()
    {
        var script = RemoteClipboard.ReadScript(123);
        var line = RemoteClipboard.OneLine(script);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("-STA", line, StringComparison.Ordinal);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(line[(line.LastIndexOf(' ') + 1)..])));
    }

    /// <summary>
    /// Every script the device will run parses without errors, checked by a real PowerShell parser: pwsh in the .NET SDK
    /// image, Windows PowerShell on Windows. / Cada guion que ejecutará el equipo se analiza sin errores con un analizador
    /// de PowerShell de verdad.
    /// </summary>
    [Fact]
    public void Every_script_parses_without_errors()
    {
        var scripts = new Dictionary<string, string>
        {
            ["read"] = RemoteClipboard.ReadScript(RemoteClipboard.AutomaticLimit),
            ["prepare"] = RemoteClipboard.PrepareScript("20261108"),
            ["files"] = RemoteClipboard.SetFilesScript(new[] { "C:\\Users\\o'brien\\a.txt", "C:\\b" }),
            ["image"] = RemoteClipboard.SetImageScript("C:\\Users\\x\\image.png"),
        };
        var shell = new[] { "pwsh", "powershell" }.FirstOrDefault(Exists) ?? throw new Exception("no PowerShell here to parse the scripts with");
        foreach (var (name, script) in scripts)
        {
            var file = Path.Combine(Path.GetTempPath(), $"mcc-clip-{name}-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(file, script, new UTF8Encoding(true));
            try
            {
                var check = "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + file.Replace("'", "''") + "',[ref]$null,[ref]$e); if($e){ $e | ForEach-Object { $_.ToString() }; exit 1 }";
                var (code, output) = Run(shell, "-NoProfile", "-NonInteractive", "-Command", check);
                Assert.True(code == 0, $"{name}: {output}");
            }
            finally { File.Delete(file); }
        }
    }

    private static bool Exists(string exe)
    {
        try { return Run(exe, "-NoProfile", "-Command", "exit 0").Code == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
