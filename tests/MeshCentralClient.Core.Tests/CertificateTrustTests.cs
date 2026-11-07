using MeshCentralClient.Core;
using Xunit;

public class CertificateTrustTests
{
    private const string Hex = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData(Hex)]
    [InlineData("01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef")]
    public void Normalises_pins(string pin) => Assert.Equal(Hex, new CertificateTrust(pin).PinnedSha256);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_pin(string? pin) => Assert.Null(new CertificateTrust(pin).PinnedSha256);

    [Fact]
    public void Rejects_short_pins() => Assert.Throws<ArgumentException>(() => new CertificateTrust("ABCD"));

    [Fact]
    public void Displays_with_colons() => Assert.Equal("AB:CD:EF", CertificateTrust.Display("ABCDEF"));
}
