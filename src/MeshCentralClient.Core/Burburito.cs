using System.Reflection;
using System.Text.Json;

namespace MeshCentralClient.Core;

/// <summary>
/// Reference codes for errors ("burburito" codes, PPP-MMM-AAA-CCC) and their light-hearted footer, read from the
/// embedded burburito.lock.json, never written by hand. Never throws: if anything is missing it answers
/// 000-000-000-000, because a failure inside the error reporter would leave the user with no message at all.
/// Códigos de referencia de los errores (códigos «burburito», PPP-MMM-AAA-CCC) y su pie con humor, leídos del
/// burburito.lock.json incrustado, nunca escritos a mano. No lanza nunca: si falta algo contesta 000-000-000-000,
/// porque un fallo dentro del que avisa de los errores dejaría a la persona sin ningún mensaje.
/// </summary>
public static class Burburito
{
    public const string Unknown = "000-000-000-000";
    private static readonly Lazy<Catalogue> Data = new(Load);

    public static string Code(string key) => Data.Value.Codes.TryGetValue(key, out var c) ? c.Code : Unknown;

    public static bool IsKnown(string key) => Data.Value.Codes.ContainsKey(key);

    /// <summary>"error" or "aviso" (warning). / «error» o «aviso».</summary>
    public static string Severity(string key) => Data.Value.Codes.TryGetValue(key, out var c) ? c.Severity : "error";

    /// <summary>
    /// The footer for a message: a random phrase for its severity plus the code. The code is deterministic;
    /// only the phrase changes.
    /// El pie de un mensaje: una frase al azar de su severidad y el código. El código es fijo; solo cambia la frase.
    /// </summary>
    public static string Footer(string key, bool spanish)
    {
        var code = Code(key);
        var label = spanish ? "Código burburito" : "Burburito code";
        if (!Data.Value.Phrases.TryGetValue(Severity(key), out var list) || list.Count == 0)
            Data.Value.Phrases.TryGetValue("error", out list);
        if (list is null || list.Count == 0) return $"{label}: {code}";
        var p = list[Random.Shared.Next(list.Count)];
        return $"{(spanish ? p.Es : p.En)} {label}: {code}";
    }

    internal static IReadOnlyCollection<string> Keys => Data.Value.Codes.Keys.ToList();

    internal static Catalogue Parse(string json)
    {
        var codes = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var phrases = new Dictionary<string, List<Phrase>>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("codigos", out var cs))
                foreach (var p in cs.EnumerateObject())
                    if (p.Value.TryGetProperty("codigo", out var c) && c.GetString() is { } s)
                        codes[p.Name] = new Entry(s, p.Value.TryGetProperty("severidad", out var sev) ? sev.GetString() ?? "error" : "error");
            if (doc.RootElement.TryGetProperty("frases", out var fs) && fs.ValueKind == JsonValueKind.Object)
                foreach (var group in fs.EnumerateObject())
                    phrases[group.Name] = group.Value.EnumerateArray()
                        .Select(f => new Phrase(f.TryGetProperty("es", out var es) ? es.GetString() ?? "" : "", f.TryGetProperty("en", out var en) ? en.GetString() ?? "" : ""))
                        .Where(f => f.Es.Length > 0 && f.En.Length > 0).ToList();
        }
        catch (Exception) { /* degrade to Unknown / se degrada a Unknown */ }
        return new Catalogue(codes, phrases);
    }

    private static Catalogue Load()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("burburito.lock.json");
            if (s is null) return Parse("{}");
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch (Exception) { return Parse("{}"); }
    }

    internal sealed record Entry(string Code, string Severity);
    internal sealed record Phrase(string Es, string En);
    internal sealed record Catalogue(IReadOnlyDictionary<string, Entry> Codes, IReadOnlyDictionary<string, List<Phrase>> Phrases);
}
