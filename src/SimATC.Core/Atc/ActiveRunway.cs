using SimATC.Core.Airports;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>Límites para elegir la pista en uso (RF-23). Configurables; los valores por defecto se explican en ADR-027.</summary>
public sealed record RunwaySelectionLimits(
    double MaxTailwindKt = 5,
    double MaxCrosswindKt = 15,
    double CalmKt = 3,
    double ReturnToPreferredAfterS = 600,
    double? PreferredMaxTailwindKt = null)
{
    /// <summary>Viento de cola aceptado en la pista preferente: el local del AIP si lo hay (SCEL: 10 kt).</summary>
    public double PreferredTailwindKt => PreferredMaxTailwindKt ?? MaxTailwindKt;

    public static RunwaySelectionLimits Default { get; } = new();
}

/// <summary>Las cabeceras en uso (las paralelas en el mismo sentido van juntas) y por qué se eligieron.</summary>
public sealed record RunwayInUse(IReadOnlyList<string> Ends, double HeadingTrueDeg, string Reason)
{
    public string Label => string.Join("/", Ends);
}

/// <summary>Viento de frente (positivo) o de cola (negativo) y cruzado (siempre positivo) sobre una cabecera.</summary>
public readonly record struct WindComponents(double HeadwindKt, double CrosswindKt)
{
    public double TailwindKt => Math.Max(0, -HeadwindKt);

    public static WindComponents For(double runwayHeadingTrueDeg, double windFromTrueDeg, double windKt)
    {
        var angle = GeoMath.AngleDiffDeg(runwayHeadingTrueDeg, windFromTrueDeg) * GeoMath.DegToRad;
        return new WindComponents(windKt * Math.Cos(angle), Math.Abs(windKt * Math.Sin(angle)));
    }
}

/// <summary>
/// Elige la pista en uso según el viento (RF-23), como lo hace un controlador:
/// <list type="number">
/// <item>Se mantiene la pista actual mientras el viento de cola y el cruzado estén dentro de los límites: no se cambia
///   por un giro leve del viento.</item>
/// <item>Si hay una pista preferente (del archivo ATC del aeropuerto) y está dentro de los límites, se vuelve a ella,
///   pero solo después de <see cref="RunwaySelectionLimits.ReturnToPreferredAfterS"/> seguidos de viento favorable.</item>
/// <item>Si no, la cabecera con más viento de frente entre las que cumplen el límite de cruzado; si ninguna lo cumple,
///   la de más viento de frente.</item>
/// </list>
/// Todo con rumbos verdaderos (viento verdadero contra rumbo verdadero de la pista): no depende de la variación magnética.
/// Las cabeceras paralelas con el mismo rumbo (17L y 17R) se usan juntas.
/// </summary>
public sealed class ActiveRunwaySelector(AirportData airport, IReadOnlyList<string>? preferred = null, RunwaySelectionLimits? limits = null)
{
    private readonly RunwaySelectionLimits _limits = limits ?? RunwaySelectionLimits.Default;
    private readonly List<(string Ident, double Heading)> _ends = Ends(airport);
    private double _preferredOkS;
    // La pista actual salió de una regla arbitraria (viento calmo sin preferente, o provisional "la principal"): la del plan
    // del piloto puede reemplazarla (ADR-127).
    private bool _arbitrary;

    public RunwayInUse? Current { get; private set; }

    /// <summary>Forzar una pista (la elige el usuario); null vuelve a la elección por viento.</summary>
    public string? Forced { get; set; }

    /// <summary>
    /// La pista del plan del piloto para este aeropuerto (ADR-127). Solo decide con viento calmo y sin pista preferente del
    /// AIP: ahí cualquier sentido vale y el del plan evita reprogramar el FMS. Con viento manda el viento.
    /// </summary>
    public string? Planned { get; set; }

