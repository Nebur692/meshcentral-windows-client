using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshCentralClient.App;

public sealed class SavedServer
{
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Accepted fingerprint for a certificate Windows does not trust. / Huella aceptada para un certificado en el que Windows no confía.</summary>
    public string? CertificateSha256 { get; set; }
    /// <summary>Password encrypted with DPAPI for this Windows user only. / Contraseña cifrada con DPAPI solo para este usuario de Windows.</summary>
    public string? ProtectedPassword { get; set; }
    public DateTimeOffset LastUsed { get; set; }
    /// <summary>MeshCentral strategy ("oidc", "saml"…) when this account signs in with single sign-on. / Estrategia cuando la cuenta entra por inicio único.</summary>
    public string? SingleSignOn { get; set; }

    public override string ToString() =>
        (string.IsNullOrEmpty(Username) ? Address : $"{Username} @ {Address}") + (SingleSignOn is null ? "" : $" ({L.T("SsoSaved")})");

    public string? ReadPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception) { return null; } // another user or a reinstalled Windows / otro usuario o un Windows reinstalado
    }

    public void WritePassword(string? password) => ProtectedPassword = string.IsNullOrEmpty(password) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeshCentralClient/servers/v1");
}

/// <summary>%APPDATA%\MeshCentralClient\servers.json</summary>
public sealed class SavedServers
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public SavedServers(string? folder = null)
    {
        var dir = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshCentralClient");
        _file = Path.Combine(dir, "servers.json");
    }

    public List<SavedServer> Items { get; private set; } = new();

    /// <summary>Returns the problem to show, if any; never throws. / Devuelve el problema a enseñar, si lo hay; no lanza nunca.</summary>
    public UserError? Load()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            Items = JsonSerializer.Deserialize<List<SavedServer>>(File.ReadAllText(_file), Json) ?? new();
            Items.RemoveAll(s => string.IsNullOrWhiteSpace(s.Address));
            return null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Items = new();
            try { File.Move(_file, _file + ".broken", overwrite: true); } catch (Exception) { }
            return UserError.Settings(corrupt: true, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Items = new();
            return UserError.Settings(corrupt: false, ex);
        }
    }

    public UserError? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.OrderByDescending(s => s.LastUsed).ToList(), Json));
            File.Move(tmp, _file, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UserError.Settings(corrupt: false, ex);
        }
    }

    public SavedServer Upsert(string address, string username)
    {
        var s = Items.FirstOrDefault(i => string.Equals(i.Address, address, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase));
        if (s is null) { s = new SavedServer { Address = address, Username = username }; Items.Add(s); }
        s.LastUsed = DateTimeOffset.Now;
        return s;
    }

    /// <summary>The pin for an address, from any saved account on it. / La huella de una dirección, de cualquier cuenta guardada en ella.</summary>
    public string? PinFor(string address) => Items.Where(i => string.Equals(i.Address, address, StringComparison.OrdinalIgnoreCase))
        .Select(i => i.CertificateSha256).FirstOrDefault(p => !string.IsNullOrEmpty(p));
}
