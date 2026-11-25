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

    // Tipos que se vieron por aerolínea ("BAW") y por aerolínea y par de aeropuertos ("BAW EGLL-KMIA", en orden alfabético:
    // el mismo avión va y vuelve). ADR-168.
    private readonly Dictionary<string, Dictionary<string, int>> _types = new(StringComparer.Ordinal);

    // Vuelos ya contados en esta sesión (se leen cada 30 min y muchos siguen ahí).
    private readonly HashSet<string> _counted = new(StringComparer.Ordinal);

    /// <summary>Tope de claves de tipos guardadas.</summary>
    public const int MaxTypeKeys = 40_000;

    private static string TypesPathFor(string routesPath) =>
        Path.Combine(Path.GetDirectoryName(routesPath) ?? "", Path.GetFileNameWithoutExtension(routesPath) + "-types.json");

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
        ReadInto(path, store._routes);
        ReadInto(TypesPathFor(path), store._types);
        return store;
    }

    private static void ReadInto(string path, Dictionary<string, Dictionary<string, int>> into)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), VatsimJson.Default.DictionaryStringDictionaryStringInt32) is { } d)
            {
                foreach (var (k, v) in d)
                    into[k] = v;
            }
        }
        catch (Exception)
        {
            // Archivo dañado: se empieza de cero.
        }
    }

    public void Save(string path)
    {
        string json, types;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_routes, VatsimJson.Default.DictionaryStringDictionaryStringInt32);
            types = JsonSerializer.Serialize(_types, VatsimJson.Default.DictionaryStringDictionaryStringInt32);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        File.WriteAllText(TypesPathFor(path), types);
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
                    AddType(Str(p, "callsign"), Str(fp, "departure"), Str(fp, "arrival"), Str(fp, "aircraft_short"), Str(fp, "flight_rules"));
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

    [GeneratedRegex(@"^[A-Z]{3}[0-9]")]
    private static partial Regex AirlineCallsign();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{2,3}$")]
    private static partial Regex TypeCode();

    /// <summary>Cuenta el tipo de avión de un plan (ADR-168): solo el código de la aerolínea, nada del piloto.</summary>
    public bool AddType(string? callsign, string? from, string? to, string? type, string? rules)
    {
        if (rules != "I" || callsign is null || type is null || from is not { Length: 4 } || to is not { Length: 4 } || from == to
            || !AirlineCallsign().IsMatch(callsign) || !TypeCode().IsMatch(type))
            return false;
        lock (_lock)
        {
            if (!_counted.Add($"{callsign} {from}-{to}"))
                return false;
            var airline = callsign[..3];
            CountType(airline, type);
            CountType(TypeKey(airline, from, to), type);
        }
        return true;
    }

    private void CountType(string key, string type)
    {
        if (!_types.TryGetValue(key, out var t))
        {
            if (_types.Count >= MaxTypeKeys)
                return;
            _types[key] = t = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        t[type] = t.GetValueOrDefault(type) + 1;
    }

    private static string TypeKey(string airline, string from, string to) =>
        string.CompareOrdinal(from, to) <= 0 ? $"{airline} {from}-{to}" : $"{airline} {to}-{from}";

    /// <summary>Lo que suma que ese tipo se haya visto mucho en la aerolínea (el más visto suma todo).</summary>
    public const double AirlineTypeWeight = 3;

    /// <summary>Lo que suma cada vez que se vio en esa ruta, hasta <see cref="MaxRouteSeen"/> veces.</summary>
    public const double RouteTypeWeight = 6;
    public const int MaxRouteSeen = 4;

    /// <summary>
    /// Cuánto pesa un tipo de una aerolínea entre los que puede volar (1 = sin datos): más si en VATSIM vuela con él, y mucho
    /// más si se vio en esa ruta. Es una preferencia: los pilotos virtuales no siempre usan el avión real.
    /// </summary>
    public double TypeWeight(string airline, string from, string to, string type)
    {
        lock (_lock)
        {
            var w = 1.0;
            if (_types.TryGetValue(airline, out var all) && all.TryGetValue(type, out var n))
                w += AirlineTypeWeight * n / all.Values.Max();
            if (_types.TryGetValue(TypeKey(airline, from, to), out var pair) && pair.TryGetValue(type, out var seen))
                w += RouteTypeWeight * Math.Min(MaxRouteSeen, seen);
            return w;
        }
    }

    /// <summary>Lo mismo para un avión estacionado, que puede salir a cualquiera de esos destinos.</summary>
    public double TypeWeight(string airline, string from, IReadOnlyList<string> destinations, string type)
    {
        lock (_lock)
        {
            var w = 1.0;
            if (!_types.TryGetValue(airline, out var all))
                return w;
            if (all.TryGetValue(type, out var n))
                w += AirlineTypeWeight * n / all.Values.Max();
            var seen = 0;
            foreach (var to in destinations)
            {
                if (_types.TryGetValue(TypeKey(airline, from, to), out var pair) && pair.TryGetValue(type, out var s))
                    seen += Math.Min(MaxRouteSeen, s);
            }
            return w + RouteTypeWeight * Math.Sqrt(seen);
        }
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
