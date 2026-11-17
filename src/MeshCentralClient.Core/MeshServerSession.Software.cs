using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>One program installed on a device. / Un programa instalado en un equipo.</summary>
public sealed record InstalledProgram(string Name, string? Version, string? Publisher, string? Date);

/// <summary>
/// The programs installed on a device, as the website's "Software" tab (meshuser.js software, type installedapps): the
/// agent reads them (Windows' uninstall keys, dpkg or rpm on Linux, /Applications on macOS) and answers with a JSON list.
/// Asked without a responseid: MeshCentral 1.3.1 answers any responseid with an early "Denied" before routing the request.
/// Los programas instalados en un equipo, como la pestaña «Software» de la web: los lee el agente y contesta una lista JSON.
/// Se pide sin responseid: la 1.3.1 contesta a cualquiera un «Denied» adelantado antes de pasar la petición.
/// </summary>
public sealed partial class MeshServerSession
{
    public bool CanSeeSoftware(Device device)
    {
        var r = RightsOn(device);
        return device.HasAgent && (r == (long)MeshRights.Full || (r != 0 && !Has(r, MeshRights.NoSoftware)));
    }

    public async Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(Device device, CancellationToken ct = default)
    {
        if (!CanSeeSoftware(device)) throw Denied("ficha/abrir-detalle/no-autorizado", "see its programs");
        var r = await RequestAsync(new JsonObject { ["action"] = "software", ["nodeid"] = device.Id, ["type"] = "installedapps" },
            o => o.Str("action") == "software" && o["value"] is not null && (o.Str("nodeid") is null || o.Str("nodeid") == device.Id),
            "ficha/abrir-detalle/tiempo-agotado", "ficha/abrir-detalle/conexion-cortada", ct, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        JsonNode? value;
        try { value = r["value"] is JsonValue v && v.TryGetValue<string>(out var text) ? JsonNode.Parse(text) : r["value"]; }
        catch (System.Text.Json.JsonException) { value = null; }
        if (value is JsonObject err && err.Str("error") is { } e)
            throw new MeshClientException(MeshFailure.Rejected, "ficha/abrir-detalle/rechazo-remoto", $"The agent could not list the programs: {e}.") { ServerMessage = e };
        if (value is not JsonArray list)
            throw new MeshClientException(MeshFailure.UnexpectedResponse, "ficha/abrir-detalle/rechazo-remoto", "The agent's answer was not a list of programs.");
        return list.OfType<JsonObject>()
            .Select(p => new InstalledProgram(p.Str("name") ?? "", Blank(p.Str("version")), Blank(p.Str("publisher")), Blank(p.Str("date") ?? p.Str("installdate"))))
            .Where(p => p.Name.Length > 0).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
