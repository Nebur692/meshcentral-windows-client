using System.Runtime.InteropServices;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>
/// A security key through Windows' own WebAuthn (webauthn.dll): Windows Hello (face, fingerprint or PIN), a USB or NFC
/// key, or a phone by QR code, in Windows' own window. The client data comes made by the core (the server's challenge and
/// https origin), so the answer is the one a browser would give for that server.
/// Una llave de seguridad con el WebAuthn propio de Windows: Windows Hello, una llave USB o NFC, o un móvil con QR, en la
/// ventana de Windows. Los datos del cliente los hace el núcleo, así que la respuesta es la de un navegador.
/// </summary>
public sealed class WindowsSecurityKey(IntPtr window) : ISecurityKey
{
    /// <summary>The person closed Windows' window. / La persona cerró la ventana de Windows.</summary>
    private const int NteUserCancelled = unchecked((int)0x80090036);

    /// <summary>Windows has webauthn.dll (Windows 10 1903 and later). / Windows tiene webauthn.dll.</summary>
    public static bool Available
    {
        get
        {
            try { return Native.WebAuthNGetApiVersionNumber() >= 1; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    public Task<KeyAssertion> GetAssertionAsync(KeyRequest r, CancellationToken ct) => Task.Run(() =>
    {
        using var mem = new Unmanaged();
        var clientData = mem.ClientData(r.ClientDataJson);
        var options = new Native.GetAssertionOptions
        {
            dwVersion = 1, dwTimeoutMilliseconds = (uint)r.Timeout.TotalMilliseconds,
            CredentialList = mem.Credentials(r.AllowedIds), dwUserVerificationRequirement = Verification(r.UserVerification),
        };
        Check(Native.WebAuthNAuthenticatorGetAssertion(window, r.RpId, ref clientData, ref options, out var p));
        try
        {
            var a = Marshal.PtrToStructure<Native.Assertion>(p);
            return new KeyAssertion(Bytes(a.Credential.pbId, a.Credential.cbId), Bytes(a.pbAuthenticatorData, a.cbAuthenticatorData),
                Bytes(a.pbSignature, a.cbSignature), a.cbUserId > 0 ? Bytes(a.pbUserId, a.cbUserId) : null);
        }
        finally { Native.WebAuthNFreeAssertion(p); }
    }, ct);

    public Task<KeyAttestation> MakeCredentialAsync(KeyCreation c, CancellationToken ct) => Task.Run(() =>
    {
        using var mem = new Unmanaged();
        var rp = new Native.RpEntity { dwVersion = 1, pwszId = mem.Text(c.RpId), pwszName = mem.Text(c.RpName) };
        var user = new Native.UserEntity
        {
            dwVersion = 1, cbId = (uint)c.UserId.Length, pbId = mem.Pin(c.UserId), pwszName = mem.Text(c.UserName), pwszDisplayName = mem.Text(c.UserDisplayName),
        };
        var param = mem.Struct(new Native.CoseParameter { dwVersion = 1, pwszCredentialType = mem.Text("public-key"), lAlg = -7 });
        var parameters = new Native.CoseParameters { cCredentialParameters = 1, pCredentialParameters = param };
        var clientData = mem.ClientData(c.ClientDataJson);
        var options = new Native.MakeCredentialOptions
        {
            dwVersion = 1, dwTimeoutMilliseconds = (uint)c.Timeout.TotalMilliseconds, CredentialList = mem.Credentials(c.ExcludedIds),
            dwUserVerificationRequirement = Verification(c.UserVerification), dwAttestationConveyancePreference = 1, // none / ninguna
        };
        Check(Native.WebAuthNAuthenticatorMakeCredential(window, ref rp, ref user, ref parameters, ref clientData, ref options, out var p));
        try
        {
            var a = Marshal.PtrToStructure<Native.CredentialAttestation>(p);
            return new KeyAttestation(Bytes(a.pbCredentialId, a.cbCredentialId), Bytes(a.pbAttestationObject, a.cbAttestationObject));
        }
        finally { Native.WebAuthNFreeCredentialAttestation(p); }
    }, ct);

    private static uint Verification(string v) => v switch { "required" => 1, "discouraged" => 3, _ => 2 };

    private static void Check(int hr)
    {
        if (hr == 0) return;
        if (hr == NteUserCancelled) throw new OperationCanceledException("The person closed Windows' security key window.");
        var name = Marshal.PtrToStringUni(Native.WebAuthNGetErrorName(hr)) ?? "";
        throw new MeshClientException(MeshFailure.Rejected, "conexion/verificar-identidad/dispositivo-mudo", $"Windows' security key said no ({name}, 0x{hr:X8}).");
    }

    private static byte[] Bytes(IntPtr p, uint n)
    {
        var b = new byte[n];
        if (n > 0) Marshal.Copy(p, b, 0, (int)n);
        return b;
    }

    /// <summary>Unmanaged copies alive for one call. / Copias no gestionadas vivas durante una llamada.</summary>
    private sealed class Unmanaged : IDisposable
    {
        private readonly List<IntPtr> _blocks = [];

        private IntPtr Keep(IntPtr p) { _blocks.Add(p); return p; }
        public IntPtr Text(string s) => Keep(Marshal.StringToHGlobalUni(s));
        public IntPtr Pin(byte[] b) { var p = Keep(Marshal.AllocHGlobal(Math.Max(1, b.Length))); Marshal.Copy(b, 0, p, b.Length); return p; }
        public IntPtr Struct<T>(T s) where T : struct { var p = Keep(Marshal.AllocHGlobal(Marshal.SizeOf<T>())); Marshal.StructureToPtr(s, p, false); return p; }

        public Native.ClientData ClientData(byte[] json) => new()
        {
            dwVersion = 1, cbClientDataJSON = (uint)json.Length, pbClientDataJSON = Pin(json), pwszHashAlgId = Text("SHA-256"),
        };

        public Native.Credentials Credentials(IReadOnlyList<byte[]> ids)
        {
            if (ids.Count == 0) return default;
            var size = Marshal.SizeOf<Native.Credential>();
            var array = Keep(Marshal.AllocHGlobal(size * ids.Count));
            var type = Text("public-key");
            for (var i = 0; i < ids.Count; i++)
                Marshal.StructureToPtr(new Native.Credential { dwVersion = 1, cbId = (uint)ids[i].Length, pbId = Pin(ids[i]), pwszCredentialType = type }, array + i * size, false);
            return new Native.Credentials { cCredentials = (uint)ids.Count, pCredentials = array };
        }

        public void Dispose() { foreach (var p in _blocks) Marshal.FreeHGlobal(p); }
    }

    /// <summary>webauthn.h, version 1 of each structure (any Windows with webauthn.dll takes it). / Versión 1 de cada estructura.</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RpEntity { public uint dwVersion; public IntPtr pwszId, pwszName, pwszIcon; }
        [StructLayout(LayoutKind.Sequential)] public struct UserEntity { public uint dwVersion, cbId; public IntPtr pbId, pwszName, pwszIcon, pwszDisplayName; }
        [StructLayout(LayoutKind.Sequential)] public struct CoseParameter { public uint dwVersion; public IntPtr pwszCredentialType; public int lAlg; }
        [StructLayout(LayoutKind.Sequential)] public struct CoseParameters { public uint cCredentialParameters; public IntPtr pCredentialParameters; }
        [StructLayout(LayoutKind.Sequential)] public struct ClientData { public uint dwVersion, cbClientDataJSON; public IntPtr pbClientDataJSON, pwszHashAlgId; }
        [StructLayout(LayoutKind.Sequential)] public struct Credential { public uint dwVersion, cbId; public IntPtr pbId, pwszCredentialType; }
        [StructLayout(LayoutKind.Sequential)] public struct Credentials { public uint cCredentials; public IntPtr pCredentials; }
        [StructLayout(LayoutKind.Sequential)] public struct Extensions { public uint cExtensions; public IntPtr pExtensions; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MakeCredentialOptions
        {
            public uint dwVersion, dwTimeoutMilliseconds;
            public Credentials CredentialList;
            public Extensions Extensions;
            public uint dwAuthenticatorAttachment;
            public int bRequireResidentKey;
            public uint dwUserVerificationRequirement, dwAttestationConveyancePreference, dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GetAssertionOptions
        {
            public uint dwVersion, dwTimeoutMilliseconds;
            public Credentials CredentialList;
            public Extensions Extensions;
            public uint dwAuthenticatorAttachment, dwUserVerificationRequirement, dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CredentialAttestation
        {
            public uint dwVersion;
            public IntPtr pwszFormatType;
            public uint cbAuthenticatorData;
            public IntPtr pbAuthenticatorData;
            public uint cbAttestation;
            public IntPtr pbAttestation;
            public uint dwAttestationDecodeType;
            public IntPtr pvAttestationDecode;
            public uint cbAttestationObject;
            public IntPtr pbAttestationObject;
            public uint cbCredentialId;
            public IntPtr pbCredentialId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Assertion
        {
            public uint dwVersion, cbAuthenticatorData;
            public IntPtr pbAuthenticatorData;
            public uint cbSignature;
            public IntPtr pbSignature;
            public Credential Credential;
            public uint cbUserId;
            public IntPtr pbUserId;
        }

        [DllImport("webauthn.dll")] public static extern uint WebAuthNGetApiVersionNumber();
        [DllImport("webauthn.dll")] public static extern IntPtr WebAuthNGetErrorName(int hr);
        [DllImport("webauthn.dll")] public static extern void WebAuthNFreeCredentialAttestation(IntPtr p);
        [DllImport("webauthn.dll")] public static extern void WebAuthNFreeAssertion(IntPtr p);

        [DllImport("webauthn.dll")]
        public static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hWnd, ref RpEntity rp, ref UserEntity user, ref CoseParameters parameters,
            ref ClientData clientData, ref MakeCredentialOptions options, out IntPtr attestation);

        [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
        public static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hWnd, string rpId, ref ClientData clientData, ref GetAssertionOptions options, out IntPtr assertion);
    }
}
