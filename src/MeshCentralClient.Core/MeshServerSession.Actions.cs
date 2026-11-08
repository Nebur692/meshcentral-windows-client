using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>Device rights of MeshCentral (meshuser.js MESHRIGHT_*). / Permisos sobre equipos de MeshCentral.</summary>
[Flags]
public enum MeshRights : long
{
    None = 0,
    EditGroup = 0x1, ManageUsers = 0x2, ManageDevices = 0x4, RemoteControl = 0x8, AgentConsole = 0x10, ServerFiles = 0x20,
    Wake = 0x40, SetNotes = 0x80, RemoteViewOnly = 0x100, NoTerminal = 0x200, NoFiles = 0x400, NoAmt = 0x800,
    LimitedInput = 0x1000, LimitEvents = 0x2000, ChatNotify = 0x4000, Uninstall = 0x8000, NoDesktop = 0x10000,
    RemoteCommand = 0x20000, ResetOff = 0x40000, GuestSharing = 0x80000, DeviceDetails = 0x100000, Relay = 0x200000,
    NoRegistry = 0x400000, NoSoftware = 0x800000,
    Full = 0xFFFFFFFF,
}

/// <summary>Power actions the agent performs (poweraction actiontype). / Acciones de energía que hace el agente.</summary>
public enum PowerAction { PowerOff = 2, Restart = 3, Sleep = 4 }

/// <summary>Which shell runs the commands (runcommands type). / Qué intérprete ejecuta las órdenes.</summary>
public enum CommandShell { Cmd = 1, PowerShell = 2, Shell = 3 }

/// <summary>The device actions a person can do, worked out from the rights. / Las acciones que puede hacer, sacadas de los permisos.</summary>
public sealed record DeviceAbilities(bool Wake, bool Power, bool Notify, bool MessageBox, bool RunCommands);

/// <summary>
/// Device actions: wake, power, messages to whoever uses the device, and commands with their output. Rights are worked
/// out first, exactly as the website does (GetNodeRights), because poweraction and toast answer "ok" without them.
/// Acciones sobre equipos: encender, energía, avisos a quien usa el equipo y órdenes con su salida. Los permisos se
/// calculan antes, exactamente como la web (GetNodeRights), porque poweraction y toast contestan «ok» sin tenerlos.
/// </summary>
public sealed partial class MeshServerSession
{
    private Dictionary<string, DeviceGroup> _groups = new();

    /// <summary>The longest wait for the output of commands. / La espera más larga para la salida de unas órdenes.</summary>
    public static readonly TimeSpan CommandsTimeout = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------------ rights / permisos

    /// <summary>default.handlebars GetMeshRights. / GetMeshRights de la web.</summary>
    public static long GroupRights(UserInfo user, ServerInfo server, DeviceGroup? group)
    {
        if (group is null) return 0;
        if (server.ManageAllDeviceGroups) return RemoveUserRights(user, 0xFFFFFFFF);
        long rights = 0;
        if (group.Links.TryGetValue(user.Id, out var r))
        {
            if (r == 0xFFFFFFFF) return RemoveUserRights(user, 0xFFFFFFFF);
            rights = r;
        }
        foreach (var link in user.Links.Keys.Where(k => k.StartsWith("ugrp/", StringComparison.Ordinal)))
            if (group.Links.TryGetValue(link, out var g))
            {
                if (g == 0xFFFFFFFF) return RemoveUserRights(user, 0xFFFFFFFF);
                rights |= g;
            }
        return RemoveUserRights(user, rights);
    }

    /// <summary>default.handlebars GetNodeRights. / GetNodeRights de la web.</summary>
    public static long DeviceRights(UserInfo user, ServerInfo server, DeviceGroup? group, Device device)
    {
        var r = GroupRights(user, server, group);
        if (r == 0xFFFFFFFF) return r; // already limited / ya limitado
        if (device.Links.TryGetValue(user.Id, out var own)) r |= own;
        foreach (var (link, rights) in device.Links)
            if (link.StartsWith("ugrp/", StringComparison.Ordinal) && user.Links.ContainsKey(link)) r |= rights;
        return RemoveUserRights(user, r);
    }

