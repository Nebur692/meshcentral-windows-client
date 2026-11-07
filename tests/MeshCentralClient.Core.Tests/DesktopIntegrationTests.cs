using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>The desktop against the real Linux agent, which has no screen. / El escritorio contra el agente Linux real, que no tiene pantalla.</summary>
[Trait("Category", "Integration")]
public class DesktopIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    [Fact]
    public async Task A_device_without_a_screen_says_so_instead_of_hanging()
    {
        Assert.True(ServerAddress.TryParse(Env("MESHTEST_URL"), out var a));
        await using var s = await MeshServerSession.ConnectAsync(a!, new("admin", "Admin-Pass-1234"), new CertificateTrust(Env("MESHTEST_CERT_SHA256")));
        Device? dev = null;
        for (var i = 0; i < 60 && dev is null; i++) { dev = (await s.GetDevicesAsync()).FirstOrDefault(x => x.GroupId == Env("MESHTEST_GROUP_ID") && x.IsOnline); if (dev is null) await Task.Delay(500); }
        Assert.NotNull(dev);

        var said = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        DesktopSession? d = null;
        try
        {
            d = await DesktopSession.OpenAsync(s, dev!.Id);
            d.Message += (_, m) => said.TrySetResult("message: " + m);
            d.Tunnel.Closed += (_, e) => said.TrySetResult("closed: " + e.Message);
            d.ScreenSizeChanged += (_, z) => said.TrySetResult($"screen {z.Width}x{z.Height}");
        }
        catch (MeshClientException e) { said.TrySetResult("open failed: " + e.BurburitoKey); }
        var what = await said.Task.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine(what);
        // Measured on MeshCentral 1.3.1: the agent explains it in a console message. / Medido: el agente lo explica en un mensaje de consola.
        Assert.StartsWith("message: ", what);
        Assert.Contains("XServer", what);
        if (d is not null) await d.DisposeAsync();
    }
}
