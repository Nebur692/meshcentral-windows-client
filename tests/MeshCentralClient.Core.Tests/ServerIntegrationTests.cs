using System.Collections.Concurrent;
using System.Text;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The server as a whole against the real one: its event log (also live), the website's reports built from what the
/// test itself does (a terminal session, sign-ins), the server's live state and error log, and notifications that reach
/// another connected session. Every refusal the server would drop without answering is stopped before it goes out.
/// El servidor entero contra el real: su registro de sucesos (también en vivo), los informes de la web hechos con lo que
/// hace la propia prueba (una sesión de terminal, entradas), el estado en vivo y el registro de errores, y avisos que
/// llegan a otra sesión conectada. Cada rechazo que el servidor tiraría sin contestar se para antes de salir.
/// </summary>
[Trait("Category", "Integration")]
public class ServerIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static async Task<MeshServerSession> As(string user, string pass)
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        var s = await MeshServerSession.ConnectAsync(a!, new(user, pass), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        return s;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task The_event_log_lists_what_happened_and_new_events_arrive_live()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var live = new ConcurrentQueue<ServerEvent>();
        admin.EventReceived += (_, e) => live.Enqueue(e);
        var name = "Sucesos " + Tag();
        var id = await admin.CreateGroupAsync(name);
        try
        {
            // The creation arrives live with its message, and the log has it afterwards. / Llega en vivo y luego está en el registro.
            var seen = await GroupsIntegrationTests.Until(() => live.FirstOrDefault(e => e.MeshId == id && e.Message.Contains(name, StringComparison.Ordinal)), "the live event of the new group");
            output.WriteLine($"{seen.Time} {seen.Action} {seen.UserName}: {seen.Message}");
            Assert.Equal(admin.User.Id, seen.UserId);
            var log = await admin.GetServerEventsAsync(200);
            Assert.Contains(log, e => e.MeshId == id && e.Message.Contains(name, StringComparison.Ordinal));
            Assert.True(log.Zip(log.Skip(1)).All(p => p.First.Time >= p.Second.Time), "newest first");

            // Another account's events, for whoever manages users only (the server would silently send one's own).
            // Los sucesos de otra cuenta, solo para quien gestiona usuarios (el servidor mandaría sin decir nada los propios).
            await using var user = await As("user", "User-Pass-1234");
            var mine = await admin.GetUserEventsAsync(admin.User, 200);
            Assert.Contains(mine, e => e.MeshId == id);
            var denied = await Assert.ThrowsAsync<MeshClientException>(() => user.GetUserEventsAsync(admin.User));
            Assert.Equal("servidor/abrir-detalle/no-autorizado", denied.BurburitoKey);
        }
        finally { await GroupsIntegrationTests.DeleteIfThere(admin, id); }
    }

    [Fact]
    public async Task The_reports_count_the_test_s_own_terminal_session_and_sign_ins()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var from = DateTimeOffset.UtcNow.AddHours(-2);
        Device? dev = null;
        for (var i = 0; i < 60 && dev is null; i++) { dev = (await admin.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline); if (dev is null) await Task.Delay(500); }
        Assert.NotNull(dev);

        // A real remote session, closed: the server logs its end with its length. / Una sesión remota de verdad, cerrada.
        await using (var term = await TerminalSession.OpenAsync(admin, dev!.Id, TunnelProtocol.Terminal, 80, 25))
        {
            var marker = "rp" + Tag();
            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var text = new StringBuilder();
            void Add(string t) { lock (text) { text.Append(t); if (text.ToString().Contains(marker + "-ok")) got.TrySetResult(); } }
            term.Tunnel.DataReceived += (_, d) => Add(Encoding.UTF8.GetString(d.Span));
            term.Tunnel.TextReceived += (_, t) => Add(t);
            await term.WriteAsync($"echo {marker}-o\"k\"\n");
            await got.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Report? sessions = null;
        for (var i = 0; i < 40; i++)
        {
            sessions = await admin.GetReportAsync(ReportKind.RemoteSessions, ReportGroupBy.Device, from, DateTimeOffset.UtcNow.AddMinutes(1));
            if (sessions.Groups.Any(g => g.Key.Contains(dev.Id, StringComparison.Ordinal) && g.Rows.Count > 0)) break;
            await Task.Delay(500);
        }
        Assert.Equal("nodemesh", sessions!.GroupFormat);
        var row = sessions.Groups.First(g => g.Key.Contains(dev.Id, StringComparison.Ordinal)).Rows[0];
        output.WriteLine(string.Join(", ", row.Select(p => $"{p.Key}={p.Value}")));
        Assert.Equal(admin.User.Id, row["userid"]?.ToString());
        Assert.Contains(sessions.Columns, c => c is { Id: "time", Format: "datetime" });

        // The traffic report: one table with a row per account. / El de tráfico: una tabla con una fila por cuenta.
        var traffic = await admin.GetReportAsync(ReportKind.Traffic, ReportGroupBy.User, from, DateTimeOffset.UtcNow.AddMinutes(1));
        output.WriteLine("traffic: " + string.Join(" | ", traffic.Groups.SelectMany(g => g.Rows).Select(r => string.Join(", ", r.Select(p => $"{p.Key}={p.Value}")))));
        Assert.Equal(["userid", "length", "bytesin", "bytesout"], traffic.Columns.Select(c => c.Id));
        Assert.Equal("0", Assert.Single(traffic.Groups).Key);

        // Sign-ins, per user. The server logs only sign-ins on its website (and single sign-on), not the app's
        // (measured: none appeared), so the test signs in on the website's form first.
        // Entradas por usuario. El servidor solo apunta las entradas de su web (y del inicio único), no las de la app
        // (medido: no salía ninguna), así que la prueba entra antes por el formulario de la web.
        using (var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => new CertificateTrust(Env("MESHTEST_CERT_SHA256")).Validate(m, c, ch, e) })
        using (var http = new System.Net.Http.HttpClient(handler))
        {
            var form = new System.Net.Http.FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "login", ["username"] = "admin", ["password"] = "Admin-Pass-1234" });
            (await http.PostAsync(admin.Server.BaseUri, form)).Dispose();
        }
        var logins = await admin.GetReportAsync(ReportKind.Logins, ReportGroupBy.User, from, DateTimeOffset.UtcNow.AddMinutes(1));
        output.WriteLine("logins: " + string.Join(" | ", logins.Groups.Select(g => g.Key + ": " + string.Join("; ", g.Rows.Select(r => string.Join(", ", r.Select(p => $"{p.Key}={p.Value}")))))));
        var mine = Assert.Single(logins.Groups, g => g.Key == admin.User.Id);
        Assert.Contains(mine.Rows, r => r.ContainsKey("ip") && r.ContainsKey("time")); // no browser: HttpClient sends no User-Agent / sin navegador: HttpClient no manda User-Agent

        var db = await admin.GetReportAsync(ReportKind.Database, ReportGroupBy.User, from, DateTimeOffset.UtcNow);
        Assert.Contains(db.Groups.Single().Rows, r => r["record"]?.ToString() == "user");

        // What the server would drop without answering is stopped here. / Lo que el servidor tiraría sin contestar se para aquí.
        var bad = await Assert.ThrowsAsync<MeshClientException>(() => admin.GetReportAsync(ReportKind.Logins, ReportGroupBy.Device, from, DateTimeOffset.UtcNow));
        Assert.Equal("servidor/informe/dato-invalido", bad.BurburitoKey);
        bad = await Assert.ThrowsAsync<MeshClientException>(() => admin.GetReportAsync(ReportKind.RemoteSessions, ReportGroupBy.User, DateTimeOffset.UtcNow, from));
        Assert.Equal("servidor/informe/dato-invalido", bad.BurburitoKey);
        await using var user = await As("user", "User-Pass-1234");
        bad = await Assert.ThrowsAsync<MeshClientException>(() => user.GetReportAsync(ReportKind.Database, ReportGroupBy.User, from, DateTimeOffset.UtcNow));
        Assert.Equal("servidor/informe/no-autorizado", bad.BurburitoKey);
    }

    [Fact]
    public async Task The_server_s_live_state_and_error_log_are_for_its_administrators()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var got = new TaskCompletionSource<ServerStats>(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.ServerStatsReceived += (_, s) => got.TrySetResult(s);
        await admin.WatchServerStatsAsync(TimeSpan.FromSeconds(1));
        var stats = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await admin.WatchServerStatsAsync(null);
        output.WriteLine($"memory {stats.FreeMemory}/{stats.TotalMemory}, cpu {string.Join(" ", stats.CpuAverage)}, {string.Join(", ", stats.State.Select(p => $"{p.Key}={p.Value}"))}");
        Assert.True(stats.TotalMemory > stats.FreeMemory && stats.FreeMemory > 0);
        Assert.True(stats.State["UserAccounts"] >= 3);
        Assert.True(stats.State["AgentSessions"] >= 1);
        Assert.Equal(3, stats.CpuAverage.Count);
        output.WriteLine($"error log: {(await admin.GetServerErrorsAsync()).Length} characters");

        await using var user = await As("user", "User-Pass-1234");
        Assert.False(user.CanSeeServer);
        var denied = await Assert.ThrowsAsync<MeshClientException>(() => user.WatchServerStatsAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("servidor/abrir-detalle/no-autorizado", denied.BurburitoKey);
        denied = await Assert.ThrowsAsync<MeshClientException>(() => user.GetServerErrorsAsync());
        Assert.Equal("servidor/abrir-detalle/no-autorizado", denied.BurburitoKey);
    }

    [Fact]
    public async Task A_notification_reaches_the_account_it_is_for_and_not_the_others()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var t = Tag();
        var oneId = await admin.CreateUserAsync(new("uno" + t, "Uno-Pass-1234"));
        var twoId = await admin.CreateUserAsync(new("dos" + t, "Dos-Pass-1234"));
        try
        {
            await using var one = await As("uno" + t, "Uno-Pass-1234");
            await using var two = await As("dos" + t, "Dos-Pass-1234");
            var toOne = new ConcurrentQueue<ServerNotification>();
            var toTwo = new ConcurrentQueue<ServerNotification>();
            one.NotificationReceived += (_, n) => toOne.Enqueue(n);
            two.NotificationReceived += (_, n) => toTwo.Enqueue(n);

            var users = await admin.GetUsersAsync();
            await admin.SendNotificationAsync("Solo para uno " + t, users.Single(u => u.Id == oneId));
            var n = await GroupsIntegrationTests.Until(() => toOne.FirstOrDefault(x => x.Text == "Solo para uno " + t), "the notification reaching its account");
            Assert.Equal(admin.User.Name, n.Title);
            await admin.SendNotificationAsync("Para todos " + t);
            await GroupsIntegrationTests.Until(() => toTwo.FirstOrDefault(x => x.Text == "Para todos " + t), "the broadcast reaching everyone");
            Assert.DoesNotContain(toTwo, x => x.Text == "Solo para uno " + t);

            var tooLong = await Assert.ThrowsAsync<MeshClientException>(() => admin.SendNotificationAsync(new string('x', 513)));
            Assert.Equal("servidor/enviar-mensaje/dato-invalido", tooLong.BurburitoKey);
            var denied = await Assert.ThrowsAsync<MeshClientException>(() => one.SendNotificationAsync("hola"));
            Assert.Equal("servidor/enviar-mensaje/no-autorizado", denied.BurburitoKey);
        }
        finally
        {
            foreach (var u in (await admin.GetUsersAsync()).Where(u => u.Id == oneId || u.Id == twoId)) await admin.DeleteUserAsync(u);
        }
    }
}
