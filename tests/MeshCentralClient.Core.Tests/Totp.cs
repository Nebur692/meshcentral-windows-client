using System.Security.Cryptography;

/// <summary>RFC 6238 (SHA-1, 30 s, 6 digits), only for the tests. / RFC 6238, solo para las pruebas.</summary>
internal static class Totp
{
    public static string Code(string base32Secret, DateTimeOffset? at = null)
    {
        var key = Base32(base32Secret);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = HMACSHA1.HashData(key, msg);
        var o = h[^1] & 0x0F;
        var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string s)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var value = 0; var output = new List<byte>();
        foreach (var c in s.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | A.IndexOf(c);
            bits += 5;
            if (bits >= 8) { output.Add((byte)(value >> (bits - 8))); bits -= 8; }
        }
        return output.ToArray();
    }
}
