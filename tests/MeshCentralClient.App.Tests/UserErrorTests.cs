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

    /// <summary>Each with the kind it is really thrown with: that kind alone says "login" or "server". / Cada uno con el tipo con el que se lanza de verdad.</summary>
    [Theory]
    [InlineData(MeshFailure.Timeout, "tunel/ver-pantalla/dispositivo-mudo")]
    [InlineData(MeshFailure.Rejected, "acciones/ejecutar-ordenes/no-autorizado")]
    [InlineData(MeshFailure.Rejected, "acciones/ejecutar-ordenes/sin-conexion")]
    [InlineData(MeshFailure.Timeout, "acciones/ejecutar-ordenes/tiempo-agotado")]
    [InlineData(MeshFailure.ConnectionLost, "acciones/ejecutar-ordenes/conexion-cortada")]
    [InlineData(MeshFailure.UnexpectedResponse, "acciones/ejecutar-ordenes/respuesta-ilegible")]
    [InlineData(MeshFailure.Rejected, "acciones/ejecutar-ordenes/rechazo-remoto")]
    public void What_it_says_is_about_this_action_and_not_about_signing_in(MeshFailure failure, string key)
    {
        var generic = UserError.From(new MeshClientException(failure, "conexion/iniciar-sesion/sin-conexion", "x")).Message;
        var e = UserError.From(new MeshClientException(failure, key, "x"));
        Assert.NotEqual(generic, e.Message);
        Assert.True(Burburito.IsKnown(key), key);
        Assert.Contains(Burburito.Code(key), e.Footer);
    }

}
