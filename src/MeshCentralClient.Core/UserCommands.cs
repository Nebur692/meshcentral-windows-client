using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>
/// "Run commands as the signed-in user" on Windows. The agent's own way (runcommands with runAsUser) cannot be trusted
/// there, measured: on a connect-mode agent it never answers and the agent drops later batches; on a service agent it
/// answers but runs as SYSTEM, with the user signed in. So the commands go through <see cref="UserShell"/>, in a child
/// cmd or powershell fed by standard input, the same way the agent feeds its own.
/// «Ejecutar órdenes como el usuario con sesión» en Windows. La vía del propio agente (runcommands con runAsUser) no es de
/// fiar allí, medido: con agente connect no contesta nunca y tira las tandas siguientes; con agente de servicio contesta
/// pero corre como SYSTEM, con el usuario dentro. Así que las órdenes van por UserShell, en un cmd o powershell hijo al que
/// se le dan por la entrada estándar, igual que hace el agente con los suyos.
/// </summary>
internal static class UserCommands
{
    private static readonly UserShellCodes Codes = new(
        "acciones/ejecutar-ordenes/conexion-cortada", "acciones/ejecutar-ordenes/respuesta-ilegible", "acciones/ejecutar-ordenes/rechazo-remoto");

    /// <summary>
    /// The script. The commands travel in base64: whatever a person typed (quotes, a line starting with '@) cannot break
    /// the script around them. A child process, so an "exit" in them does not close the terminal. Past the limit the whole
    /// child tree is ended there: nothing keeps running on the device with nobody waiting for it.
    /// El guion. Las órdenes viajan en base64: lo que escriba una persona (comillas, una línea que empiece por '@) no puede
    /// romper el guion que las rodea. Un proceso hijo, para que un «exit» no cierre la terminal. Pasado el límite se acaba
    /// allí todo el árbol del hijo: nada sigue corriendo en el equipo sin nadie esperándolo.
    /// </summary>
    internal static string Script(string commands, CommandShell shell, TimeSpan limit)
    {
        var (file, args) = shell == CommandShell.PowerShell
            ? ("System32\\WindowsPowerShell\\v1.0\\powershell.exe", "-NoProfile -NoLogo -Command -")
            : ("System32\\cmd.exe", "");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(commands));
        var ms = ((long)limit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return $$"""
            $o=[ordered]@{kind='output'}
            try {
              $si=New-Object Diagnostics.ProcessStartInfo
              $si.FileName=Join-Path $env:windir '{{file}}'; $si.Arguments='{{args}}'
              $si.UseShellExecute=$false; $si.CreateNoWindow=$true
              $si.RedirectStandardInput=$true; $si.RedirectStandardOutput=$true; $si.RedirectStandardError=$true
              $child=[Diagnostics.Process]::Start($si)
              $out=$child.StandardOutput.ReadToEndAsync(); $err=$child.StandardError.ReadToEndAsync()
              $child.StandardInput.Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{b64}}'))+"`r`nexit`r`n")
              $child.StandardInput.Close()
              if(-not $child.WaitForExit({{ms}})){ & taskkill /PID $child.Id /T /F | Out-Null; $o.timedOut=$true }
              [void]$out.Wait(2000); [void]$err.Wait(2000)
              $o.output=$(if($out.IsCompleted){$out.Result})+$(if($err.IsCompleted){$err.Result})
            } catch { $o=[ordered]@{kind='error'; error="$_"} }

            """ + RemoteClipboard.Answer;
    }

    /// <summary>What the commands said, from the script's answer. / Lo que dijeron las órdenes, de la respuesta del guion.</summary>
    internal static string OutputOf(JsonObject answer, TimeSpan limit)
    {
        if (answer["timedOut"] is not null)
            throw new MeshClientException(MeshFailure.Timeout, "acciones/ejecutar-ordenes/tiempo-agotado", $"The commands had not ended after {limit.TotalMinutes:0.#} minutes, so they were stopped on the device.");
        return answer.Str("output") ?? "";
    }

    internal static async Task<string> RunAsync(MeshServerSession session, Device device, string commands, CommandShell shell, TimeSpan limit, CancellationToken ct)
    {
        await using var user = new UserShell(session, device, Codes);
        // A little more than the script's own limit, so it is the script that stops them and says so.
        // Algo más que el límite del propio guion, para que sea el guion quien las pare y lo diga.
        var answer = await user.RunAsync(Script(commands, shell, limit), "run the commands", ct, limit + TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return OutputOf(answer, limit);
    }
}
