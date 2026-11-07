namespace MeshCentralClient.Core;

public static class DeviceLinks
{
    /// <summary>
    /// "node//AbC@$…" → the 96-hex form MeshCentral's website accepts in ?gotonode= (base64 with @ and $ is not URL-safe).
    /// «node//AbC@$…» → la forma de 96 hexadecimales que acepta la web de MeshCentral en ?gotonode= (el base64 con @ y $ no es seguro en una URL).
    /// </summary>
    public static string HexNodeId(string nodeId)
    {
        var b64 = nodeId[(nodeId.LastIndexOf('/') + 1)..].Replace('@', '+').Replace('$', '/');
        return Convert.ToHexString(Convert.FromBase64String(b64));
    }
}
