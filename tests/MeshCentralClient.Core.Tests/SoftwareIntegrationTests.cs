using MeshCentralClient.Core;
using Xunit;

/// <summary>The programs installed on the test device (Debian: dpkg) come as a list with names and versions.
/// Los programas instalados en el equipo de prueba llegan como lista con nombres y versiones.</summary>
[Trait("Category", "Integration")]
public class SoftwareIntegrationTests
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    [Fact]
    public async Task The_installed_programs_of_the_device_are_listed()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        await s.GetDeviceGroupsAsync();
        Device? d = null;
        for (var i = 0; i < 60 && d is null; i++) { d = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline); if (d is null) await Task.Delay(500); }
        Assert.NotNull(d);
        Assert.True(s.CanSeeSoftware(d!));
        var programs = await s.GetInstalledProgramsAsync(d!);
        Assert.True(programs.Count > 20, $"only {programs.Count} programs");
        var bash = Assert.Single(programs, p => p.Name == "bash");   // Debian always has it / Debian siempre lo tiene
        Assert.False(string.IsNullOrEmpty(bash.Version));
    }
}
