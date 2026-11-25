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
public sealed partial class ModelMatcher
{
    private readonly Dictionary<string, AircraftModel> _byTitle;
    private readonly Dictionary<(string Airline, string Type), List<AircraftModel>> _byAirlineType = [];
    // Los de relleno (ver IsPlaceholder): solo para aerolíneas que no tienen otra cosa.
    private readonly Dictionary<(string Airline, string Type), List<AircraftModel>> _placeholders = [];
    private readonly HashSet<string> _withLivery = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Airline, string Type), List<string>> _rules = [];
    private readonly Dictionary<string, List<AircraftModel>> _genericByType = new(StringComparer.Ordinal);
    private readonly List<AircraftModel> _all;
    private readonly Dictionary<string, double> _maxSpanByType = new(StringComparer.Ordinal);

    public ModelMatcher(IEnumerable<AircraftModel> spawnableModels, IEnumerable<FsltlRule> rules)
    {
        _all = spawnableModels.Where(m => !IsFictional(m)).ToList();
        _byTitle = new Dictionary<string, AircraftModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _all)
        {
            _byTitle.TryAdd(m.Title, m);
            if (m.WingspanM > _maxSpanByType.GetValueOrDefault(m.IcaoType))
                _maxSpanByType[m.IcaoType] = m.WingspanM;
            if (m.IsGeneric)
                Add(_genericByType, m.IcaoType, m);
            else if (IsPlaceholder(m))
                Add(_placeholders, (m.IcaoAirline, m.IcaoType), m);
            else
            {
                Add(_byAirlineType, (m.IcaoAirline, m.IcaoType), m);
                _withLivery.Add(m.IcaoAirline);
            }
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

    /// <summary>Librea inventada (aerolínea virtual): nunca se usa (ADR-166).</summary>
    public static bool IsFictional(AircraftModel m) => m.Title.Contains("Fictional", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Librea que no es la de la aerolínea de hoy (ADR-166): "STUB" (el avión blanco genérico con el nombre de la aerolínea,
    /// KMIA 2026-11-25) o "Historical" (la pintura de otra época o de una aerolínea que ya no existe).
    /// </summary>
    public static bool IsPlaceholder(AircraftModel m) =>
        m.Title.EndsWith("STUB", StringComparison.OrdinalIgnoreCase) || m.Title.Contains("Historical", StringComparison.OrdinalIgnoreCase);

    /// <summary>¿Hay instalada alguna librea actual de la aerolínea?</summary>
    public bool HasLivery(string airline) => _withLivery.Contains(airline.Trim().ToUpperInvariant());

    /// <summary>Los modelos propios de una aerolínea: los de su librea o, si no tiene ninguna, los de relleno.</summary>
    private Dictionary<(string Airline, string Type), List<AircraftModel>> OwnTable(string airline) =>
        _withLivery.Contains(airline) ? _byAirlineType : _placeholders;

    /// <summary>Todos los títulos de aviones que el simulador puede crear (incluidos los suyos), para la decoración (ADR-109).</summary>
    public IReadOnlySet<string> SimTitles { get; set; } = new HashSet<string>();

    /// <summary>Modelos de carga con livery de aerolínea (para las plataformas de carga sin aerolíneas de carga en las rutas).</summary>
    public IReadOnlyList<AircraftModel> CargoModels() =>
        [.. _all.Where(m => !m.IsGeneric && m.IcaoAirline.Length > 0 && IsCargo(m) && !IsPlaceholder(m))];

    /// <summary>Modelo de carga: puesto de carga en su cfg, o "Cargo"/"Freight" en el título.</summary>
    public static bool IsCargo(AircraftModel m) =>
        m.ParkingTypes.Contains("CARGO") || m.Title.Contains("Cargo", StringComparison.OrdinalIgnoreCase)
        || m.Title.Contains("Freight", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Aerolínea de carga según sus liveries instaladas: tiene alguna y todas son de carga (ADR-163). La base mundial de rutas
    /// no dice si una aerolínea es de carga.
    /// </summary>
    public bool IsCargoAirline(string airline)
    {
        airline = airline.Trim().ToUpperInvariant();
        var any = false;
        foreach (var (key, models) in OwnTable(airline))
        {
            if (key.Airline != airline)
                continue;
            foreach (var m in models)
            {
                if (!IsCargo(m))
                    return false;
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// Un modelo propio de la aerolínea, de ese tipo y de esa clase (carga o pasajeros), o null: no cae a uno genérico ni a un
    /// carguero para un vuelo de pasajeros (ADR-166).
    /// </summary>
    /// <param name="used">Liveries ya puestas: una especial o con matrícula es un solo avión y no se repite (ADR-171).</param>
    public ModelMatch? MatchOwn(string airline, string type, string variationKey, bool cargo, IReadOnlySet<string>? used = null)
    {
        airline = airline.Trim().ToUpperInvariant();
        if (!OwnTable(airline).TryGetValue((airline, type.Trim().ToUpperInvariant()), out var own))
            return null;
        return PickLivery(own.Where(m => IsCargo(m) == cargo).ToList(), variationKey, used) is { } m
            ? new ModelMatch(m, MatchKind.AirlineLivery) : null;
    }

    /// <summary>¿Tiene la aerolínea modelos de carga y de pasajeros?</summary>
    public bool IsMixed(string airline)
    {
        airline = airline.Trim().ToUpperInvariant();
        var (cargo, pax) = (false, false);
        foreach (var (key, models) in OwnTable(airline))
        {
            if (key.Airline != airline)
                continue;
            foreach (var m in models)
            {
                if (IsCargo(m))
                    cargo = true;
                else
                    pax = true;
            }
        }
        return cargo && pax;
    }

    /// <summary>Peso de la livery normal de la aerolínea, de una con matrícula y de una especial (ADR-171). APROXIMADO.</summary>
    private const double PlainWeight = 6, TailWeight = 1, SpecialWeight = 0.3;

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|[_ ])(N\d{1,5}[A-Z]{0,2}|[A-Z]{1,2}-[A-Z0-9]{3,5}|\d{3})$")]
    private static partial System.Text.RegularExpressions.Regex TailName();

    /// <summary>
    /// Entre las liveries de una aerolínea y tipo: la normal (la de nombre más corto de cada paquete) es la de casi toda la flota;
    /// las que llevan matrícula y las especiales (retro, aniversario) son un avión cada una. En KMIA, 10 de los 16 B738 de
    /// American salían con pintura retro.
    /// </summary>
    private static AircraftModel? PickLivery(List<AircraftModel> candidates, string variationKey, IReadOnlySet<string>? used)
    {
        if (candidates.Count <= 1)
            return candidates.Count == 0 ? null : candidates[0];
        candidates.Sort((x, y) => string.CompareOrdinal(x.Title, y.Title));
        var total = 0.0;
        var weights = new double[candidates.Count];
        for (var i = 0; i < weights.Length; i++)
        {
            var m = candidates[i];
            var tail = TailName().IsMatch(m.Title);
            var plain = !tail;
            for (var j = 0; j < candidates.Count && plain; j++)
            {
                var o = candidates[j];
                plain = j == i || o.Source != m.Source || TailName().IsMatch(o.Title) || o.Title.Length > m.Title.Length
                    || o.Title.Length == m.Title.Length && j > i;
            }
            weights[i] = plain ? PlainWeight : used?.Contains(m.Title) == true ? 0 : tail ? TailWeight : SpecialWeight;
            total += weights[i];
        }
        if (total <= 0)
            return Pick(candidates, variationKey);
        // Estable para la misma clave.
        var roll = StableHash(variationKey) / (uint.MaxValue + 1.0) * total;
        for (var i = 0; i < weights.Length; i++)
        {
            if ((roll -= weights[i]) < 0)
                return candidates[i];
        }
        return candidates[^1];
    }

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
        return OwnTable(airline).Keys.Where(k => k.Airline == airline).Select(k => k.Type).Distinct().Order().ToList();
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
        if (airlineCandidates.Count == 0 && _placeholders.TryGetValue((airline, type), out var stubs) && !HasLivery(airline))
            airlineCandidates.AddRange(stubs);
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
