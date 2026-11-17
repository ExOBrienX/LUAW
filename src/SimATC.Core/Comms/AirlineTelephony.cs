using System.Text;
using System.Text.Json;
using SimATC.Core.Atc;

namespace SimATC.Core.Comms;

/// <summary>Una aerolínea de la base: su telefonía ("LAN CHILE") y si se confirmó en una segunda fuente.</summary>
public sealed record AirlineEntry(string Telephony, string Name, bool Verified);

/// <summary>
/// Telefonía de las aerolíneas (§8.2, V-11, hito 4e-3): del designador OACI ("LAN") a la palabra que se dice por radio
/// ("LAN CHILE"). Se carga de <c>data/airlines.json</c>, generado de Wikipedia y OpenFlights (ADR-042). El chat muestra
/// el indicativo escrito ("LAN2045"); la forma hablada ("LAN CHILE two zero fower fife") es para las sugerencias y la voz.
/// </summary>
public sealed class AirlineTelephony
{
    private readonly Dictionary<string, AirlineEntry> _airlines;

    public AirlineTelephony(IReadOnlyDictionary<string, AirlineEntry> airlines) =>
        _airlines = new Dictionary<string, AirlineEntry>(airlines, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sin base: los indicativos se dicen letra por letra.</summary>
    public static AirlineTelephony Empty { get; } = new(new Dictionary<string, AirlineEntry>());

    public int Count => _airlines.Count;

    private HashSet<string>? _words;

    /// <summary>Si <paramref name="word"/> es una palabra de alguna telefonía ("LAN", "CHILE", "SPEEDBIRD").</summary>
    public bool IsTelephonyWord(string word)
    {
        _words ??= [.. _airlines.Values.SelectMany(a => a.Telephony.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
        return _words.Contains(word);
    }

    private sealed record FileFormat(string Source, Dictionary<string, AirlineEntry> Airlines);

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "airlines.json");

    public static AirlineTelephony Load(string path)
    {
        var f = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Base de aerolíneas vacía: {path}");
        return new AirlineTelephony(f.Airlines);
    }

    /// <summary>La aerolínea de ese designador OACI, o null.</summary>
    public AirlineEntry? Find(string icao) => _airlines.GetValueOrDefault(icao);

    /// <summary>"LAN2045" → ("LAN", "2045"); sin la forma letras + número, null.</summary>
    public static (string Airline, string Number)? Split(string callsign)
    {
        var cs = callsign.Trim().ToUpperInvariant();
        var i = 0;
        while (i < cs.Length && char.IsAsciiLetter(cs[i]))
            i++;
        return i == 3 && i < cs.Length && char.IsAsciiDigit(cs[i]) ? (cs[..3], cs[3..]) : null;
    }

    /// <summary>
    /// Forma hablada del indicativo: "LAN2045" → "LAN CHILE two zero fower fife" (dígitos uno por uno, §8.2; una letra
    /// final con el alfabeto OACI). Si la aerolínea no está en la base, sus letras con el alfabeto ("Hotel Delta Lima").
    /// </summary>
    public string Spoken(string callsign, AtcLanguage lang = AtcLanguage.English)
    {
        if (Split(callsign) is not { } p)
            return callsign;
        var sb = new StringBuilder();
        if (Find(p.Airline) is { } a)
            sb.Append(a.Telephony);
        else
            sb.Append(string.Join(' ', p.Airline.Select(AtisBuilder.Phonetic)));
        foreach (var c in p.Number)
            sb.Append(' ').Append(char.IsAsciiDigit(c) ? SpokenForm.Digit(c, lang) : AtisBuilder.Phonetic(c));
        return sb.ToString();
    }

    /// <summary>
    /// Formas normalizadas (como <see cref="InputNormalizer"/>) con las que el piloto puede decir su indicativo:
    /// "lan2045", "lan 2045" y con la telefonía, "lan chile 2045" y "lanchile 2045".
    /// </summary>
    public IReadOnlyList<string> Forms(string callsign)
    {
        var cs = callsign.Trim().ToLowerInvariant();
        if (Split(callsign) is not { } p)
            return cs.Length > 0 ? [cs] : [];
        var number = p.Number.ToLowerInvariant();
        var forms = new List<string> { cs, $"{p.Airline.ToLowerInvariant()} {number}" };
        if (Find(p.Airline) is { } a)
        {
            var tel = InputNormalizer.Normalize(a.Telephony);
            if (tel.Length > 0)
            {
                forms.Add($"{tel} {number}");
                forms.Add($"{tel.Replace(" ", "", StringComparison.Ordinal)} {number}");
            }
        }
        return [.. forms.Distinct()];
    }

}
