using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>The device sheet's hardware lines. / Las líneas de hardware de la ficha del equipo.</summary>
internal static class HardwareText
{
    public static string Memory(HardwareSummary h)
    {
        if (h.Memory.Count == 0) return "";
        // Equal modules count as one line: "2 × 8.00 GB DDR4 2666 MHz Kingston". / Los módulos iguales cuentan como una línea.
        var lines = h.Memory.GroupBy(m => m).Select(g => (g.Count() > 1 ? g.Count() + " × " : "") +
            string.Join(" ", new[] { ReportFormat.Bytes(g.Key.Bytes), g.Key.Type, g.Key.Speed, g.Key.Maker }.Where(x => x is not null)));
        return ReportFormat.Bytes(h.TotalMemory) + "\n" + string.Join("\n", lines);
    }

    public static string Disks(HardwareSummary h) => string.Join("\n", h.Disks.Select(d =>
        d.Name + " — " + (d.Bytes is { } b ? ReportFormat.Bytes(b) : d.SizeText)));

    public static string Volumes(HardwareSummary h) => string.Join("\n", h.Volumes.Select(v =>
        string.Join(" ", new[] { v.Name, v.Label, v.FileSystem is null ? null : $"({v.FileSystem})" }.Where(x => x is { Length: > 0 })) +
        " — " + L.F("VolumeFree", ReportFormat.Bytes(v.FreeBytes), ReportFormat.Bytes(v.Bytes))));
}
