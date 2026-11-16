using System.Runtime.InteropServices;
using MeshCentralClient.App;
using Xunit;

namespace MeshCentralClient.App.Tests;

/// <summary>
/// webauthn.dll is there and our structures have webauthn.h's sizes (x64, version 1). Asking a real key needs a person in
/// front of Windows' window, so that is tried by hand; the protocol is tested in the core with a key in software.
/// webauthn.dll está y nuestras estructuras miden lo que webauthn.h (x64, versión 1). Pedir una llave de verdad necesita a
/// una persona delante, así que eso se prueba a mano; el protocolo se prueba en el núcleo con una llave por software.
/// </summary>
public class WindowsSecurityKeyTests
{
    [Fact]
    public void Windows_has_its_webauthn() => Assert.True(WindowsSecurityKey.Available);

    [Fact]
    public void The_structures_have_the_sizes_of_webauthn_h()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Marshal.SizeOf<WindowsSecurityKey.Native.RpEntity>());
        Assert.Equal(40, Marshal.SizeOf<WindowsSecurityKey.Native.UserEntity>());
        Assert.Equal(24, Marshal.SizeOf<WindowsSecurityKey.Native.ClientData>());
        Assert.Equal(24, Marshal.SizeOf<WindowsSecurityKey.Native.Credential>());
        Assert.Equal(64, Marshal.SizeOf<WindowsSecurityKey.Native.MakeCredentialOptions>());
        Assert.Equal(56, Marshal.SizeOf<WindowsSecurityKey.Native.GetAssertionOptions>());
        Assert.Equal(72, Marshal.SizeOf<WindowsSecurityKey.Native.Assertion>());
        Assert.Equal(96, Marshal.SizeOf<WindowsSecurityKey.Native.CredentialAttestation>());
    }
}
