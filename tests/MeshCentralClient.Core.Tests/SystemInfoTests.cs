using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

public class SystemInfoTests
{
    private static SystemInfo Of(string json) => new((JsonObject)JsonNode.Parse(json)!, null);

    // The shape of a real Windows agent's inventory (keys as sent; values shortened). The memory comes before the cpu.
    // La forma del inventario de un agente de Windows real (claves tal cual; valores acortados). La memoria va antes que la cpu.
    private const string Windows = """
        { "windows": { "memory": [ { "Name": "Physical Memory", "Capacity": "17179869184" } ],
                       "cpu": [ { "Name": "INTEL(R) XEON(R) PLATINUM 8573C", "NumberOfCores": 2 } ] },
          "identifiers": { "bios_vendor": "Microsoft Corporation" } }
        """;

    [Fact]
    public void Cpu_comes_from_the_cpu_section_not_from_any_name() =>
        Assert.Equal("INTEL(R) XEON(R) PLATINUM 8573C", Of(Windows).Cpu);

    [Fact]
    public void Cpu_name_wins_when_the_agent_sends_it() =>
        Assert.Equal("Xeon E5", Of("""{ "identifiers": { "cpu_name": "Xeon E5" }, "windows": { "cpu": [ { "Name": "other" } ] } }""").Cpu);

    [Fact]
    public void No_cpu_at_all() => Assert.Null(Of("""{ "windows": { "memory": [ { "Name": "Physical Memory" } ] } }""").Cpu);

    [Fact]
    public void Flatten_gives_paths_with_array_positions() =>
        Assert.Contains(("windows › cpu [1] › NumberOfCores", "2"), Of(Windows).Flatten());
}
