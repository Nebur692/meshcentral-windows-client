using MeshCentralClient.Core;
using Xunit;

public class DeviceLinksTests
{
    [Fact]
    public void Hex_node_id_round_trips_to_the_base64_id()
    {
        // A real node id from a MeshCentral 1.3.1 test server. / Un id real de un servidor de pruebas MeshCentral 1.3.1.
        const string id = "node//Vbqd$oEbFoIqfZDnJhB7Z4I13OBWhPt8m6wrkSpSAWw0dhYF09Hc4i40M5W@LLre";
        var hex = DeviceLinks.HexNodeId(id);
        Assert.Equal(96, hex.Length);
        // The same conversion the website does: btoa(hex2rstr(hex)) with + → @ and / → $.
        // La misma conversión que hace la web: btoa(hex2rstr(hex)) con + → @ y / → $.
        var back = Convert.ToBase64String(Convert.FromHexString(hex)).Replace('+', '@').Replace('/', '$');
        Assert.Equal(id["node//".Length..], back);
    }

    [Fact]
    public void Works_with_a_domain() =>
        Assert.Equal(96, DeviceLinks.HexNodeId("node/customers/Vbqd$oEbFoIqfZDnJhB7Z4I13OBWhPt8m6wrkSpSAWw0dhYF09Hc4i40M5W@LLre").Length);
}
