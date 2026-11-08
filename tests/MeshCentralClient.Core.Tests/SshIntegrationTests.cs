using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The app's own SSH client, carried by the real Linux agent to the throwaway SSH server of run-integration.sh (which
/// only the agent's network reaches). / El cliente SSH propio de la app, llevado por el agente Linux real al servidor SSH
/// desechable de run-integration.sh (al que solo llega la red del agente).
/// </summary>
[Trait("Category", "Integration")]
public class SshIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static string Host => Env("MESHTEST_SSH_HOST");
    private static string Key(string name) => Path.Combine(Env("MESHTEST_SSH_DIR"), name);

    /// <summary>The fingerprints the SSH server really has, as its own ssh-keygen prints them. / Las huellas que tiene de verdad, como las escribe su ssh-keygen.</summary>
    private static string[] RealFingerprints() =>
        File.ReadAllLines(Key("hostkeys")).Select(l => l.Split(' ')[1]).ToArray();

    private static async Task<(MeshServerSession, Device)> Admin()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return (s, d);
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online / el agente de prueba no llegó a conectarse");
    }

    private static Task<SshTerminal> Open(MeshServerSession s, Device d, SshLogin login, Func<SshHostKey, bool>? trust = null, int port = 22, string? host = null) =>
        SshTerminal.OpenAsync(s, d.Id, d.Name, host ?? Host, port, login, trust ?? (k => RealFingerprints().Contains(k.Fingerprint)), 100, 30);

    /// <summary>Types <paramref name="command"/> and waits for <paramref name="expected"/> on the screen. / Escribe la orden y espera el texto en pantalla.</summary>
    private async Task<string> Run(SshTerminal t, string command, string expected)
    {
        var seen = new StringBuilder();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, ReadOnlyMemory<byte> d) { lock (seen) { seen.Append(Encoding.UTF8.GetString(d.Span)); if (seen.ToString().Contains(expected, StringComparison.Ordinal)) got.TrySetResult(); } }
        t.Output += On;
        try
        {
            await t.WriteAsync(command + "\n");
            if (await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(20))) != got.Task)
                throw new Exception($"\"{expected}\" did not appear; seen: {seen}");
            lock (seen) return seen.ToString();
        }
        finally { t.Output -= On; }
    }

    [Fact]
    public async Task A_password_opens_a_shell_on_the_ssh_server_behind_the_device()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var t = await Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"));
        Assert.Contains(t.HostKey.Fingerprint, RealFingerprints());
        output.WriteLine($"host key {t.HostKey.Algorithm} {t.HostKey.Fingerprint}");
        // Computed there, so it can only come from the remote shell. / Calculado allí, así que solo puede venir del shell remoto.
        await Run(t, "echo \"$(whoami)-$((6*7))\"", "sshuser-42");
    }

    [Fact]
    public async Task A_private_key_and_one_with_its_passphrase_open_it_and_a_wrong_passphrase_says_so()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using (var t = await Open(s, d, new SshLogin("sshuser", PrivateKeyPath: Key("id_plain"))))
            await Run(t, "echo key-$((2+3))", "key-5");
        await using (var t = await Open(s, d, new SshLogin("sshuser", PrivateKeyPath: Key("id_phrase"), Passphrase: "Frase-1234")))
            await Run(t, "echo phrase-$((2+4))", "phrase-6");
        var e = await Assert.ThrowsAsync<MeshClientException>(() => Open(s, d, new SshLogin("sshuser", PrivateKeyPath: Key("id_phrase"), Passphrase: "wrong")));
        Assert.Equal("ssh/iniciar-sesion/dato-invalido", e.BurburitoKey);
    }

    [Fact]
    public async Task A_wrong_password_and_a_missing_one_are_told_apart()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        Assert.Equal("ssh/iniciar-sesion/credencial-invalida",
            (await Assert.ThrowsAsync<MeshClientException>(() => Open(s, d, new SshLogin("sshuser", "wrong-password")))).BurburitoKey);
        Assert.Equal("ssh/iniciar-sesion/falta-dato",
            (await Assert.ThrowsAsync<MeshClientException>(() => Open(s, d, new SshLogin("sshuser")))).BurburitoKey);
    }

    [Fact]
    public async Task An_identity_that_is_not_trusted_stops_before_signing_in()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        SshHostKey? shown = null;
        var e = await Assert.ThrowsAsync<MeshClientException>(() => Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"), k => { shown = k; return false; }));
        Assert.Equal("ssh/abrir-ssh/tls", e.BurburitoKey);
        Assert.Contains(shown!.Fingerprint, RealFingerprints());
    }

    /// <summary>The terminal sends each key on its own, without waiting. / La terminal manda cada tecla por su cuenta, sin esperar.</summary>
    [Fact]
    public async Task Keys_sent_without_waiting_arrive_complete_and_in_order()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var t = await Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"));
        var seen = new StringBuilder();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Output += (_, b) => { lock (seen) { seen.Append(Encoding.UTF8.GetString(b.Span)); if (seen.ToString().Contains("abcdefghijklmnopqrstuvwxyz-42", StringComparison.Ordinal)) got.TrySetResult(); } };
        await Task.WhenAll("echo abcdefghijklmnopqrstuvwxyz-$((6*7))\n".Select(c => t.WriteAsync(c.ToString())).ToArray());
        await got.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task The_window_size_reaches_the_remote_terminal()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var t = await Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"));
        await t.ResizeAsync(123, 37);
        await Run(t, "stty size", "37 123");
    }

    [Fact]
    public async Task Exit_ends_the_terminal_with_its_own_reason()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        await using var t = await Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"));
        var ended = new TaskCompletionSource<MeshClientException>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Ended += (_, e) => ended.TrySetResult(e);
        t.Output += (_, _) => { };
        await t.WriteAsync("exit\n");
        var why = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        output.WriteLine($"{why.BurburitoKey}: {why.Message}");
        Assert.StartsWith("ssh/abrir-ssh/", why.BurburitoKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_that_is_not_ssh_or_has_nobody_says_so()
    {
        var (s, d) = await Admin();
        await using var _ = s;
        foreach (var (host, port) in new[] { ("mesh.test", 443), ("mesh.test", 1) })
        {
            var e = await Assert.ThrowsAsync<MeshClientException>(() => Open(s, d, new SshLogin("sshuser", "Ssh-Pass-1234"), port: port, host: host));
            output.WriteLine($"{host}:{port} → {e.BurburitoKey}: {e.Message} ({e.InnerException?.GetType().Name}: {e.InnerException?.Message})");
            Assert.Equal("ssh/abrir-ssh/sin-conexion", e.BurburitoKey);
        }
    }
}
