using System.Text;
using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

namespace MeshCentralClient.Core.Tests;

public class UserCommandsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData("'@\nWrite-Output 'x'\n$a=@'\nb\n'@")] // would close Wrap's here-string / cerraría la cadena de Wrap
    [InlineData("echo \"comillas\" & echo 'simples'")]
    [InlineData("echo ñandú")]
    public void Whatever_was_typed_travels_in_base64_and_cannot_break_the_script(string typed)
    {
        var script = UserCommands.Script(typed, CommandShell.PowerShell, Limit);
        Assert.DoesNotContain(typed, script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(typed)), script, StringComparison.Ordinal);
        var wrapped = RemoteClipboard.Wrap("$rid='x'\n" + script);
        // Only Wrap's own closing mark starts a line with '@. / Solo el cierre del propio Wrap empieza una línea por '@.
        Assert.Single(wrapped.Split('\n'), l => l.StartsWith("'@", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_shell_runs_its_own_program()
    {
        var ps = UserCommands.Script("x", CommandShell.PowerShell, Limit);
        Assert.Contains("powershell.exe", ps, StringComparison.Ordinal);
        Assert.Contains("-Command -", ps, StringComparison.Ordinal);
        var cmd = UserCommands.Script("x", CommandShell.Cmd, Limit);
        Assert.Contains("cmd.exe", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void The_limit_reaches_the_device_and_the_answer_goes_out_on_the_usual_way()
    {
        var script = UserCommands.Script("x", CommandShell.Cmd, TimeSpan.FromSeconds(90));
        Assert.Contains("WaitForExit(90000)", script, StringComparison.Ordinal);
        Assert.EndsWith(RemoteClipboard.Answer, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"kind":"output","output":"hola"}""", "hola")]
    [InlineData("""{"kind":"output","output":""}""", "")]
    [InlineData("""{"kind":"output","output":null}""", "")]
    [InlineData("""{"kind":"output"}""", "")]
    public void The_output_is_what_the_commands_said(string answer, string expected) =>
        Assert.Equal(expected, UserCommands.OutputOf((JsonObject)JsonNode.Parse(answer)!, Limit));

    [Fact]
    public void Commands_stopped_at_the_limit_are_a_timeout_not_an_empty_output()
    {
        var e = Assert.Throws<MeshClientException>(() => UserCommands.OutputOf((JsonObject)JsonNode.Parse("""{"kind":"output","output":"medio","timedOut":true}""")!, Limit));
        Assert.Equal(MeshFailure.Timeout, e.Failure);
        Assert.Equal("acciones/ejecutar-ordenes/tiempo-agotado", e.BurburitoKey);
    }
}
