using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

/// <summary>
/// A device's place on the map, as the website's map_parseNodeLoc: set by hand beats GPS, Wi-Fi and the address; the
/// address one is moved by the website's own hash of the id (expected values computed with its JavaScript).
/// El sitio de un equipo en el mapa, como la web; los valores esperados se calcularon con su JavaScript.
/// </summary>
public class MapPlaceTests
{
    private const string Id = "node//WUMttOt6AvFnB1OL0TKLkPuTqEGIHkic56q3SYpQyMJhqXitn323@s@Q4C$g$dZa";

    private static Device Of(string json) => Device.From((JsonObject)JsonNode.Parse(json)!);

    [Fact]
    public void The_place_set_by_hand_beats_gps_wifi_and_address()
    {
        var d = Of($$"""{"_id":"{{Id}}","iploc":"1,1,5","wifiloc":"2,2,5","gpsloc":"3,3,5","userloc":"40.4168,-3.7038,5"}""");
        Assert.Equal(new MapPlace(40.4168, -3.7038, MapPlaceKind.User), d.MapPlace);
        Assert.Equal(MapPlaceKind.Gps, Of($$"""{"_id":"{{Id}}","iploc":"1,1,5","wifiloc":"2,2,5","gpsloc":"3,3,5"}""").MapPlace!.Kind);
        Assert.Null(Of($$"""{"_id":"{{Id}}"}""").MapPlace);
        Assert.Null(Of($$"""{"_id":"{{Id}}","userloc":"95,0,5"}""").MapPlace);   // not a place / no es un sitio
    }

    [Fact]
    public void A_place_from_the_address_moves_as_the_website_moves_it()
    {
        var p = Of($$"""{"_id":"{{Id}}","iploc":"40.4168,-3.7038,5"}""").MapPlace!;
        Assert.Equal(MapPlaceKind.Ip, p.Kind);
        Assert.Equal(40.420819793440, p.Latitude, 9);
        Assert.Equal(-3.698579912540, p.Longitude, 9);
    }
}
