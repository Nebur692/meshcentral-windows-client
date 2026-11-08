using System.Text.Json.Nodes;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

/// <summary>Report values read as the website shows them. / Los valores de un informe se leen como en la web.</summary>
public class ReportFormatTests
{
    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(3_145_728L, "3.0 MB")]
    public void Bytes_read_in_the_right_unit(long bytes, string english)
    {
        if (!L.Spanish) Assert.Equal(english, ReportFormat.Bytes(bytes));
        else Assert.Equal(english.Replace('.', ','), ReportFormat.Bytes(bytes));
    }

    [Theory]
    [InlineData(59L, "0:59")]
    [InlineData(61L, "1:01")]
    [InlineData(3725L, "1:02:05")]
    public void Seconds_read_as_a_length(long s, string expected) => Assert.Equal(expected, ReportFormat.Seconds(s));

    [Fact]
    public void Sessions_and_sign_ins_are_named()
    {
        Assert.Equal(L.T("ProtoTerminal"), ReportFormat.Protocol(1));
        Assert.Equal(L.T("ProtoTerminal"), ReportFormat.Protocol(6)); // PowerShell is a terminal too / PowerShell también es terminal
        Assert.Equal(L.T("ProtoDesktop"), ReportFormat.Protocol(2));
        Assert.Equal(L.T("ProtoFiles"), ReportFormat.Protocol(5));
        Assert.Equal(L.T("ProtoAmt"), ReportFormat.Protocol(101));
        Assert.Equal(L.F("ProtoOther", 42), ReportFormat.Protocol(42));
        Assert.Equal(L.T("LoginOk"), ReportFormat.Login(107));
        Assert.Equal(L.T("LoginBadPassword"), ReportFormat.Login(110));
    }

    [Fact]
    public void A_time_in_milliseconds_reads_as_a_local_date()
    {
        var at = new DateTimeOffset(2026, 11, 8, 10, 30, 0, TimeSpan.Zero);
        var shown = ReportFormat.Value(new ReportColumn("time", "time", "datetime"), JsonValue.Create(at.ToUnixTimeMilliseconds()), null!);
        Assert.Equal(at.ToLocalTime().ToString("g"), shown);
    }

    /// <summary>Every counter MeshCentral 1.3.1 sends in serverstats has its text; an unknown one keeps its own name.
    /// Cada contador que manda serverstats tiene su texto; uno desconocido se queda con su nombre.</summary>
    [Theory]
    [InlineData("UserAccounts")] [InlineData("DeviceGroups")] [InlineData("AgentSessions")] [InlineData("ConnectedUsers")] [InlineData("UsersSessions")]
    [InlineData("RelaySessions")] [InlineData("RelayCount")] [InlineData("RelayErrors")] [InlineData("ConnectedIntelAMT")] [InlineData("ConnectedIntelAMTCira")]
    public void Every_server_counter_has_its_text(string key) => Assert.NotEqual(key, ServerWindow.CounterName(key));

    [Fact]
    public void An_unknown_counter_keeps_the_server_s_name() => Assert.Equal("SomethingNew", ServerWindow.CounterName("SomethingNew"));

    [Fact]
    public void Csv_fields_with_commas_quotes_or_lines_are_quoted()
    {
        Assert.Equal("plain", ReportFormat.Csv("plain"));
        Assert.Equal("\"a,b\"", ReportFormat.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReportFormat.Csv("say \"hi\""));
        Assert.Equal("\"one\ntwo\"", ReportFormat.Csv("one\ntwo"));
    }
}
