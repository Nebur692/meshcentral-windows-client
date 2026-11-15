using System.Net.WebSockets;
using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The chat with the person at the device, updating the agent, a group's consent and features, and the recordings,
/// against the bench (tests/server/run-integration.sh, whose server records every session). The bench's agent has no
/// browser, so the person is played here by what its chat page does: it connects to the relay at the same id.
/// El chat, actualizar el agente, el consentimiento y las funciones de un grupo y las grabaciones, contra el banco (cuyo
/// servidor graba todas las sesiones). El agente del banco no tiene navegador: la persona la hace aquí lo que hace su
/// página de chat, que se conecta al relé con el mismo id.
/// </summary>
[Trait("Category", "Integration")]
public class Phase9IntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server { get { Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a)); return a!; } }

    private static Task<MeshServerSession> Admin() =>
        MeshServerSession.ConnectAsync(Server, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));

    private static async Task<Device> OnlineDevice(MeshServerSession s)
    {
        await s.GetDeviceGroupsAsync();
        for (var i = 0; i < 60; i++)
        {
            var d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline);
            if (d is not null) return d;
            await Task.Delay(500);
        }
        throw new Exception("the test agent never came online");
    }

    /// <summary>What the device's chat page does. / Lo que hace la página de chat del equipo.</summary>
    private static async Task<ClientWebSocket> Person(string nodeId, string userId)
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = new CertificateTrust(Env("MESHTEST_CERT_SHA256")).Validate;
        var url = new Uri(Server.BaseUri.ToString().Replace("https://", "wss://", StringComparison.Ordinal) + "meshrelay.ashx?id=" + Uri.EscapeDataString($"meshmessenger/{nodeId}/{userId}"));
        await ws.ConnectAsync(url, CancellationToken.None);
        return ws;
    }

    private static async Task<string?> Heard(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return r.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(buffer, 0, r.Count);
    }

    private static Task Say(ClientWebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<ChatEvent> Next(MeshChat chat)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await chat.NextAsync(cts.Token) ?? throw new Exception("the chat was closed here");
    }

    [Fact]
    public async Task The_person_who_opens_the_page_late_still_finds_the_technician_and_both_hear_each_other()
    {
        await using var s = await Admin();
        var d = await OnlineDevice(s);
        await using var chat = await MeshChat.OpenAsync(s, d);
        // Longer than the 15 s the relay keeps a lonely side: the technician has to have called again.
        // Más que los 15 s que el relé guarda a un lado solo: el técnico ha tenido que volver a llamar.
        await Task.Delay(TimeSpan.FromSeconds(20));
        Assert.True(chat.IsOpen);
        Assert.False(chat.Joined);
        Assert.False(await chat.SayAsync("antes"), "nothing can be sent before the person is in");
        using var person = await Person(d.Id, s.User.Id);
        Assert.IsType<ChatEvent.Joined>(await Next(chat));
        Assert.Contains(await Heard(person), new[] { "c", "cr" });
        Assert.True(await chat.SayAsync("Hola, soy el técnico: ñ, á, €"));
        await chat.WritingAsync(true);
        Assert.Equal("""{"action":"chat","msg":"Hola, soy el técnico: ñ, á, €"}""", await Heard(person));
        Assert.Equal("""{"action":"outtext","value":true}""", await Heard(person));
        // What the page also sends and this side does not use (WebRTC) is ignored. / Lo que no usa (WebRTC) se ignora.
        await Say(person, """{"action":"random","random":0.99}""");
        await Say(person, """{"action":"outtext","value":true}""");
        await Say(person, """{"action":"chat","msg":"Hola, aquí la persona"}""");
        Assert.Equal(new ChatEvent.Typing(true), await Next(chat));
        Assert.Equal(new ChatEvent.Message("Hola, aquí la persona"), await Next(chat));
        // The person closes the page: the chat ends with its reason. / La persona cierra: el chat acaba con su motivo.
        await person.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        var end = await Assert.ThrowsAsync<MeshClientException>(async () => await Next(chat));
        Assert.True(MeshChat.IsPersonLeft(end), end.BurburitoKey);
        Assert.False(chat.IsOpen);
    }

    [Fact]
    public async Task Nobody_opens_the_page_and_the_chat_says_so_on_time()
    {
        await using var s = await Admin();
        var d = await OnlineDevice(s);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var chat = await MeshChat.OpenAsync(s, d, TimeSpan.FromSeconds(3));
        var end = await Assert.ThrowsAsync<MeshClientException>(async () => await Next(chat));
        Assert.Equal("chat/abrir-detalle/dispositivo-mudo", end.BurburitoKey);
        // On time, not when the relay drops the call after its 15 s. / A su hora, no a los 15 s del relé.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Closing_while_waiting_ends_quietly()
    {
        await using var s = await Admin();
        var chat = await MeshChat.OpenAsync(s, await OnlineDevice(s));
        await chat.DisposeAsync();
        Assert.Null(await chat.NextAsync()); // closed here, no error / cerrado aquí, sin error
        Assert.False(chat.IsOpen);
    }

    [Fact]
    public async Task The_agents_own_answer_to_an_update_comes_back()
    {
        await using var s = await Admin();
        var d = await OnlineDevice(s);
        Assert.True(s.CanUpdateAgent(d));
        var said = await s.UpdateAgentsAsync(new[] { d });
        // Measured on 1.3.1: the bench's agent runs with "connect", not as a service, so it cannot replace itself.
        // Medido en 1.3.1: el agente del banco corre con «connect», no como servicio, así que no puede reemplazarse.
        Assert.Equal("Self Update Failed because this agent is not an instance of (meshagent)", said[d.Id]);
        Assert.True(s.IsOpen);
    }

    [Fact]
    public async Task Consent_and_features_are_saved_as_the_website_saves_them_and_read_back()
    {
        await using var s = await Admin();
        var g = (await s.GetDeviceGroupsAsync()).First(x => x.Id == Env("MESHTEST_GROUP_ID"));
        var before = g.Settings;
        try
        {
            // PREFER_AGENT_NAME without SYNC_NAME falls; RECORD too (the bench records every group already).
            // PREFER_AGENT_NAME sin SYNC_NAME cae; RECORD también (el banco ya graba todos los grupos).
            var saved = await s.SaveGroupSettingsAsync(g, new GroupSettings(
                (int)(GroupConsent.DesktopPrompt | GroupConsent.DesktopToolbar | GroupConsent.FilesNotify),
                (int)(GroupFeature.PreferAgentName | GroupFeature.Record), 30));
            Assert.Equal(new GroupSettings(0x8 | 0x40 | 0x4, 0, 30), saved);
            Assert.Equal(saved, (await s.GetDeviceGroupsAsync()).First(x => x.Id == g.Id).Settings);
            // Removing on disconnect and removing the inactive ones do not go together. / Las dos no van juntas.
            var other = await s.SaveGroupSettingsAsync(g, new GroupSettings(0, (int)(GroupFeature.RemoveOnDisconnect | GroupFeature.SyncName | GroupFeature.AllowOverride), 30));
            Assert.Equal(new GroupSettings(0, 0x1 | 0x2 | 0x10, 0), other);
            Assert.Equal(other, (await s.GetDeviceGroupsAsync()).First(x => x.Id == g.Id).Settings);
            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.SaveGroupSettingsAsync(g, new GroupSettings(0, 0, 2001)));
            Assert.Equal("grupos/gestionar-grupos/dato-invalido", bad.BurburitoKey);
        }
        finally
        {
            await s.SaveGroupSettingsAsync((await s.GetDeviceGroupsAsync()).First(x => x.Id == g.Id), before);
        }
        Assert.Equal(before, (await s.GetDeviceGroupsAsync()).First(x => x.Id == g.Id).Settings);
    }

    [Fact]
    public async Task A_session_leaves_its_recording_in_the_list_and_a_password_session_cannot_download_it()
    {
        await using var s = await Admin();
        Assert.True(s.CanSeeRecordings);
        Assert.False(s.HasWebSession);
        var d = await OnlineDevice(s);
        await using (var t = await TerminalSession.OpenAsync(s, d.Id, TunnelProtocol.Terminal, 80, 24))
        {
            await t.WriteAsync("echo grabado\n");
            await Task.Delay(1500);
        }
        Recording? found = null;
        for (var i = 0; i < 20 && found is null; i++)
        {
            found = (await s.GetRecordingsAsync()).FirstOrDefault(r => r.Kind == RecordedKind.Terminal && r.DeviceId == d.Id && r.Present);
            if (found is null) await Task.Delay(1000);
        }
        Assert.NotNull(found);
        output.WriteLine($"{found!.FileName} {found.Bytes} B");
        Assert.EndsWith(".mcrec", found.FileName, StringComparison.Ordinal);
        Assert.Contains(s.User.Id, found.UserIds);
        Assert.True(found.Bytes > 0);
        var refused = await Assert.ThrowsAsync<MeshClientException>(() => s.DownloadRecordingAsync(found, new MemoryStream()));
        Assert.Equal("grabaciones/descargar/sesion-cerrada", refused.BurburitoKey);
        Assert.EndsWith("?viewmode=52", s.RecordingsPageUri.ToString(), StringComparison.Ordinal);
    }
}