    /// <summary>default.handlebars removeUserRights. / removeUserRights de la web.</summary>
    internal static long RemoveUserRights(UserInfo user, long rights)
    {
        if (user.RemoveRights is not long remove) return rights;
        long add = 0, subtract = 0;
        foreach (var (bit, adds) in new (long, bool)[]
        {
            (0x8, false), (0x10000, true), (0x100, true), (0x200, true), (0x400, true), (0x400000, true), (0x800000, true),
            (0x10, false), (0x8000, false), (0x20000, false), (0x40, false), (0x40000, false),
        })
            if ((remove & bit) != 0) { if (adds) add |= bit; else subtract |= bit; }
        if (rights == 0xFFFFFFFF) rights = 1 + 2 + 4 + 8 + 32 + 64 + 128 + 16384 + 32768 + 131072 + 262144 + 524288 + 1048576;
        rights |= add;
        rights &= 0xFFFFFFFF - subtract;
        return rights;
    }

    private static bool Has(long rights, MeshRights r) => (rights & (long)r) == (long)r;

    /// <summary>The rights of this account on a device (the groups must have been listed). / Los permisos de la cuenta en un equipo.</summary>
    public long RightsOn(Device device) => DeviceRights(User, ServerInfo, _groups.GetValueOrDefault(device.GroupId), device);

    /// <summary>What can be done on a device now: rights and, where the agent does it, the agent connected.
    /// Lo que se puede hacer ahora en un equipo: permisos y, si lo hace el agente, el agente conectado.</summary>
    public DeviceAbilities AbilitiesOn(Device device)
    {
        var r = RightsOn(device);
        return new(
            Wake: Has(r, MeshRights.Wake),
            Power: Has(r, MeshRights.ResetOff) && device.HasAgent,
            Notify: Has(r, MeshRights.ChatNotify) && device.HasAgent,
            // msg is routed only with remote control or view only (routeCommandToNode). / msg solo se enruta con control remoto o solo ver.
            MessageBox: (Has(r, MeshRights.RemoteControl) || Has(r, MeshRights.RemoteViewOnly)) && device.HasAgent,
            RunCommands: Has(r, MeshRights.RemoteCommand) && device.HasAgent);
    }

    /// <summary>Port mapping, SSH and RDP through the device: the server asks for remote control or relay rights
    /// (meshrelay.js, 0x00200008) and the agent connected. / Redirección, SSH y RDP por el equipo: el servidor pide
    /// control remoto o relé, y el agente conectado.</summary>
    public bool CanRelayThrough(Device device) =>
        (Has(RightsOn(device), MeshRights.RemoteControl) || Has(RightsOn(device), MeshRights.Relay)) && device.HasAgent;