    /// <summary>Evalúa con el viento actual; <paramref name="elapsedS"/> desde la evaluación anterior. Devuelve true si cambió.</summary>
    public bool Update(double windFromTrueDeg, double windKt, double elapsedS)
    {
        // Aeropuerto sin pistas utilizables (un helipuerto o parque): no hay pista en uso (antes se caía en Choose).
        if (_ends.Count == 0)
            return false;
        var next = Choose(windFromTrueDeg, windKt, elapsedS);
        if (Current is { } c && c.Label == next.Label)
        {
            Current = next with { Reason = c.Reason };
            return false;
        }
        Current = next;
        return true;
    }

    /// <summary>
    /// ADR-089: sin viento del aeropuerto todavía (se activa en vuelo a 150 NM y LUAW solo conoce el clima donde está el
    /// avión), una pista provisional para que el tráfico y el ATC funcionen desde ya: la que planificó el piloto
    /// (<paramref name="planned"/>, de SimBrief) si es apta, si no la preferente del AIP, si no la principal (la más larga).
    /// Con el viento real se vuelve a elegir con las reglas de siempre (se mantiene si el viento la permite). true si la fijó.
    /// </summary>
    public bool SetProvisional(string? planned)
    {
        if (Current is not null || _ends.Count == 0)
            return false;
        if (planned is { Length: > 0 } p && _ends.Any(e => string.Equals(e.Ident, p, StringComparison.OrdinalIgnoreCase)))
            Current = Group(_ends.First(e => string.Equals(e.Ident, p, StringComparison.OrdinalIgnoreCase)).Ident, Tr.T("provisional: la de tu plan, sin viento del aeropuerto todavía"));
        else if (preferred is { Count: > 0 } && _ends.Any(e => e.Ident == preferred[0]))
            Current = Group(preferred[0], Tr.T("provisional: la preferente, sin viento del aeropuerto todavía"));
        else
        {
            Current = Group(_ends[0].Ident, Tr.T("provisional: la principal, sin viento del aeropuerto todavía"));
            _arbitrary = true;
        }
        return true;
    }

    private RunwayInUse Choose(double windFrom, double windKt, double elapsedS)
    {
        if (Forced is { } forced && _ends.Any(e => e.Ident == forced))
            return Group(forced, Tr.T("elegida a mano"));

        var calm = windKt < _limits.CalmKt;
        if (!calm)
            _arbitrary = false; // el viento ya la confirmó o la cambió
        var preferredOk = preferred is { Count: > 0 } && preferred.All(p => _ends.Any(e => e.Ident == p))
                          && Within(preferred[0], windFrom, calm ? 0 : windKt, _limits.PreferredTailwindKt);
        _preferredOkS = preferredOk ? _preferredOkS + elapsedS : 0;

        // Viento calmo, sin preferente y la actual era arbitraria (o no hay): la del plan del piloto, si es apta.
        var planned = calm && preferred is not { Count: > 0 } && Planned is { Length: > 0 } pl
            ? _ends.FirstOrDefault(e => string.Equals(e.Ident, pl, StringComparison.OrdinalIgnoreCase)).Ident : null;
        if (planned is not null && (Current is null || _arbitrary && !Current.Ends.Contains(planned)))
        {
            _arbitrary = false;
            return Group(planned, Tr.T("viento calmo: la de tu plan"));
        }
        if (Current is { } cur && Within(cur.Ends[0], windFrom, calm ? 0 : windKt, TailLimit(cur.Ends[0])))
        {
            // Ya en la preferente, o el viento todavía no lleva el tiempo suficiente para volver a ella.
            if (preferred is not { Count: > 0 } || cur.Ends.Contains(preferred[0]) || _preferredOkS < _limits.ReturnToPreferredAfterS)
                return cur;
        }
        if (preferredOk && (Current is null || _preferredOkS >= _limits.ReturnToPreferredAfterS || !Within(Current.Ends[0], windFrom, windKt, TailLimit(Current.Ends[0]))))
            return Group(preferred![0], calm ? Tr.T("viento calmo: pista preferente") : Tr.T("pista preferente con viento dentro de los límites"));
        if (calm)
        {
            _arbitrary |= Current is null;
            return Current ?? Group(_ends[0].Ident, Tr.T("viento calmo"));
        }

        var best = _ends
            .Select(e => (e.Ident, W: WindComponents.For(e.Heading, windFrom, windKt)))
            .OrderByDescending(x => x.W.CrosswindKt <= _limits.MaxCrosswindKt)
            .ThenByDescending(x => x.W.HeadwindKt)
            .First();
        return Group(best.Ident, Tr.F("más viento de frente ({0:0} kt, cruzado {1:0} kt)", best.W.HeadwindKt, best.W.CrosswindKt));
    }

