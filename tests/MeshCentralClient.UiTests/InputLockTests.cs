using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// Blocking the keyboard and mouse of the person at the device, against this Windows machine's agent: the agent says
/// it is blocked, then free (MNG_KVM_INPUT_LOCK 87). The Android app sends the same bytes. Locking the session (Win+L) is
/// not tested here: it would lock this machine and every UI test after it.
/// Bloquear el teclado y el ratón de quien está en el equipo, contra el agente de esta máquina: dice bloqueado y luego
/// libre. La app de Android manda los mismos bytes. Bloquear la sesión no se prueba aquí: bloquearía esta máquina.
/// </summary>
public sealed class InputLockTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/ui/run-ui-test.ps1");

    [Fact]
    public async Task The_person_s_keyboard_and_mouse_are_blocked_and_freed()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHUI_SERVER"), out var server));
        await using var s = await MeshServerSession.ConnectAsync(server!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHUI_CERT_SHA256")));
        var dev = (await s.GetDevicesAsync()).Single(d => d.Name == Environment.MachineName);
        await using var desk = await DesktopSession.OpenAsync(s, dev.Id);
        var states = new System.Collections.Concurrent.BlockingCollection<bool>();
        desk.InputLockChanged += (_, locked) => states.Add(locked);
        bool Next(bool wanted)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until) if (states.TryTake(out var v, 500) && v == wanted) return true;
            return false;
        }
        try
        {
            await desk.SetInputLockAsync(true);
            Assert.True(Next(true), "the agent did not say the input is blocked");
        }
        finally
        {
            await desk.SetInputLockAsync(false); // never leave this machine without keyboard / nunca dejar esta máquina sin teclado
        }
        Assert.True(Next(false), "the agent did not say the input is free again");
    }
}
