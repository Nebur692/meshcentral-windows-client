using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeshCentralClient.Core;

/// <summary>A memory module. / Un módulo de memoria.</summary>
public sealed record MemoryModule(long Bytes, string? Type, string? Speed, string? Maker);

/// <summary>A physical disk; Linux agents send its size as text. / Un disco físico; los agentes de Linux mandan su tamaño como texto.</summary>
public sealed record DiskInfo(string Name, long? Bytes, string? SizeText);

/// <summary>A mounted volume or drive letter. / Un volumen montado o una letra de unidad.</summary>
public sealed record VolumeInfo(string Name, string? Label, string? FileSystem, long Bytes, long FreeBytes);

/// <summary>
/// What a person looks for in a device's sheet, out of an inventory whose shape changes with the system. Shapes seen in real
/// agents (MeshCentral 1.3.1): Windows, windows.memory[].Capacity in bytes as text and windows.volumes by letter in bytes;
/// Linux, linux.memory.Memory_Device[].Size such as "16 GiB" and linux.volumes[] in KiB; both, identifiers.storage_devices,
/// whose Size is bytes, null (a card reader without a card) or text like "931GiB (1TB)". Any of them may be missing.
/// Lo que una persona busca en la ficha, de un inventario cuya forma cambia con el sistema. Formas vistas en agentes reales:
/// Windows, windows.memory[].Capacity en bytes como texto y windows.volumes por letra en bytes; Linux,
/// linux.memory.Memory_Device[].Size como «16 GiB» y linux.volumes[] en KiB; los dos, identifiers.storage_devices, cuyo Size
/// es bytes, null (un lector de tarjetas sin tarjeta) o texto como «931GiB (1TB)». Cualquiera puede faltar.
/// </summary>
public sealed record HardwareSummary(IReadOnlyList<MemoryModule> Memory, IReadOnlyList<DiskInfo> Disks, IReadOnlyList<VolumeInfo> Volumes, IReadOnlyList<string> Graphics, string? Model, string? Serial)
{
    public long TotalMemory => Memory.Sum(m => m.Bytes);

    // Copies of other volumes, or not disks at all. / Copias de otros volúmenes, o que ni son discos.
    private static readonly HashSet<string> NotDisks = new(StringComparer.OrdinalIgnoreCase) { "overlay", "tmpfs", "devtmpfs", "squashfs", "efivarfs", "ramfs", "autofs", "fuse.snapfuse" };
    private static readonly string[] NotDiskMounts = { "/var/lib/docker/", "/snap/", "/run/", "/dev/", "/sys/", "/proc/" };
    // What a manufacturer leaves when it fills nothing in. / Lo que deja un fabricante cuando no rellena nada.
    private static readonly Regex Placeholder = new(@"^(default string|to be filled.*|system (serial number|product name)|none|n/?a|0+|not specified|unknown)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static HardwareSummary From(JsonObject hardware)
    {
        var ids = hardware["identifiers"] as JsonObject;
        var windows = hardware["windows"] as JsonObject;
        var linux = hardware["linux"] as JsonObject;
        return new HardwareSummary(
            ReadMemory(windows, linux),
            (ids?["storage_devices"] as JsonArray)?.OfType<JsonObject>().Select(Disk).Where(d => d is not null).Select(d => d!).ToList() ?? new(),
            ReadVolumes(windows, linux),
            ReadGraphics(ids, windows),
            Clean(ids?.Str("product_name")),
            Clean(ids?.Str("bios_serial")) ?? Clean(linux?.Str("product_serial")));
    }

    private static string? Clean(string? value) => value?.Trim() is { Length: > 0 } v && !Placeholder.IsMatch(v) ? v : null;

    /// <summary>A whole number that may come as a number or as text. / Un número entero que puede venir como número o como texto.</summary>
    private static long? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return v.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static IReadOnlyList<MemoryModule> ReadMemory(JsonObject? windows, JsonObject? linux)
    {
        var list = new List<MemoryModule>();
        foreach (var m in (windows?["memory"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (Number(m["Capacity"]) is > 0 and var bytes)
                list.Add(new MemoryModule(bytes, WindowsType(Number(m["SMBIOSMemoryType"]) ?? 0), Number(m["ConfiguredClockSpeed"] ?? m["Speed"]) is > 0 and var mhz ? mhz + " MHz" : null, Clean(m.Str("Manufacturer"))));
        foreach (var m in ((linux?["memory"] as JsonObject)?["Memory_Device"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (LinuxSize(m.Str("Size")) is { } bytes)
                list.Add(new MemoryModule(bytes, m.Str("Type") is { } t && t is not ("RAM" or "Unknown" or "Other") ? t : null, Clean(m.Str("Speed")), Clean(m.Str("Manufacturer"))));
        return list;
    }

    /// <summary>The SMBIOS memory types still in use. / Los tipos de memoria SMBIOS aún en uso.</summary>
    private static string? WindowsType(long smbios) => smbios switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => null };

    /// <summary>dmidecode's "8 GB" means GiB, as "16 GiB" does; "No Module Installed" is an empty slot. / El «8 GB» de dmidecode significa GiB, como «16 GiB»; «No Module Installed» es una ranura vacía.</summary>
    private static long? LinuxSize(string? size)
    {
        var m = Regex.Match(size ?? "", @"^\s*(\d+)\s*([KMGT])i?B\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (char.ToUpperInvariant(m.Groups[2].Value[0]) switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, _ => 1L << 40 });
    }

    private static DiskInfo? Disk(JsonObject d)
    {
        var name = Clean(d.Str("Model")) ?? Clean(d.Str("Caption")) ?? "?";
        if (Number(d["Size"]) is > 0 and var bytes) return new DiskInfo(name, bytes, null);
        return d.Str("Size") is { Length: > 0 } text && !text.All(c => char.IsDigit(c)) ? new DiskInfo(name, null, text) : null;
    }

    private static IReadOnlyList<VolumeInfo> ReadVolumes(JsonObject? windows, JsonObject? linux)
    {
        var list = new List<VolumeInfo>();
        if (windows?["volumes"] is JsonObject letters)
            foreach (var (letter, node) in letters)
                if (node is JsonObject v && Number(v["size"]) is > 0 and var size)
                    list.Add(new VolumeInfo(letter + ":", Clean(v.Str("name")), v.Str("type"), size, Number(v["sizeremaining"]) ?? 0));
        if (linux?["volumes"] is JsonArray mounts)
            foreach (var v in mounts.OfType<JsonObject>())
            {
                var at = v.Str("mount_point") ?? "";
                if (at.Length == 0 || NotDisks.Contains(v.Str("type") ?? "") || NotDiskMounts.Any(n => at.StartsWith(n, StringComparison.Ordinal))) continue;
                if (Number(v["size"]) is > 0 and var kib) list.Add(new VolumeInfo(at, null, v.Str("type"), kib * 1024, (Number(v["available"]) ?? 0) * 1024));
            }
        return list;
    }

    private static IReadOnlyList<string> ReadGraphics(JsonObject? ids, JsonObject? windows)
    {
        var names = ids?["gpu_name"] switch
        {
            JsonArray a => a.Select(n => n?.ToString() ?? "").ToList(),
            JsonValue v => new List<string> { v.ToString() },
            _ => (windows?["gpu"] as JsonArray)?.OfType<JsonObject>().Select(g => g.Str("Name") ?? "").ToList() ?? new(),
        };
        return names.Select(Clean).Where(n => n is not null).Select(n => n!).Distinct().ToList();
    }
}
