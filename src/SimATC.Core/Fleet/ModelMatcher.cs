namespace SimATC.Core.Fleet;

/// <summary>Cómo se encontró el modelo (para el log).</summary>
public enum MatchKind
{
    /// <summary>Livery de la aerolínea pedida, del tipo pedido.</summary>
    AirlineLivery,
    /// <summary>Livery genérica del tipo pedido (o de su equivalente según las reglas de FSLTL).</summary>
    GenericSameType,
    /// <summary>Tipo parecido de la misma familia (mismo prefijo de designador).</summary>
    SimilarType,
    /// <summary>Último recurso: cualquier modelo de la misma categoría de estela.</summary>
    AnySameCategory,
}

public sealed record ModelMatch(AircraftModel Model, MatchKind Kind);

/// <summary>
/// Elige el modelo y la livery para una aerolínea y un tipo OACI (RF-13, RF-14). Solo usa modelos que el
/// simulador puede crear. Entre varios candidatos la elección es estable para la misma <c>variationKey</c>.
/// </summary>
public sealed class ModelMatcher
{
    private readonly Dictionary<string, AircraftModel> _byTitle;
    private readonly Dictionary<(string Airline, string Type), List<AircraftModel>> _byAirlineType = [];
    private readonly Dictionary<(string Airline, string Type), List<string>> _rules = [];
    private readonly Dictionary<string, List<AircraftModel>> _genericByType = new(StringComparer.Ordinal);
    private readonly List<AircraftModel> _all;
    private readonly Dictionary<string, double> _maxSpanByType = new(StringComparer.Ordinal);

    public ModelMatcher(IEnumerable<AircraftModel> spawnableModels, IEnumerable<FsltlRule> rules)
    {
        _all = spawnableModels.ToList();
        _byTitle = new Dictionary<string, AircraftModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _all)
        {
            _byTitle.TryAdd(m.Title, m);
            if (m.WingspanM > _maxSpanByType.GetValueOrDefault(m.IcaoType))
                _maxSpanByType[m.IcaoType] = m.WingspanM;
            if (m.IsGeneric)
                Add(_genericByType, m.IcaoType, m);
            else
                Add(_byAirlineType, (m.IcaoAirline, m.IcaoType), m);
        }
        foreach (var r in rules)
        {
            var key = (r.Airline, r.TypeCode);
            if (!_rules.TryGetValue(key, out var list))
                _rules[key] = list = [];
            list.AddRange(r.Models);
        }
    }

    public int ModelCount => _all.Count;

    /// <summary>Todos los títulos de aviones que el simulador puede crear (incluidos los suyos), para la decoración (ADR-109).</summary>
    public IReadOnlySet<string> SimTitles { get; set; } = new HashSet<string>();

    /// <summary>Modelos de carga con livery de aerolínea (para las plataformas de carga sin aerolíneas de carga en las rutas).</summary>
    public IReadOnlyList<AircraftModel> CargoModels() =>
        [.. _all.Where(m => !m.IsGeneric && m.IcaoAirline.Length > 0
                            && (m.ParkingTypes.Contains("CARGO") || m.Title.Contains("Cargo", StringComparison.OrdinalIgnoreCase)
                                || m.Title.Contains("Freight", StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// Envergadura para asignar puestos: el mayor wing_span instalado para el tipo del modelo. Los cfg de AIG traen
    /// valores reducidos (A320neo: 22 m) y MSFS usa ese campo para sus propios puestos (ADR-016); 0 si no hay dato.
    /// </summary>
    public double WingspanFor(AircraftModel model) =>
        Math.Max(model.WingspanM, _maxSpanByType.GetValueOrDefault(model.IcaoType));

    /// <summary>Tipos con livery instalada de una aerolínea (aproximación de su flota cuando no hay otra fuente).</summary>
    public IReadOnlyList<string> TypesFor(string airline)
    {
        airline = airline.Trim().ToUpperInvariant();
        return _byAirlineType.Keys.Where(k => k.Airline == airline).Select(k => k.Type).Distinct().Order().ToList();
    }

    /// <param name="airline">Código OACI de la aerolínea (LAN, SKU…).</param>
    /// <param name="type">Tipo OACI (A320, B788…).</param>
    /// <param name="variationKey">Clave estable para elegir entre varias liveries (matrícula o vuelo).</param>
    /// <returns>null solo si no hay ningún modelo disponible.</returns>
    public ModelMatch? Match(string airline, string type, string variationKey)
    {
        airline = airline.Trim().ToUpperInvariant();
        type = type.Trim().ToUpperInvariant();

        // 1. Livery de la aerolínea: modelos con esa aerolínea y tipo, más los que nombren sus reglas de FSLTL.
        var airlineCandidates = new List<AircraftModel>();
        if (_byAirlineType.TryGetValue((airline, type), out var direct))
            airlineCandidates.AddRange(direct);
        AddRuleModels(airline, type, airlineCandidates, requireAirline: airline);
        if (Pick(airlineCandidates, variationKey) is { } a)
            return new ModelMatch(a, MatchKind.AirlineLivery);

        // 2. Genérica del mismo tipo, o la que indique la regla genérica de FSLTL para ese tipo.
        var genericCandidates = new List<AircraftModel>();
        if (_genericByType.TryGetValue(type, out var generic))
            genericCandidates.AddRange(generic);
        if (genericCandidates.Count == 0)
            AddRuleModels("", type, genericCandidates, requireAirline: null);
        if (Pick(genericCandidates, variationKey) is { } g)
            return new ModelMatch(g, MatchKind.GenericSameType);

        // 3. Tipo parecido: genéricos con el mismo prefijo de designador (A32x, B73x, E19x…).
        if (type.Length >= 3)
        {
            var prefix = type[..3];
            var similar = _genericByType.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(kv => kv.Value).ToList();
            if (Pick(similar, variationKey) is { } s)
                return new ModelMatch(s, MatchKind.SimilarType);
        }

        // 4. Último recurso: un genérico de categoría media (o cualquier modelo).
        var fallback = _all.Where(m => m.IsGeneric && m.Wake == WakeCategory.Medium).ToList();
        if (fallback.Count == 0)
            fallback = _all;
        return Pick(fallback, variationKey) is { } f ? new ModelMatch(f, MatchKind.AnySameCategory) : null;
    }

    private void AddRuleModels(string airline, string type, List<AircraftModel> into, string? requireAirline)
    {
        if (!_rules.TryGetValue((airline, type), out var titles))
            return;
        foreach (var title in titles)
        {
            if (_byTitle.TryGetValue(title, out var m) && !into.Contains(m) &&
                (requireAirline is null || m.IcaoAirline.Length == 0 || m.IcaoAirline == requireAirline))
                into.Add(m);
        }
    }

    /// <summary>Elección estable: mismo resultado para la misma clave y los mismos candidatos.</summary>
    private static AircraftModel? Pick(List<AircraftModel> candidates, string variationKey)
    {
        if (candidates.Count == 0)
            return null;
        candidates.Sort((x, y) => string.CompareOrdinal(x.Title, y.Title));
        return candidates[(int)(StableHash(variationKey) % (uint)candidates.Count)];
    }

    /// <summary>FNV-1a de 32 bits: estable entre ejecuciones (string.GetHashCode no lo es).</summary>
    private static uint StableHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    private static void Add<TKey>(Dictionary<TKey, List<AircraftModel>> dict, TKey key, AircraftModel m) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        list.Add(m);
    }
}