    /// <summary>
    /// MeshCentral's own clipboard: text only (meshcore.js setclip). The agent answers {action:'msg', type:'setclip',
    /// success:true}; a server with clipboardset off, or an agent still busy with the previous one, answers nothing.
    /// El portapapeles de MeshCentral: solo texto. El agente contesta success; un servidor con clipboardset desactivado, o
    /// un agente aún ocupado con el anterior, no contesta.
    /// </summary>
    public async Task SetDeviceClipboardTextAsync(Device device, string text, CancellationToken ct = default)
    {
        if (!Has(RightsOn(device), MeshRights.RemoteControl)) throw Denied("portapapeles/sincronizar/no-autorizado", "use the clipboard");
        if (!device.HasAgent) throw Offline("portapapeles/sincronizar/sin-conexion");
        await RequestAsync(new JsonObject { ["action"] = "msg", ["type"] = "setclip", ["nodeid"] = device.Id, ["data"] = text },
            o => o.Str("action") == "msg" && o.Str("type") == "setclip",
            "portapapeles/sincronizar/tiempo-agotado", "portapapeles/sincronizar/conexion-cortada", ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    private static MeshClientException Denied(string key, string what) =>
        new(MeshFailure.Rejected, key, $"This account has no rights to {what} on this device.");

    private static MeshClientException Offline(string key) =>
        new(MeshFailure.Rejected, key, "The device's agent is not connected.");

    private static JsonArray Ids(IEnumerable<Device> devices) => new(devices.Select(d => (JsonNode)d.Id).ToArray());

    // ------------------------------------------------------------------ power / energía

    /// <summary>Wakes devices (wake-on-LAN sent by other agents of their network). Returns what the server says.
    /// Despierta equipos (el paquete lo mandan otros agentes de su red). Devuelve lo que dice el servidor.</summary>
    public async Task<string> WakeAsync(IReadOnlyList<Device> devices, CancellationToken ct = default)
    {
        foreach (var d in devices) if (!AbilitiesOn(d).Wake) throw Denied("acciones/accionar-dispositivo/no-autorizado", "wake it");
        var id = NewId();
        var reply = await RequestAsync(new JsonObject { ["action"] = "wakedevices", ["nodeids"] = Ids(devices), ["responseid"] = id },
            o => o.Str("action") == "wakedevices" && o.Str("responseid") == id,
            "acciones/accionar-dispositivo/tiempo-agotado", "acciones/accionar-dispositivo/conexion-cortada", ct).ConfigureAwait(false);
        var result = reply.Str("result") ?? "";
        // Measured on 1.3.1: "Used N device(s) to send wake packets", or "Invalid nodeid" / "Invalid domain".
        if (result.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase))
            throw new MeshClientException(MeshFailure.Rejected, "acciones/accionar-dispositivo/rechazo-remoto", $"The server said: {result}.") { ServerMessage = result };
        return result;
    }

    /// <summary>Power off, restart or sleep, done by the agent. / Apagar, reiniciar o suspender, lo hace el agente.</summary>
    public async Task PowerAsync(IReadOnlyList<Device> devices, PowerAction action, CancellationToken ct = default)
    {
        foreach (var d in devices)
        {
            if (!Has(RightsOn(d), MeshRights.ResetOff)) throw Denied("acciones/accionar-dispositivo/no-autorizado", "power it off or restart it");
            if (!d.HasAgent) throw Offline("acciones/accionar-dispositivo/sin-conexion");
        }
        var id = NewId();
        await RequestAsync(new JsonObject { ["action"] = "poweraction", ["nodeids"] = Ids(devices), ["actiontype"] = (int)action, ["responseid"] = id },
            o => o.Str("action") == "poweraction" && o.Str("responseid") == id,
            "acciones/accionar-dispositivo/tiempo-agotado", "acciones/accionar-dispositivo/conexion-cortada", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ messages / avisos

    /// <summary>A Windows notification on the device (toast). / Una notificación de Windows en el equipo.</summary>
    public async Task NotifyAsync(IReadOnlyList<Device> devices, string title, string message, CancellationToken ct = default)
    {
        foreach (var d in devices)
        {
            if (!Has(RightsOn(d), MeshRights.ChatNotify)) throw Denied("acciones/enviar-mensaje/no-autorizado", "send notifications");
            if (!d.HasAgent) throw Offline("acciones/enviar-mensaje/sin-conexion");
        }
        var id = NewId();
        var reply = await RequestAsync(new JsonObject { ["action"] = "toast", ["nodeids"] = Ids(devices), ["title"] = title, ["msg"] = message, ["responseid"] = id },
            o => o.Str("action") == "toast" && o.Str("responseid") == id,
            "acciones/enviar-mensaje/tiempo-agotado", "acciones/enviar-mensaje/conexion-cortada", ct).ConfigureAwait(false);
        if (!string.Equals(reply.Str("result"), "ok", StringComparison.OrdinalIgnoreCase))
            throw new MeshClientException(MeshFailure.Rejected, "acciones/enviar-mensaje/rechazo-remoto", $"The server said: {reply.Str("result")}.") { ServerMessage = reply.Str("result") };
    }

    /// <summary>A message window on the device that stays until closed or <paramref name="timeout"/>. / Una ventana de mensaje en el equipo.</summary>
    public async Task MessageBoxAsync(Device device, string title, string message, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!AbilitiesOn(device).MessageBox)
            throw device.HasAgent ? Denied("acciones/enviar-mensaje/no-autorizado", "show messages") : Offline("acciones/enviar-mensaje/sin-conexion");
        var id = NewId();
        var reply = await RequestAsync(new JsonObject
        {
            ["action"] = "msg", ["type"] = "messagebox", ["nodeid"] = device.Id, ["title"] = title, ["msg"] = message,
            ["timeout"] = (long)timeout.TotalMilliseconds, ["responseid"] = id,
        }, o => o.Str("action") == "msg" && o.Str("responseid") == id,
            "acciones/enviar-mensaje/tiempo-agotado", "acciones/enviar-mensaje/conexion-cortada", ct).ConfigureAwait(false);
        if (reply.Str("result") != "OK")
            throw new MeshClientException(MeshFailure.Rejected, "acciones/enviar-mensaje/rechazo-remoto", $"The server said: {reply.Str("result")}.") { ServerMessage = reply.Str("result") };
    }

    // ------------------------------------------------------------------ commands / órdenes

    /// <summary>The shell a device uses: cmd or PowerShell on Windows, sh elsewhere. / El intérprete de cada equipo.</summary>
    public static CommandShell ShellFor(Device device, bool powerShell) =>
        device.IsWindows ? (powerShell ? CommandShell.PowerShell : CommandShell.Cmd) : CommandShell.Shell;

    /// <summary>
    /// Runs commands on one device and returns their output (stdout and stderr together, as the agent sends them).
    /// The agent runs one batch at a time and silently ignores another one meanwhile, so callers send one per device.
    /// Ejecuta órdenes en un equipo y devuelve su salida (stdout y stderr juntas, como las manda el agente). El agente
    /// ejecuta una tanda cada vez y no contesta a otra mientras, así que se manda una por equipo.
    /// </summary>
    /// <param name="asUser">As the signed-in user, or as the agent if nobody is (runAsUser 1). / Como el usuario con sesión o, si no hay, como el agente.</param>
    public async Task<string> RunCommandsAsync(Device device, string commands, CommandShell shell, bool asUser = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Has(RightsOn(device), MeshRights.RemoteCommand)) throw Denied("acciones/ejecutar-ordenes/no-autorizado", "run commands");
        if (!device.HasAgent) throw Offline("acciones/ejecutar-ordenes/sin-conexion");
        var id = NewId();
        var reply = await RequestAsync(new JsonObject
        {
            ["action"] = "runcommands", ["nodeids"] = new JsonArray(device.Id), ["type"] = (int)shell, ["cmds"] = commands,
            ["runAsUser"] = asUser ? 1 : 0, ["reply"] = true, ["responseid"] = id,
        },
            // The output comes as a "msg" from the agent; refusals come from the server as "runcommands".
            // La salida llega como «msg» del agente; los rechazos, del servidor como «runcommands».
            o => o.Str("responseid") == id && ((o.Str("action") == "msg" && o.Str("type") == "runcommands") || o.Str("action") == "runcommands"),
            "acciones/ejecutar-ordenes/tiempo-agotado", "acciones/ejecutar-ordenes/conexion-cortada", ct, timeout ?? CommandsTimeout).ConfigureAwait(false);
        if (reply.Str("action") == "msg") return reply.Str("result") ?? "";
        var result = reply.Str("result") ?? "";
        throw result switch
        {
            "Access denied" => new MeshClientException(MeshFailure.Rejected, "acciones/ejecutar-ordenes/no-autorizado", "The server refused: no rights to run commands.") { ServerMessage = result },
            "Agent not connected" => new MeshClientException(MeshFailure.Rejected, "acciones/ejecutar-ordenes/sin-conexion", "The device's agent is not connected.") { ServerMessage = result },
            _ => new MeshClientException(MeshFailure.Rejected, "acciones/ejecutar-ordenes/rechazo-remoto", $"The server said: {result}.") { ServerMessage = result },
        };
    }
}
