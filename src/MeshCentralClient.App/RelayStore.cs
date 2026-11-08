using System.IO;
using System.Text.Json;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>A port mapping the person wants back at every sign-in. / Una redirección que la persona quiere en cada entrada.</summary>
public sealed class SavedMapping
{
    public string Server { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? RemoteHost { get; set; }
    public int RemotePort { get; set; }
    public int LocalPort { get; set; }

    public PortTarget Target => new(NodeId, DeviceName, RemotePort, RemoteHost, LocalPort);
}

/// <summary>How the person last signed in to an SSH server through a device; never the password.
/// Cómo entró la persona la última vez en un servidor SSH por un equipo; nunca la contraseña.</summary>
public sealed class SshProfile
{
    public string Server { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string? Host { get; set; }
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? KeyPath { get; set; }
}

/// <summary>An SSH server identity the person trusted, as OpenSSH's known_hosts. / Una identidad SSH en la que confió la persona, como el known_hosts de OpenSSH.</summary>
public sealed class KnownSshHost
{
    public string Server { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string? Host { get; set; }
    public int Port { get; set; }
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>What the trust check found. / Lo que encontró la comprobación de confianza.</summary>
public enum HostKeyVerdict { Known, New, Changed }

/// <summary>%APPDATA%\MeshCentralClient\relays.json: remembered mappings, SSH profiles and trusted SSH identities, per server.
/// Redirecciones recordadas, perfiles SSH e identidades SSH de confianza, por servidor.</summary>
public sealed class RelayStore
{
    private sealed class Data
    {
        public List<SavedMapping> Mappings { get; set; } = new();
        public List<SshProfile> Ssh { get; set; } = new();
        public List<KnownSshHost> KnownHosts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private Data _data = new();

    public RelayStore(string? folder = null) => _file = Path.Combine(folder ?? AppPaths.Roaming, "relays.json");

    public static RelayStore Shared { get; } = Load(new RelayStore());

    private static RelayStore Load(RelayStore s)
    {
        try { if (File.Exists(s._file)) s._data = JsonSerializer.Deserialize<Data>(File.ReadAllText(s._file), Json) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { s._data = new(); }
        return s;
    }

    /// <summary>Reads it again from disk (tests, and a second app instance). / Lo vuelve a leer del disco.</summary>
    public RelayStore Reload() => Load(this);

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SavedMapping> MappingsOf(string server) => _data.Mappings.Where(m => Same(m.Server, server)).ToList();

    public void Remember(string server, PortTarget t, int localPort)
    {
        Forget(server, t);
        _data.Mappings.Add(new SavedMapping { Server = server, NodeId = t.NodeId, DeviceName = t.DeviceName, RemoteHost = t.RemoteHost, RemotePort = t.RemotePort, LocalPort = localPort });
        Save();
    }

    public void Forget(string server, PortTarget t)
    {
        if (_data.Mappings.RemoveAll(m => Same(m.Server, server) && m.NodeId == t.NodeId && Same(m.RemoteHost, t.RemoteHost) && m.RemotePort == t.RemotePort) > 0) Save();
    }

    public SshProfile? SshOf(string server, string nodeId) =>
        _data.Ssh.LastOrDefault(p => Same(p.Server, server) && p.NodeId == nodeId);

    public void RememberSsh(SshProfile p)
    {
        _data.Ssh.RemoveAll(x => Same(x.Server, p.Server) && x.NodeId == p.NodeId);
        _data.Ssh.Add(p);
        Save();
    }

    public HostKeyVerdict Check(string server, string nodeId, string? host, int port, SshHostKey key)
    {
        var known = _data.KnownHosts.Where(k => Same(k.Server, server) && k.NodeId == nodeId && Same(k.Host, host) && k.Port == port).ToList();
        if (known.Count == 0) return HostKeyVerdict.New;
        return known.Any(k => k.Algorithm == key.Algorithm && k.Fingerprint == key.Fingerprint) ? HostKeyVerdict.Known : HostKeyVerdict.Changed;
    }

    /// <summary>Trusts <paramref name="key"/> for that server, replacing what was trusted before. / Confía en esa identidad, sustituyendo la anterior.</summary>
    public void Trust(string server, string nodeId, string? host, int port, SshHostKey key)
    {
        _data.KnownHosts.RemoveAll(k => Same(k.Server, server) && k.NodeId == nodeId && Same(k.Host, host) && k.Port == port);
        _data.KnownHosts.Add(new KnownSshHost { Server = server, NodeId = nodeId, Host = host?.Trim() is { Length: > 0 } h ? h : null, Port = port, Algorithm = key.Algorithm, Fingerprint = key.Fingerprint });
        Save();
    }
}
