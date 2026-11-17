using System.Globalization;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>What a sign-in was (the server's msgid). / Qué fue una entrada.</summary>
public enum LoginKind { Valid, Token, WrongSecondFactor, Locked, WrongPassword, Other }

/// <summary>One sign-in of this account, with where it came from (address, browser, system). / Una entrada de esta cuenta.</summary>
public sealed record LoginEntry(DateTimeOffset Time, LoginKind Kind, string? TokenName, IReadOnlyList<string> Details);

/// <summary>
/// This account's previous sign-ins, good and failed, as the website's "Previous logins" (meshuser.js previousLogins):
/// 107 valid (or with a login token), 108 wrong second factor, 109 locked, 110 wrong password.
/// Las entradas anteriores de esta cuenta, buenas y fallidas, como «Inicios de sesión anteriores» de la web.
/// </summary>
public sealed partial class MeshServerSession
{
    public async Task<IReadOnlyList<LoginEntry>> GetPreviousLoginsAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "previousLogins" }, o => o.Str("action") == "previousLogins" && o["events"] is JsonArray && o.Str("userid") is null,
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        return ((JsonArray)r["events"]!).OfType<JsonObject>().Select(e =>
        {
            var tn = e.Str("tn");
            var kind = e.Long("m") switch { 107 => tn is null ? LoginKind.Valid : LoginKind.Token, 108 => LoginKind.WrongSecondFactor, 109 => LoginKind.Locked, 110 => LoginKind.WrongPassword, _ => LoginKind.Other };
            var details = (e["a"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
            return new LoginEntry(TimeOf(e["t"]), kind, tn, details);
        }).OrderByDescending(x => x.Time).ToList();
    }

    /// <summary>The server sends dates as milliseconds or as text. / El servidor manda las fechas en milisegundos o en texto.</summary>
    private static DateTimeOffset TimeOf(JsonNode? t) =>
        t is JsonValue v && v.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
        : t is JsonValue s && s.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d
        : DateTimeOffset.MinValue;
}
