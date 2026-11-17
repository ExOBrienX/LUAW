using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using SimATC.Core.Text;

namespace SimATC.Core.World;

/// <summary>
/// Rutas preferidas por par de aeropuertos sacadas de los planes de vuelo públicos de VATSIM (pilotos virtuales, casi
/// siempre con rutas reales de SimBrief). Uso no comercial (términos de VATSIM, ADR-061). Se guarda en un archivo local que
/// crece en cada sesión: con el tiempo cubre rutas de todo el mundo. Sin internet, se usa lo guardado.
/// </summary>
public sealed partial class VatsimRouteStore
{
    public const string FeedUrl = "https://data.vatsim.net/v3/vatsim-data.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // "SCIE-SCEL" → ruta → veces vista.
    private readonly Dictionary<string, Dictionary<string, int>> _routes = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public static string PathFor(string dir) => Path.Combine(dir, "vatsim-routes.json");

    public int PairCount
    {
        get
        {
            lock (_lock)
                return _routes.Count;
        }
    }

    public static VatsimRouteStore Load(string path)
    {
        var store = new VatsimRouteStore();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), VatsimJson.Default.DictionaryStringDictionaryStringInt32) is { } d)
            {
                foreach (var (k, v) in d)
                    store._routes[k] = v;
            }
        }
        catch (Exception)
        {
            // Archivo dañado: se empieza de cero.
        }
        return store;
    }

    public void Save(string path)
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_routes, VatsimJson.Default.DictionaryStringDictionaryStringInt32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Lee los planes de vuelo IFR que hay ahora en VATSIM (pilotos y planes presentados). Cuántos agregó.</summary>
    public async Task<int> RefreshAsync(Action<string>? log, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(FeedUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var added = 0;
            foreach (var list in new[] { "pilots", "prefiles" })
            {
                if (!doc.RootElement.TryGetProperty(list, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var p in arr.EnumerateArray())
                {
                    if (!p.TryGetProperty("flight_plan", out var fp) || fp.ValueKind != JsonValueKind.Object)
                        continue;
                    if (Add(Str(fp, "departure"), Str(fp, "arrival"), Str(fp, "route"), Str(fp, "flight_rules")))
                        added++;
                }
            }
            log?.Invoke(Tr.F("VATSIM: {0} planes de vuelo leídos ({1} pares de aeropuertos guardados).", added, PairCount));
            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke(Tr.F("VATSIM no disponible ({0}); se usan las rutas guardadas.", ex.GetBaseException().Message));
            return 0;
        }
    }

    public bool Add(string? from, string? to, string? route, string? rules)
    {
        if (rules != "I" || from is not { Length: 4 } || to is not { Length: 4 } || from == to || string.IsNullOrWhiteSpace(route))
            return false;
        var clean = string.Join(' ', route.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length < 3 || clean.Length > 400)
            return false;
        lock (_lock)
        {
            var key = $"{from}-{to}";
            if (!_routes.TryGetValue(key, out var routes))
                _routes[key] = routes = new Dictionary<string, int>(StringComparer.Ordinal);
            routes[clean] = routes.GetValueOrDefault(clean) + 1;
        }
        return true;
    }

    /// <summary>Las rutas guardadas de un par, de la más vista a la menos.</summary>
    public IReadOnlyList<string> RoutesFor(string from, string to)
    {
        lock (_lock)
            return _routes.TryGetValue($"{from}-{to}", out var r) ? r.OrderByDescending(x => x.Value).Select(x => x.Key).ToList() : [];
    }

    [GeneratedRegex(@"^[A-Z0-9]{2,7}$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^\d{2,4}[NS]\d{3,5}[EW]$|^\d{2}[NS]\d{3}[EW]$")]
    private static partial Regex LatLon();

    /// <summary>
    /// Fijos y aerovías de una ruta ("SOST2J UT105 UPUSI DCT EROLO"): sin DCT, sin cambios de velocidad o nivel ("/N0450F350")
    /// y sin coordenadas sueltas.
    /// </summary>
    public static List<string> Tokens(string route)
    {
        var list = new List<string>();
        foreach (var raw in route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Split('/')[0];
            if (t == "DCT" || !Token().IsMatch(t) || LatLon().IsMatch(t))
                continue;
            list.Add(t);
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
internal sealed partial class VatsimJson : JsonSerializerContext;
