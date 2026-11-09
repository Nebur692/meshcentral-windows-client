using System.Text.Json.Nodes;
using MeshCentralClient.Core;
using Xunit;

/// <summary>The inventories here keep the shape of what real agents stored on a MeshCentral 1.3.1, with their serial numbers changed.
/// Los inventarios de aquí conservan la forma de lo que guardaron agentes reales en un MeshCentral 1.3.1, con los números de serie cambiados.</summary>
public class HardwareSummaryTests
{
    private static HardwareSummary Of(string json) => HardwareSummary.From(JsonNode.Parse(json)!.AsObject());

    private const string Windows = """
        {"identifiers":{"bios_serial":"ABC123","product_name":"To be filled by O.E.M.","gpu_name":["NVIDIA GeForce GTX 1060","NVIDIA GeForce GTX 1060"],
          "storage_devices":[{"Caption":"Generic- SD/MMC USB Device","Model":"Generic- SD/MMC USB Device","Size":null},
                             {"Caption":"INTEL SSDSC2BW180A","Model":"INTEL SSDSC2BW180A","Size":"180043153920"}]},
         "windows":{"memory":[{"BankLabel":"BANK 0","Capacity":"8589934592","ConfiguredClockSpeed":2666,"Manufacturer":"Kingston","SMBIOSMemoryType":26,"Speed":3200},
                              {"BankLabel":"BANK1","Capacity":"4294967296","Manufacturer":"Samsung         ","SMBIOSMemoryType":24,"Speed":1333}],
                    "volumes":{"C":{"type":"NTFS","size":179386249216,"sizeremaining":89576316928,"dType":3},"D":{"type":"Unknown","size":0,"sizeremaining":0,"dType":5},"E":{"name":"Juegos","type":"NTFS","size":1000186310656,"sizeremaining":983357186048}}}}
        """;

    [Fact]
    public void A_windows_inventory()
    {
        var h = Of(Windows);
        Assert.Equal(12L << 30, h.TotalMemory);
        Assert.Equal(new MemoryModule(8L << 30, "DDR4", "2666 MHz", "Kingston"), h.Memory[0]);
        Assert.Equal(new MemoryModule(4L << 30, "DDR3", "1333 MHz", "Samsung"), h.Memory[1]);
        Assert.Equal(new[] { new DiskInfo("INTEL SSDSC2BW180A", 180043153920, null) }, h.Disks); // the reader without a card goes / el lector sin tarjeta fuera
        Assert.Equal(new[] { "C:", "E:" }, h.Volumes.Select(v => v.Name)); // the empty DVD goes / el DVD vacío fuera
        Assert.Equal("Juegos", h.Volumes[1].Label);
        Assert.Equal(89576316928, h.Volumes[0].FreeBytes);
        Assert.Equal(new[] { "NVIDIA GeForce GTX 1060" }, h.Graphics);
        Assert.Null(h.Model); // a manufacturer's placeholder is no model / el relleno de un fabricante no es un modelo
        Assert.Equal("ABC123", h.Serial);
    }

    [Fact]
    public void A_linux_inventory()
    {
        var h = Of("""
            {"identifiers":{"product_name":"NUC7JYB","storage_devices":[{"Caption":"ATA Disk","Model":"Samsung SSD 870","Size":"931GiB (1TB)"}]},
             "linux":{"product_serial":"XYZ","memory":{"Physical_Memory_Array":[{"MaximumCapacity":"128 GB"}],"Memory_Device":[
                {"Size":"4 GB","Type":"DDR4","Speed":"2400 MT/s","Manufacturer":"Micron"},{"Size":"16 GiB","Type":"RAM","Manufacturer":"QEMU"},{"Size":"512 MB","Type":"Unknown"},{"Size":"No Module Installed","Type":"Unknown"}]},
              "volumes":[{"size":"957150424","used":"1","available":"386945436","mount_point":"/","type":"ext4"},
                         {"size":"202018704","available":"1","mount_point":"/var/lib/docker/overlay2/cd62/merged","type":"overlay"},
                         {"size":"100","available":"1","mount_point":"/run/user/0","type":"tmpfs"},
                         {"size":"200","available":"1","mount_point":"/snap/core/1","type":"ext4"},
                         {"size":"136699008212","available":"1874877320","mount_point":"/mnt/backup/bbdd","type":"cifs"}]}}
            """);
        Assert.Equal((4L << 30) + (16L << 30) + (512L << 20), h.TotalMemory);
        Assert.Equal(new MemoryModule(4L << 30, "DDR4", "2400 MT/s", "Micron"), h.Memory[0]);
        Assert.Null(h.Memory[1].Type); // "RAM" says nothing / «RAM» no dice nada
        Assert.Equal(3, h.Memory.Count); // the empty slot goes / la ranura vacía fuera
        Assert.Equal(new[] { new DiskInfo("Samsung SSD 870", null, "931GiB (1TB)") }, h.Disks);
        Assert.Equal(new[] { "/", "/mnt/backup/bbdd" }, h.Volumes.Select(v => v.Name));
        Assert.Equal(957150424L * 1024, h.Volumes[0].Bytes);
        Assert.Equal(386945436L * 1024, h.Volumes[0].FreeBytes);
        Assert.Equal("NUC7JYB", h.Model);
        Assert.Equal("XYZ", h.Serial);
    }

