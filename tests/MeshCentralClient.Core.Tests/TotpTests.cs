using Xunit;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1 secret "12345678901234567890" = GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ. Truncated to 6 digits.
    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Matches_the_rfc_vectors(long t, string code) =>
        Assert.Equal(code, Totp.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(t)));
}
