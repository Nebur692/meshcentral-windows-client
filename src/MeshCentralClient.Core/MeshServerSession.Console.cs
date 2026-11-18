using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>A piece of what an agent wrote on its console. / Un trozo de lo que escribió un agente en su consola.</summary>
public sealed record ConsoleText(string DeviceId, string Text);

/// <summary>
/// The agent's own console, as the website's "Console" tab (a "msg" of type "console"): commands to the agent itself
/// (help, info, netinfo, ls, wslist…), useful to look into an agent that misbehaves. The answer comes on its own, in as
/// many pieces as the agent likes, as <see cref="ConsoleOutput"/>. The agent requires remote control and console rights.
/// La consola del propio agente, como la pestaña «Consola» de la web: órdenes al agente (help, info, netinfo, ls…), útil
/// para mirar un agente que se porta raro. La respuesta llega sola, en los trozos que quiera, por <see cref="ConsoleOutput"/>.
/// El agente pide los permisos de control remoto y de consola.
/// </summary>
public sealed partial class MeshServerSession
{
    public bool CanUseAgentConsole(Device device)
    {
        var r = RightsOn(device);
        return device.HasAgent && device.Capabilities.HasFlag(AgentCapabilities.Console) &&
               (r == (long)MeshRights.Full || (Has(r, MeshRights.RemoteControl) && Has(r, MeshRights.AgentConsole)));
    }

    public async Task SendConsoleCommandAsync(Device device, string command, CancellationToken ct = default)
    {
        if (!CanUseAgentConsole(device)) throw Denied("ficha/consola/no-autorizado", "use the agent's console");
        var text = command.Trim();
        if (text.Length is 0 or > 4096) throw new MeshClientException(MeshFailure.Rejected, "ficha/consola/dato-invalido", "A console command is one line of up to 4096 characters.");
        if (!IsOpen) throw new MeshClientException(MeshFailure.ConnectionLost, "ficha/consola/conexion-cortada", "The connection to the server was lost.");
        try { await SendAsync(new JsonObject { ["action"] = "msg", ["type"] = "console", ["nodeid"] = device.Id, ["value"] = text }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, "ficha/consola/conexion-cortada", "The connection to the server was lost.", ex);
        }
    }
}
