using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

/// <summary>The device clipboard's scripts and answers, without a device. / Los guiones y respuestas del portapapeles del equipo, sin equipo.</summary>
public class RemoteClipboardTests
{
    /// <summary>What goes on the screen: here, the whole answer, to read it as ParseAnswer does. / Lo que va a la pantalla: aquí, la respuesta entera, para leerla como ParseAnswer.</summary>
    private static string Answer(JsonObject o) => RemoteClipboard.Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())) + RemoteClipboard.EndMarker;

    [Fact]
    public void The_answer_is_found_among_whatever_the_shell_printed()
    {
        var o = new JsonObject { ["kind"] = "text", ["text"] = "ñandú € «hola»\r\nsegunda" };
        var output = "Windows PowerShell\r\nCopyright (C) Microsoft\r\n\r\nPS C:\\> " + Answer(o) + "\r\nPS C:\\> exit\r\n";
        var clip = RemoteClipboard.ToClip(RemoteClipboard.ParseAnswer(output)!);
        Assert.Equal(ClipKind.Text, clip.Kind);
        Assert.Equal("ñandú € «hola»\r\nsegunda", clip.Text);
    }

    /// <summary>A terminal splits the answer at its width and puts escape sequences in it. / Una terminal parte la respuesta a su ancho y le mete secuencias de escape.</summary>
    [Fact]
    public void An_answer_split_by_the_terminal_and_full_of_escapes_still_reads()
    {
        var o = new JsonObject { ["kind"] = "files", ["files"] = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode)$"C:\\Users\\ana\\Documents\\informe número {i}.docx").ToArray()) };
        var answer = Answer(o);
        var split = new StringBuilder("\u001b[?25l\u001b[2J\u001b[mPS C:\\> iex (...)\r\n");
        for (var i = 0; i < answer.Length; i += 80) split.Append(answer.AsSpan(i, Math.Min(80, answer.Length - i))).Append(i % 160 == 0 ? "\r\n" : "\u001b[K\r\n\u001b[?25h");
        split.Append("\u001b]0;Windows PowerShell\u0007PS C:\\> exit\r\n");
        var clip = RemoteClipboard.ToClip(RemoteClipboard.ParseAnswer(split.ToString())!);
        Assert.Equal(30, clip.Files.Count);
        Assert.Equal("C:\\Users\\ana\\Documents\\informe número 29.docx", clip.Files[29]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PS C:\\> exit")]
    [InlineData("MCCCLIP:not-base64!:PILCCCM")]
    [InlineData("MCCCLIP:eyJraW5kIjoidGV4dCJ9")]
    public void No_answer_or_a_spoiled_one_is_null(string output) => Assert.Null(RemoteClipboard.ParseAnswer(output));

    /// <summary>A terminal may paint an earlier answer again: only the one with this request's id counts. / Una terminal puede repintar una respuesta anterior: solo cuenta la que lleva el id de esta petición.</summary>
    [Fact]
    public void An_answer_to_another_request_is_not_taken()
    {
        var old = Answer(new JsonObject { ["kind"] = "text", ["text"] = "vieja", ["id"] = "aaa" });
        var now = Answer(new JsonObject { ["kind"] = "text", ["text"] = "nueva", ["id"] = "bbb" });
        Assert.Null(RemoteClipboard.ParseAnswer(old, "bbb"));
        Assert.Equal("nueva", RemoteClipboard.ParseAnswer(old + "\r\n" + now, "bbb")!.Str("text"));
    }

    [Theory]
    [InlineData(0xFFFFFFFFL, true)]                                       // full: every bit on / completos: todos los bits a uno
    [InlineData((long)MeshRights.RemoteControl, true)]
    [InlineData((long)(MeshRights.RemoteControl | MeshRights.NoTerminal), false)]
    [InlineData((long)(MeshRights.RemoteControl | MeshRights.NoFiles), false)]
    [InlineData((long)MeshRights.RemoteViewOnly, false)]
    [InlineData(0L, false)]
    public void Which_rights_let_images_and_files_through(long rights, bool allowed) => Assert.Equal(allowed, RemoteClipboard.RightsAllowAll(rights));

    /// <summary>Without this, what a service agent uploads cannot be opened by the user (measured on a real one). / Sin esto, lo que sube un agente de servicio no lo puede abrir el usuario (medido en uno real).</summary>
    [Fact]
    public void The_folders_the_agent_uploads_into_are_opened_to_the_user_first()
    {
        Assert.Contains(RemoteClipboard.GrantToUser, RemoteClipboard.PrepareScript("x"), StringComparison.Ordinal);
        Assert.Contains(RemoteClipboard.GrantToUser, RemoteClipboard.AnswerForTests, StringComparison.Ordinal);
        Assert.Contains("GetCurrent().Name+':(OI)(CI)F'", RemoteClipboard.GrantToUser, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-18:(OI)(CI)F", RemoteClipboard.GrantToUser, StringComparison.Ordinal); // SYSTEM too, or the agent cannot read the answers / SYSTEM también, o el agente no lee las respuestas
    }

    [Fact]
    public void One_file_many_files_and_no_files_read_alike()
    {
        Assert.Equal(new[] { "C:\\a.txt" }, RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["files"] = "C:\\a.txt" }).Files);
        Assert.Equal(new[] { "C:\\a", "D:\\b" }, RemoteClipboard.ToClip(new JsonObject { ["kind"] = "files", ["files"] = new JsonArray("C:\\a", "D:\\b") }).Files);
        Assert.Empty(RemoteClipboard.ToClip(new JsonObject { ["kind"] = "none" }).Files);
    }

    [Theory]
    [InlineData("PS C:\\> if(...){'MCC'+'-LIMITED'}else{'MCC'+'-FULL'}", null)] // only the echo / solo el eco
    [InlineData("eco\r\nMCC-FULL\r\nPS C:\\>", true)]
    [InlineData("eco\r\nMCC-LIMITED\r\nPS C:\\>", false)]
    [InlineData("\u001b[1mMCC-\u001b[0mLIMITED", false)] // painted in pieces / pintado a trozos
    [InlineData("", null)]
    public void The_language_of_the_users_powershell_is_told_by_the_answer_not_the_echo(string screen, bool? expected)
    {
        Assert.Equal(expected, UserShell.IsFull(screen));
        Assert.Null(UserShell.IsFull(UserShell.LanguageProbe)); // what is typed never answers / lo tecleado nunca contesta
    }

    private const string Banner = "Microsoft Windows [Version 10]\r\n(c) Microsoft Corporation.\r\n\r\nC:\\Agent>query user\r\n";

    [Theory]
    [InlineData(Banner + " USERNAME  SESSIONNAME  ID  STATE\r\n>ana  console  1  Active\r\n\r\nC:\\Agent>exit\r\n", true)]
    [InlineData(Banner + " NOMBRE DE USUARIO  NOMBRE DE SESIÓN  ID  ESTADO\r\n ana  rdp-tcp#3  2  Activo\r\n\r\nC:\\Agent>exit\r\n", true)]
    // As measured with nobody signed in: more lines than a header and a row, all of them no session.
    // Tal como se midió sin nadie con sesión: más líneas que una cabecera y una fila, y ninguna es una sesión.
    [InlineData(Banner + "No existe un usuario para *\r\n\r\nC:\\Agent>exit\r\n", false)]
    [InlineData("", false)]
    public void Who_is_signed_in_comes_from_the_lines_of_query_user(string answer, bool expected) =>
        Assert.Equal(expected, UserShell.HasSessions(answer));

    [Fact]
    public void Signatures_ignore_line_ends_and_path_spelling_but_not_content()
    {
        Assert.Equal(RemoteClipboard.TextSignature("a\r\nb"), RemoteClipboard.TextSignature("a\nb"));
        Assert.NotEqual(RemoteClipboard.TextSignature("a"), RemoteClipboard.TextSignature("b"));
        Assert.Equal(RemoteClipboard.FilesSignature(new[] { "C:\\Users\\x\\A.txt" }), RemoteClipboard.FilesSignature(new[] { "c:/users/x/a.txt" }));
        Assert.NotEqual(RemoteClipboard.FilesSignature(new[] { "C:\\a" }), RemoteClipboard.FilesSignature(new[] { "C:\\a", "C:\\b" }));
        Assert.Equal("none", RemoteClipboard.ToClip(new JsonObject { ["kind"] = "none" }).Signature);
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
    public void The_one_line_is_short_carries_the_whole_script_in_an_sta_runspace_and_shows_no_mark()
    {
        var script = RemoteClipboard.HelloScript;
        var line = RemoteClipboard.OneLine(script);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length < 1000, $"{line.Length} characters"); // PSReadLine repaints it at each key / PSReadLine la repinta a cada tecla
        // The terminal echoes the line: the marks must not be readable in it. / La terminal repite la línea: las marcas no pueden leerse en ella.
        Assert.DoesNotContain(RemoteClipboard.Marker, line, StringComparison.Ordinal);
        var unpacked = RemoteClipboard.Unpack(line);
        Assert.Equal(RemoteClipboard.Wrap(script), unpacked);
        Assert.Contains("ApartmentState='STA'", unpacked, StringComparison.Ordinal);
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
            ["hello"] = RemoteClipboard.Unpack(RemoteClipboard.OneLine(RemoteClipboard.HelloScript)),
            ["read"] = RemoteClipboard.Unpack(RemoteClipboard.OneLine(RemoteClipboard.ReadScript())),
            ["prepare"] = RemoteClipboard.Unpack(RemoteClipboard.OneLine(RemoteClipboard.PrepareScript("20261108"))),
            ["files"] = RemoteClipboard.Unpack(RemoteClipboard.OneLine(RemoteClipboard.SetFilesScript(new[] { "C:\\Users\\o'brien\\a.txt", "C:\\b" }))),
            ["image"] = RemoteClipboard.Unpack(RemoteClipboard.OneLine(RemoteClipboard.SetImageScript("C:\\Users\\x\\image.png"))),
            // and the bodies alone, which the here-string hides from the outer parse / y los cuerpos solos, que el here-string esconde al análisis de fuera
            ["read-body"] = RemoteClipboard.ReadScript(),
            ["files-body"] = RemoteClipboard.SetFilesScript(new[] { "C:\\Users\\o'brien\\a.txt", "C:\\b" }),
            ["image-body"] = RemoteClipboard.SetImageScript("C:\\Users\\x\\image.png"),
            ["prepare-body"] = RemoteClipboard.PrepareScript("20261108"),
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

    /// <summary>
    /// On Windows (the Windows job runs it), each line goes through Windows PowerShell exactly as the agent sends it:
    /// "-command -" with the line and exit on standard input. Prepare a folder, put it on the clipboard, read it back.
    /// En Windows (lo pasa el trabajo de Windows), cada línea va por Windows PowerShell exactamente como la manda el agente.
    /// </summary>
    [Fact]
    public void On_windows_the_lines_run_in_windows_powershell_as_the_agent_sends_them()
    {
        if (!OperatingSystem.IsWindows()) return; // Linux has no Windows clipboard; the Windows job runs it / Linux no tiene portapapeles de Windows
        JsonObject AsTheAgent(string body)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-noprofile", "-nologo", "-command", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(RemoteClipboard.OneLine(body) + "\r\nexit\r\n");
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(60_000), "Windows PowerShell did not finish");
            var note = RemoteClipboard.ParseAnswer(output.Result);
            Assert.True(note is not null, $"no note in: {output.Result} {errors.Result}");
            var path = note!.Str("path")!;
            var answer = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            File.Delete(path);
            Assert.NotEqual("error", answer.Str("kind"));
            return answer;
        }
        // Text of any size and alphabet comes whole, whatever the terminal does: it never goes through the screen.
        // Un texto de cualquier tamaño y alfabeto llega entero, haga lo que haga la terminal: no pasa por la pantalla.
        var big = string.Concat(Enumerable.Repeat("ñandú €«»\r\n", 2000));
        var bigAnswer = AsTheAgent("$rid='r1'\n$o=[ordered]@{kind='text';text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(big)) + "'))}\n" + RemoteClipboard.AnswerForTests);
        Assert.Equal(big, bigAnswer.Str("text"));
        var folder = AsTheAgent(RemoteClipboard.PrepareScript("t" + Guid.NewGuid().ToString("N")[..8])).Str("folder")!;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "hola");
        var set = RemoteClipboard.ToClip(AsTheAgent(RemoteClipboard.SetFilesScript(new[] { Path.Combine(folder, "a.txt") })));
        var read = RemoteClipboard.ToClip(AsTheAgent(RemoteClipboard.ReadScript()));
        Assert.Equal(ClipKind.Files, read.Kind);
        Assert.Equal(set.Signature, read.Signature);
        Directory.Delete(folder, true);
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
