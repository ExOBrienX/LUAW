using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimATC.Core.Comms;

/// <summary>Una plantilla de fraseología: su referencia y sus variantes equivalentes.</summary>
public sealed record PhraseTemplate(string Ref, IReadOnlyList<string> Variants);

/// <summary>
/// Las frases del ATC y del piloto (§8, RF-35b), cargadas de <c>data/phraseology/{idioma}.json</c>: ninguna frase se
/// arma a mano en el código (§8.1). Cada plantilla tiene variantes equivalentes; se elige una al azar para no sonar
/// repetitivo. Los datos (pista, nivel, frecuencia) vienen en <c>fields</c> y siempre son exactos.
/// </summary>
public sealed partial class Phraseology
{
    private readonly Dictionary<string, PhraseTemplate> _templates;
    private readonly Random _random;

    public Phraseology(IReadOnlyDictionary<string, PhraseTemplate> templates, Random? random = null)
    {
        _templates = new Dictionary<string, PhraseTemplate>(templates);
        _random = random ?? new Random();
    }

    public IReadOnlyDictionary<string, PhraseTemplate> Templates => _templates;

    private sealed record FileFormat(string Language, string Source, Dictionary<string, PhraseTemplate> Templates);

    public static string PathFor(string dataDir, string language = "icao-en") => Path.Combine(dataDir, "phraseology", $"{language}.json");

    /// <summary>El archivo del idioma del ATC (§8.6).</summary>
    public static string PathFor(string dataDir, AtcLanguage language) => PathFor(dataDir, AtcWords.PhraseFile(language));

    public static Phraseology Load(string path, Random? random = null) => Load(path, null, random);

    /// <summary>
    /// El archivo base y, encima, una capa (<paramref name="overlayPath"/>, p. ej. <c>faa-en</c>) que reemplaza solo las
    /// frases que trae; las demás quedan como en la base (ADR-099).
    /// </summary>
    public static Phraseology Load(string path, string? overlayPath, Random? random = null)
    {
        var templates = new Dictionary<string, PhraseTemplate>(Read(path).Templates);
        if (overlayPath is not null)
        {
            foreach (var (id, t) in Read(overlayPath).Templates)
                templates[id] = t;
        }
        return new Phraseology(templates, random);
    }

    private static FileFormat Read(string path) =>
        JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"Fraseología vacía: {path}");

    /// <summary>
    /// Arma la frase <paramref name="id"/> con una variante al azar. Un campo que falta es un error de programación
    /// (se lanza): nunca sale una frase con "{campo}" sin reemplazar.
    /// </summary>
    public string Say(string id, IReadOnlyDictionary<string, string> fields)
    {
        if (!_templates.TryGetValue(id, out var t))
            throw new KeyNotFoundException($"No hay plantilla \"{id}\" en la fraseología.");
        var variant = t.Variants[_random.Next(t.Variants.Count)];
        return Fill(variant, fields, id);
    }

    /// <summary>La primera variante (la de referencia): para sugerencias al piloto y tests.</summary>
    public string Canonical(string id, IReadOnlyDictionary<string, string> fields) => Fill(_templates[id].Variants[0], fields, id);

    private static string Fill(string variant, IReadOnlyDictionary<string, string> fields, string id) =>
        FieldRe().Replace(variant, m => fields.TryGetValue(m.Groups[1].Value, out var v)
            ? m.Groups[1].Value is "taxiway" or "route" ? SpokenTaxiways(v) : v
            : Derived(m.Groups[1].Value, fields)
              ?? throw new KeyNotFoundException($"La plantilla \"{id}\" necesita el campo {{{m.Groups[1].Value}}}."));

    /// <summary>
    /// Un campo que se puede sacar de otro: la capa FAA (ADR-099) dice el altímetro en pulgadas donde OACI dice el QNH en
    /// hPa. Quien da el QNH no tiene que acordarse del altímetro (crash del 2026-11-15 en la radio de los IA).
    /// </summary>
    private static string? Derived(string field, IReadOnlyDictionary<string, string> fields) =>
        field == "altimeter" && fields.TryGetValue("qnh", out var qnh)
        && double.TryParse(qnh, NumberStyles.Float, CultureInfo.InvariantCulture, out var hpa)
            ? AtcWords.AltimeterInHg(hpa)
            : null;

    /// <summary>
    /// Calles de rodaje con el alfabeto OACI, como se dicen por radio (CAP 413 cap. 4: "taxiway Alpha"; prueba de Marco:
    /// "abandone por A" en vez de "Alfa"): "A" → "Alfa", "B2" → "Bravo 2", "P, H" → "Papa, Hotel".
    /// </summary>
    public static string SpokenTaxiways(string text) =>
        TaxiwayRe().Replace(text, m =>
        {
            var letters = string.Join(' ', m.Groups[1].Value.Select(c => Atc.AtisBuilder.Phonetic(c)));
            return m.Groups[2].Value.Length > 0 ? $"{letters} {m.Groups[2].Value}" : letters;
        });

    [GeneratedRegex(@"\b([A-Z]{1,2})(\d{0,2})\b")]
    private static partial Regex TaxiwayRe();

    /// <summary>Los campos que usa una plantilla (todas sus variantes).</summary>
    public IReadOnlySet<string> FieldsOf(string id) =>
        _templates[id].Variants.SelectMany(v => FieldRe().Matches(v).Select(m => m.Groups[1].Value)).ToHashSet();

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex FieldRe();
}
