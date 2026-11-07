using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

public class UserErrorTests
{
    public static TheoryData<MeshFailure> AllFailures()
    {
        var d = new TheoryData<MeshFailure>();
        foreach (var f in Enum.GetValues<MeshFailure>()) d.Add(f);
        return d;
    }

    /// <summary>Every failure has its own plain-language text, never the generic fallback.
    /// Cada fallo tiene su texto en cristiano, nunca el genérico de reserva.</summary>
    [Theory]
    [MemberData(nameof(AllFailures))]
    public void Every_failure_has_its_own_message(MeshFailure f)
    {
        var e = UserError.From(new MeshClientException(f, "conexion/iniciar-sesion/sin-conexion", "x"));
        Assert.NotEqual("Something failed.", e.Message);
        Assert.NotEqual("Algo ha fallado.", e.Message);
        Assert.Contains("burburito", e.Footer, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Burburito.Code("conexion/iniciar-sesion/sin-conexion"), e.Footer);
    }

    [Fact]
    public void Technical_details_carry_the_server_message_and_version()
    {
        var e = UserError.From(new MeshClientException(MeshFailure.Rejected, "conexion/iniciar-sesion/rechazo-remoto", "x") { ServerMessage = "weird-1", HttpStatus = 502 }, "1.3.1");
        Assert.Contains("weird-1", e.Technical);
        Assert.Contains("HTTP 502", e.Technical);
        Assert.Contains("MeshCentral 1.3.1", e.Technical);
        Assert.Contains(e.Footer, e.CopyText);
    }
}
