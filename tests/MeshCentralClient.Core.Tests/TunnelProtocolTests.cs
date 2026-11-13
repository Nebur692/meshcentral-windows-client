using MeshCentralClient.Core;
using Xunit;

namespace MeshCentralClient.Core.Tests;

/// <summary>
/// The tunnel protocols are the numbers the agent's meshcore.js (MeshCentral 1.3.1) reads: 1 and 6 cmd/PowerShell, 8 and
/// 9 the signed-in user's shell/PowerShell (uid = consoleUid), 2 desktop, 5 files; 7 is plugin data, not a terminal.
/// Los protocolos de túnel son los números que lee meshcore.js del agente: el 7 es de los plugins, no una terminal.
/// </summary>
public class TunnelProtocolTests
{
    [Theory]
    [InlineData(TunnelProtocol.Terminal, 1)]
    [InlineData(TunnelProtocol.Desktop, 2)]
    [InlineData(TunnelProtocol.Files, 5)]
    [InlineData(TunnelProtocol.PowerShell, 6)]
    [InlineData(TunnelProtocol.UserShell, 8)]
    [InlineData(TunnelProtocol.UserPowerShell, 9)]
    public void Each_protocol_is_the_number_the_agent_reads(TunnelProtocol protocol, int code) => Assert.Equal(code, (int)protocol);

    [Fact]
    public void No_protocol_is_the_plugins_7() => Assert.DoesNotContain(7, Enum.GetValues<TunnelProtocol>().Select(p => (int)p));
}
