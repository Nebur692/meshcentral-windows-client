using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// A file just written on a Linux device on Madrid time shows the time its clock reads, not one hour (or two) more: the
/// agent sends that reading with a false "Z" (measured 18-11-2026; Windows agents send true UTC).
/// Un fichero recién escrito en un equipo Linux en hora de Madrid sale con la hora de su reloj, no una (o dos) más.
/// </summary>
[Trait("Category", "Integration")]
public class FileTimesIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    [Fact]
    public async Task A_new_file_shows_the_device_clock()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        Device? d = null;
        for (var i = 0; i < 60 && d is null; i++) { d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline); if (d is null) await Task.Delay(500); }
        Assert.NotNull(d);
        Assert.False(d!.IsWindows);
        await using var f = await FilesSession.OpenAsync(s, d);
        var name = "hora-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        await f.UploadAsync("/tmp", name, new MemoryStream([1, 2, 3]), new Progress<long>());
        try
        {
            var e = (await f.ListAsync("/tmp")).Entries.First(x => x.Name == name);
            // The bench's agent and this test both run on Madrid time (run-integration.sh), as a person and their device:
            // what the app shows (the time in this machine's zone) must be what the device's clock read.
            // El agente del banco y esta prueba van en hora de Madrid, como una persona y su equipo: lo que enseña la app
            // (la hora en la zona de esta máquina) tiene que ser lo que marcaba el reloj del equipo.
            Assert.Equal("Europe/Madrid", TimeZoneInfo.Local.Id);
            var deviceClock = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));
            Assert.InRange((e.Modified!.Value.ToLocalTime().DateTime - deviceClock).TotalMinutes, -2, 2);
        }
        finally { await f.DeleteAsync("/tmp", [name], recursive: false); }
    }

    [Fact]
    public void Windows_times_stay_utc_and_others_keep_their_clock_reading()
    {
        var raw = System.Text.Json.Nodes.JsonValue.Create("2026-11-18T11:05:30Z");
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T11:05:30Z"), FilesSession.ParseDate(raw, deviceClock: false));
        Assert.Equal(new DateTime(2026, 11, 18, 11, 5, 30), FilesSession.ParseDate(raw, deviceClock: true)!.Value.ToLocalTime().DateTime);
    }
}
