using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Device actions against the real Linux agent. Each one is checked where it lands: the command output, or the line
/// the agent writes in the device's history when it shows a message or performs a power action.
/// Acciones sobre equipos contra el agente Linux real. Cada una se comprueba donde llega: la salida de las órdenes, o la
/// línea que el agente escribe en el historial del equipo al enseñar un aviso o hacer una acción de energía.
/// </summary>
[Trait("Category", "Integration")]
public class ActionsIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> As(string user, string pass)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        return await MeshServerSession.ConnectAsync(a!, new(user, pass), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
    }

    private static async Task<Device> OnlineDevice(MeshServerSession s)
    {
        await s.GetDeviceGroupsAsync(); // rights are worked out from the groups / los permisos salen de los grupos
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return d;
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online");
    }

    /// <summary>Waits for the agent's line in the history (texts of meshcore.js). / Espera la línea del agente en el historial (textos de meshcore.js).</summary>
    private static async Task<DeviceEvent> AgentLogged(MeshServerSession s, Device d, string text)
    {
        for (var i = 0; i < 40; i++)
        {
            var hit = (await s.GetEventsAsync(d.Id, 30)).FirstOrDefault(e => e.Message.Contains(text, StringComparison.Ordinal));
            if (hit is not null) return hit;
            await Task.Delay(250);
        }
        throw new Exception($"the agent never logged \"{text}\"");
    }

    [Fact]
    public async Task The_owner_of_the_group_can_do_everything_on_an_online_device()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        Assert.Equal(0xFFFFFFFF, s.RightsOn(d));
        Assert.Equal(new DeviceAbilities(true, true, true, true, true), s.AbilitiesOn(d));
        // An offline device can only be woken. / Un equipo desconectado solo se puede encender.
        Assert.Equal(new DeviceAbilities(true, false, false, false, false), s.AbilitiesOn(d with { Connectivity = DeviceConnectivity.None }));
    }

    [Fact]
    public async Task Commands_return_their_output_and_errors_sent_to_it()
    {
        // The agent replies when the process exits, and its error stream sometimes arrives after that and is lost
        // (measured: once in, once out). Sent to the output with 2>&1 it always arrives; the window says so.
        // El agente contesta al acabar el proceso, y su salida de errores a veces llega después y se pierde (medido:
        // una vez sí y otra no). Mandada a la salida con 2>&1 llega siempre; la ventana lo dice.
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        // As the user, away from Windows, is the agent's own way: it must answer. / Como usuario, fuera de Windows, es la vía del agente: tiene que contestar.
        Assert.Contains("como-usuario", await s.RunCommandsAsync(d, "echo como-usuario", MeshServerSession.ShellFor(d, powerShell: false), asUser: true));
        var outText = await s.RunCommandsAsync(d, "echo hola $((6*7))\nls /no-existe-mcc 2>&1\necho ñandú", MeshServerSession.ShellFor(d, powerShell: false));
        output.WriteLine(outText);
        Assert.Contains("hola 42", outText);
        Assert.Contains("no-existe-mcc", outText);
        Assert.Contains("ñandú", outText);
    }

    [Fact]
    public async Task A_Windows_shell_on_Linux_is_refused_by_the_server()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var e = await Assert.ThrowsAsync<MeshClientException>(() => s.RunCommandsAsync(d, "echo x", CommandShell.Cmd));
        Assert.Equal("acciones/ejecutar-ordenes/rechazo-remoto", e.BurburitoKey);
        Assert.Equal("Invalid command type", e.ServerMessage);
    }

    [Fact]
    public async Task While_the_agent_runs_commands_it_ignores_others_without_answering()
    {
        // Measured on 1.3.1 (meshcore.js "Run commands can't execute, already busy"): the reason the app sends one
        // batch per device at a time. / La razón de que la app mande una tanda por equipo cada vez.
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var first = s.RunCommandsAsync(d, "sleep 4; echo primera", CommandShell.Shell);
        await Task.Delay(700);
        var e = await Assert.ThrowsAsync<MeshClientException>(() => s.RunCommandsAsync(d, "echo segunda", CommandShell.Shell, timeout: TimeSpan.FromSeconds(2)));
        Assert.Equal("acciones/ejecutar-ordenes/tiempo-agotado", e.BurburitoKey);
        Assert.Contains("primera", await first);
    }

    [Fact]
    public async Task Notification_and_message_box_reach_the_agent()
    {
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var tag = Guid.NewGuid().ToString("N")[..6];
        await s.NotifyAsync([d], "Prueba", "aviso " + tag);
        Assert.StartsWith("Displaying toast message", (await AgentLogged(s, d, "aviso " + tag)).Message);
        await s.MessageBoxAsync(d, "Prueba", "ventana " + tag, TimeSpan.FromMinutes(1));
        Assert.StartsWith("Displaying message box", (await AgentLogged(s, d, "ventana " + tag)).Message);
    }

    [Fact]
    public async Task Sleep_reaches_the_agent_and_wake_is_sent()
    {
        // Only sleep: in the test container a power off or a restart would stop the agent (it is PID 1).
        // Solo suspender: en el contenedor de prueba, apagar o reiniciar pararía al agente (es el PID 1).
        await using var s = await As("admin", "Admin-Pass-1234");
        var d = await OnlineDevice(s);
        var before = (await s.GetEventsAsync(d.Id, 200)).Count(e => e.Message.Contains("power action=4", StringComparison.Ordinal));
        await s.PowerAsync([d], PowerAction.Sleep);
        for (var i = 0; i < 40 && (await s.GetEventsAsync(d.Id, 200)).Count(e => e.Message.Contains("power action=4", StringComparison.Ordinal)) == before; i++) await Task.Delay(250);
        Assert.True((await s.GetEventsAsync(d.Id, 200)).Count(e => e.Message.Contains("power action=4", StringComparison.Ordinal)) > before, "the agent never logged the sleep");
        var woke = await s.WakeAsync([d]);
        Assert.Contains("wake packets", woke);
    }

    [Fact]
    public async Task Lie_without_rights_nothing_is_sent_even_where_the_server_would_say_ok()
    {
        // The device as the owner sees it, asked by an account with no rights on its group: poweraction and toast
        // would answer "ok" (measured), so the app must refuse before sending.
        // El equipo como lo ve el dueño, pedido por una cuenta sin permisos en su grupo: poweraction y toast
        // contestarían «ok» (medido), así que la app tiene que negarse antes de mandarlo.
        Device d;
        await using (var admin = await As("admin", "Admin-Pass-1234")) d = await OnlineDevice(admin);
        await using var user = await As("user", "User-Pass-1234");
        await user.GetDeviceGroupsAsync();
        Assert.Equal(0, user.RightsOn(d));
        Assert.Equal(new DeviceAbilities(false, false, false, false, false), user.AbilitiesOn(d));
        foreach (var (key, act) in new (string, Func<Task>)[]
        {
            ("acciones/accionar-dispositivo/no-autorizado", () => user.PowerAsync([d], PowerAction.Sleep)),
            ("acciones/accionar-dispositivo/no-autorizado", () => user.WakeAsync([d])),
            ("acciones/enviar-mensaje/no-autorizado", () => user.NotifyAsync([d], "t", "m")),
            ("acciones/enviar-mensaje/no-autorizado", () => user.MessageBoxAsync(d, "t", "m", TimeSpan.FromMinutes(1))),
            ("acciones/ejecutar-ordenes/no-autorizado", () => user.RunCommandsAsync(d, "echo x", CommandShell.Shell)),
        })
            Assert.Equal(key, (await Assert.ThrowsAsync<MeshClientException>(act)).BurburitoKey);
    }
}
