namespace SimATC.Core.Atc;

/// <summary>
/// METAR de internet (ADR-100, aprobado por Marco: "haz que se requiera internet para usar METAR, total el clima siempre está
/// en vivo"): la API pública de aviationweather.gov (NOAA), sin clave, formato texto:
/// <c>https://aviationweather.gov/api/data/metar?ids=SCEL,KJFK&amp;format=raw</c> → una línea "METAR SCEL 090200Z …" por
/// aeropuerto (verificado el 2026-11-09). Solo se envían los códigos OACI.
/// </summary>
public sealed class MetarClient(HttpClient http)
{
    public const string BaseUrl = "https://aviationweather.gov/api/data/metar";

    public static Uri UrlFor(IEnumerable<string> icaos) =>
        new($"{BaseUrl}?ids={Uri.EscapeDataString(string.Join(",", icaos.Select(i => i.Trim().ToUpperInvariant()).Distinct()))}&format=raw");

    /// <summary>Los METAR de esos aeropuertos (los que respondieron y se pudieron leer), por código OACI.</summary>
    public async Task<IReadOnlyDictionary<string, Metar>> FetchAsync(IReadOnlyCollection<string> icaos, DateTime nowUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, Metar>(StringComparer.OrdinalIgnoreCase);
        if (icaos.Count == 0)
            return result;
        using var response = await http.GetAsync(UrlFor(icaos), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseAll(body, nowUtc);
    }

    /// <summary>Una línea por METAR; se queda con el más nuevo de cada aeropuerto.</summary>
    public static IReadOnlyDictionary<string, Metar> ParseAll(string body, DateTime nowUtc)
    {
        var result = new Dictionary<string, Metar>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MetarParser.Parse(line, nowUtc) is { } m && (!result.TryGetValue(m.Icao, out var old) || m.TimeUtc > old.TimeUtc))
                result[m.Icao] = m;
        }
        return result;
    }
}
