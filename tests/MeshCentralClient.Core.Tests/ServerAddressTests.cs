using MeshCentralClient.Core;
using Xunit;

public class ServerAddressTests
{
    [Theory]
    [InlineData("remote.example.com", "https://remote.example.com/", "wss://remote.example.com/control.ashx")]
    [InlineData("remote.example.com:8443", "https://remote.example.com:8443/", "wss://remote.example.com:8443/control.ashx")]
    [InlineData("https://remote.example.com:443", "https://remote.example.com/", "wss://remote.example.com/control.ashx")]
    [InlineData("https://remote.example.com/customers", "https://remote.example.com/customers/", "wss://remote.example.com/customers/control.ashx")]
    [InlineData("https://remote.example.com/customers/login", "https://remote.example.com/customers/", "wss://remote.example.com/customers/control.ashx")]
    [InlineData("wss://remote.example.com/control.ashx", "https://remote.example.com/", "wss://remote.example.com/control.ashx")]
    [InlineData("  https://remote.example.com/  ", "https://remote.example.com/", "wss://remote.example.com/control.ashx")]
    public void Accepts_and_normalises(string input, string baseUri, string control)
    {
        Assert.True(ServerAddress.TryParse(input, out var a));
        Assert.Equal(baseUri, a!.BaseUri.ToString());
        Assert.Equal(control, a.ControlUri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://remote.example.com")]           // not encrypted / sin cifrar
    [InlineData("ftp://remote.example.com")]
    [InlineData("https://user:pass@remote.example.com")] // credentials in the URL / credenciales en la URL
    [InlineData("https://remote.example.com/?a=1")]
    [InlineData("https://remote.example.com/#x")]
    [InlineData("https://")]
    public void Rejects(string? input)
    {
        Assert.False(ServerAddress.TryParse(input, out var a));
        Assert.Null(a);
    }
}
