using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>One device in the list, updated live. / Un equipo de la lista, que se actualiza en vivo.</summary>
public sealed class DeviceRow : INotifyPropertyChanged
{
    private Device _device;
    private string _groupName;

    public DeviceRow(Device device, string groupName) { _device = device; _groupName = groupName; }

    public Device Device
    {
        get => _device;
        set { _device = value; Changed(""); }
    }

    public string Id => _device.Id;
    public string Name => _device.Name;
    public string GroupName { get => _groupName; set { _groupName = value; Changed(); } }
    public string Os => _device.OsDescription ?? "";
    public string Address => _device.Address ?? "";
    public string Tags => string.Join(", ", _device.Tags);
    public bool IsOnline => _device.IsOnline;
    public bool CanOpenTerminal => _device.CanOpenTerminal;
    public bool CanOpenFiles => _device.CanOpenFiles;
    public bool IsWindows => _device.IsWindows;
    public string Status => L.T(_device.IsOnline ? "Online" : "Offline");

    public bool Matches(string filter) => filter.Length == 0
        || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Os.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Address.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Tags.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || GroupName.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    // What screen readers and UI Automation read for the row. / Lo que leen los lectores de pantalla y UI Automation de la fila.
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