    /// <summary>A memory device as SMBIOS lays it out (type 17). / Un dispositivo de memoria como lo dispone SMBIOS (tipo 17).</summary>
    private static byte[] Dmi(int sizeWord, int type = 26, int speed = 2400, uint extended = 0, string maker = "Crucial")
    {
        var d = new byte[0x22];
        d[0] = 17; d[1] = 0x22;
        BitConverter.TryWriteBytes(d.AsSpan(0x0C), (ushort)sizeWord);
        d[0x12] = (byte)type;
        BitConverter.TryWriteBytes(d.AsSpan(0x15), (ushort)speed);
        d[0x17] = 2; // second string / segunda cadena
        BitConverter.TryWriteBytes(d.AsSpan(0x1C), extended);
        return [.. d, .. System.Text.Encoding.ASCII.GetBytes("DIMM 0\0" + maker + "\0\0")];
    }

    [Fact]
    public void The_firmwares_memory_table_says_each_module_without_dmidecode()
    {
        Assert.Equal(new MemoryModule(8L << 30, "DDR4", "2400 MT/s", "Crucial"), HardwareSummary.DmiModule(Dmi(8192)));
        Assert.Equal(512L * 1024, HardwareSummary.DmiModule(Dmi(0x8000 | 512))!.Bytes); // bit 15: KB
        Assert.Equal(64L << 30, HardwareSummary.DmiModule(Dmi(0x7FFF, extended: 65536))!.Bytes); // too big: the extended field / demasiado grande: el campo extendido
        Assert.Null(HardwareSummary.DmiModule(Dmi(0))); // an empty slot / una ranura vacía
        Assert.Null(HardwareSummary.DmiModule(Dmi(0xFFFF))); // unknown / desconocido
        Assert.Null(HardwareSummary.DmiModule(new byte[] { 16, 4, 0, 0 })); // another table / otra tabla
    }

    [Fact]
    public void A_linux_without_lshw_or_dmidecode_is_filled_from_the_probe()
    {
        var bare = Of("""{"identifiers":{"cpu_name":"J5005"},"linux":{"volumes":[{"size":"100","available":"50","mount_point":"/","type":"ext4"}]}}""");
        Assert.True(bare.MissingLinuxParts(JsonNode.Parse("""{"linux":{}}""")!.AsObject()));
        var filled = bare.WithLinuxProbe($"""
            MCC-MEM 8041000
            TYPE="disk" SIZE="250059350016" MODEL="Samsung SSD 860"
            TYPE="disk" SIZE="32212254720" MODEL=""
            TYPE="rom" SIZE="1073741312" MODEL="DVD"
            TYPE="loop" SIZE="4096" MODEL=""
            MCC-DMI {Convert.ToBase64String(Dmi(4096))}
            MCC-DMI {Convert.ToBase64String(Dmi(4096))}
            MCC-DMI {Convert.ToBase64String(Dmi(0))}
            MCC-DMI no-es-base64!
            """.Replace("\r", ""));
        Assert.Equal(2, filled.Memory.Count);
        Assert.Equal(8L << 30, filled.TotalMemory);
        Assert.Equal(new[] { new DiskInfo("Samsung SSD 860", 250059350016, null), new DiskInfo("?", 32212254720, null) }, filled.Disks);
        Assert.Single(filled.Volumes); // what it had stays / lo que tenía se queda
    }

    [Fact]
    public void Without_the_firmwares_table_the_kernels_total_is_said()
    {
        var filled = Of("""{"linux":{}}""").WithLinuxProbe("MCC-MEM 1024\n");
        Assert.Empty(filled.Memory);
        Assert.Equal(1024L * 1024, filled.TotalMemory);
    }

    [Fact]
    public void What_the_agent_already_said_is_not_replaced()
    {
        var known = Of(Windows.Replace("windows", "linux", StringComparison.Ordinal));
        Assert.Equal(known.Disks, known.WithLinuxProbe("TYPE=\"disk\" SIZE=\"5\" MODEL=\"otro\"").Disks);
    }

    [Fact]
    public void A_phone_reports_its_storage_as_a_number()
    {
        var h = Of("""{"mobile":{},"identifiers":{"storage_devices":[{"Size":111145725952,"Caption":"Onboard Storage","Model":"Onboard Storage"}]}}""");
        Assert.Equal(111145725952, Assert.Single(h.Disks).Bytes);
        Assert.Empty(h.Memory);
    }

    [Fact]
    public void An_inventory_without_any_of_it_is_empty_not_an_error()
    {
        var h = Of("""{"agentvers":{"openssl":"3"}}""");
        Assert.Empty(h.Memory); Assert.Empty(h.Disks); Assert.Empty(h.Volumes); Assert.Empty(h.Graphics);
        Assert.Equal(0, h.TotalMemory);
        Assert.Null(h.Model); Assert.Null(h.Serial);
    }

    [Fact]
    public void Graphics_also_come_from_the_windows_section_or_as_one_name()
    {
        Assert.Equal(new[] { "Intel UHD" }, Of("""{"windows":{"gpu":[{"Name":"Intel UHD"}]}}""").Graphics);
        Assert.Equal(new[] { "AMD" }, Of("""{"identifiers":{"gpu_name":"AMD"}}""").Graphics);
    }
}