    private double TailLimit(string ident) =>
        preferred is { Count: > 0 } && preferred.Contains(ident) ? _limits.PreferredTailwindKt : _limits.MaxTailwindKt;

    private bool Within(string ident, double windFrom, double windKt, double maxTailwindKt)
    {
        var end = _ends.FirstOrDefault(e => e.Ident == ident);
        if (end.Ident is null)
            return false;
        var w = WindComponents.For(end.Heading, windFrom, windKt);
        return w.TailwindKt <= maxTailwindKt && w.CrosswindKt <= _limits.MaxCrosswindKt;
    }

    /// <summary>La cabecera y sus paralelas en el mismo sentido (rumbo a menos de 5°), ordenadas por nombre.</summary>
    private RunwayInUse Group(string ident, string reason)
    {
        var heading = _ends.First(e => e.Ident == ident).Heading;
        var group = _ends.Where(e => Math.Abs(GeoMath.AngleDiffDeg(e.Heading, heading)) < 5).Select(e => e.Ident).Order().ToList();
        return new RunwayInUse(group, heading, reason);
    }

    /// <summary>Pistas aptas para el tráfico de aerolínea: al menos este largo (m) si el aeropuerto tiene alguna así. APROXIMADO.</summary>
    public const double AirlineMinLengthM = 1800;

    /// <summary>...y al menos esta parte de la más larga (no una pista corta cruzada por un poco más de viento de frente).</summary>
    public const double ShareOfLongest = 0.7;

    /// <summary>
    /// Las cabeceras candidatas (ADR-086, cualquier aeropuerto del mundo): solo pistas aptas para aviones de línea, de la más
    /// larga a la más corta (con viento calmo se usa la más larga, como la pista principal).
    /// </summary>
    /// <summary>Cuántas pistas aptas para aviones de línea tiene (para la capacidad de llegadas por hora, ADR-088).</summary>
    public static int AirlineRunwayCount(AirportData a) => Ends(a).Count / 2;

    /// <summary>Índices de las pistas aptas para aviones de línea (ADR-086), para el reparto de pistas (ADR-091).</summary>
    public static List<int> AirlineRunwayIndices(AirportData a)
    {
        var longest = a.Runways.Count == 0 ? 0 : a.Runways.Max(r => r.LengthM);
        var min = Math.Max(Math.Min(AirlineMinLengthM, longest), longest * ShareOfLongest);
        return [.. Enumerable.Range(0, a.Runways.Count).Where(i => a.Runways[i].LengthM >= min - 1)];
    }

    private static List<(string, double)> Ends(AirportData a)
    {
        var longest = a.Runways.Count == 0 ? 0 : a.Runways.Max(r => r.LengthM);
        var min = Math.Max(Math.Min(AirlineMinLengthM, longest), longest * ShareOfLongest);
        var list = new List<(string, double)>();
        foreach (var r in a.Runways.Where(r => r.LengthM >= min - 1).OrderByDescending(r => r.LengthM))
        {
            list.Add((r.Primary.Ident, r.Primary.HeadingTrueDeg));
            list.Add((r.Secondary.Ident, r.Secondary.HeadingTrueDeg));
        }
        return list;
    }
}
