using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Units;
using SimATC.Core.World;

using SimATC.Core.Text;

namespace SimATC.Core.Flight;

/// <summary>Un fijo de la ruta en ruta (ya resuelto en el simulador) y la aerovía por la que se llega a él ("" = directo).</summary>
public readonly record struct RouteFix(string Ident, string Region, GeoPoint Position, string Airway);

/// <summary>
/// La ruta en ruta de un vuelo, ya resuelta: los fijos en orden, el texto como en un plan de vuelo, de dónde salió (VATSIM,
/// aerovías del simulador o directo) y los nombres de SID y STAR que trae el texto (si los trae).
/// </summary>
public sealed record ResolvedRoute(IReadOnlyList<RouteFix> Fixes, string Text, string Source, string? SidToken = null, string? StarToken = null)
{
    public static readonly ResolvedRoute Direct = new([], "DCT", "directo");
}

/// <summary>Los dos extremos de un vuelo: aeropuertos (con sus datos del simulador, si se pudieron leer) y pistas.</summary>
public sealed record FlightEnds(
    string OriginIcao, GeoPoint OriginPosition, double OriginElevationFt, AirportData? Origin, string? DepartureRunway,
    string DestinationIcao, GeoPoint DestinationPosition, double DestinationElevationFt, AirportData? Destination, LandingRunway? Landing);

/// <summary>Un plan armado: la ruta lateral completa con perfil, los procedimientos y el nivel de crucero.</summary>
public sealed record PlannedFlight(FlightPath Path, string? Sid, string? Star, string? Approach, string RouteText, string Source, double CruiseFt)
{
    /// <summary>Lo que el validador encontró raro en el plan (giros cerrados, pasar sobre el aeropuerto…); vacío si está bien.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Por qué se eligió cada procedimiento (para la ficha), como en el plan del piloto.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>
    /// Dónde está el vuelo cuando lleva <paramref name="alongM"/> recorridos: posición, rumbo, punto siguiente y altitud
    /// (la menor entre el ascenso desde el origen, el crucero y el perfil de descenso).
    /// </summary>
    public (GeoPoint Position, double HeadingDeg, int NextIndex, double AltitudeFt, bool Climbing) At(double alongM, double originElevationFt)
    {
        var (pos, heading, profile, next) = Path.PointAt(alongM);
        // Ascenso medio de un reactor: ~300 ft por NM recorrida (FL350 a ~115 NM). APROXIMADO.
        var climb = originElevationFt + 1500 + alongM / UnitConversions.MetersPerNauticalMile * 300;
        var alt = Math.Min(CruiseFt, climb);
        if (profile > 0)
            alt = Math.Min(alt, profile);
        return (pos, heading, next, Math.Round(alt / 100) * 100, climb < CruiseFt && (profile <= 0 || climb < profile));
    }
}

/// <summary>
/// Plan de vuelo de un IA como lo arma un FMS (ADR-062), con los datos del simulador:
/// <list type="number">
/// <item>la ruta escrita del par (VATSIM) o, si no hay, la búsqueda por aerovías desde el final de la SID hasta la entrada
/// de la STAR (<see cref="ResolveRouteAsync"/>);</item>
/// <item>la SID de la pista de salida que termina en un fijo de la ruta (la que nombra el texto, si sirve);</item>
/// <item>la STAR de la pista de llegada que empieza en un fijo de la ruta (ídem);</item>
/// <item>la aproximación de esa pista con la transición que empieza donde termina la STAR (o que la contiene), y la final.</item>
/// </list>
/// Nada de puntos inventados: si un procedimiento no empalma, se une al siguiente fijo de la ruta (como un "direct to").
/// </summary>
public static class FlightPlanBuilder
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    /// <summary>Tiempo máximo para resolver una ruta (s). Si no alcanza, la siguiente opción.</summary>
    public const double ResolveBudgetS = 25;

    // --- 1. Ruta en ruta ---

    /// <summary>
    /// La ruta en ruta del vuelo: primero las de VATSIM del par (la más vista que no dé rodeos), después aerovías del
    /// simulador entre la SID y la STAR, y si nada sirve, directo.
    /// </summary>
    public static async Task<ResolvedRoute> ResolveRouteAsync(FlightEnds e, AirwayDatabase? db, NavIndex? nav, VatsimRouteStore? store,
        Action<string>? log, CancellationToken ct)
    {
        if (db is null)
            return ResolvedRoute.Direct;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(ResolveBudgetS));
        try
        {
            if (store is not null && await VatsimRouteAsync(e, db, nav, store, log, budget.Token).ConfigureAwait(false) is { } v)
                return v;
            if (await AirwayRouteAsync(e, db, nav, log, budget.Token).ConfigureAwait(false) is { } a)
                return a;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Invoke(Tr.F("{0}-{1}: la ruta tardó más de {2:0} s; va directo.", e.OriginIcao, e.DestinationIcao, ResolveBudgetS));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke(Tr.F("{0}-{1}: no se pudo armar la ruta ({2}); va directo.", e.OriginIcao, e.DestinationIcao, ex.Message));
        }
        return ResolvedRoute.Direct;
    }

    // Rutas de VATSIM ya descartadas en esta sesión (origen-destino|texto): no se vuelven a expandir ni a anotar en el log
    // cada vez que se rehace el tráfico de la zona (vuelo EGLL-EDDF: las mismas, cada pocos minutos).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Rejected = new(StringComparer.Ordinal);

    private static async Task<ResolvedRoute?> VatsimRouteAsync(FlightEnds e, AirwayDatabase db, NavIndex? nav, VatsimRouteStore store,
        Action<string>? log, CancellationToken ct)
    {
        string[] hints = [e.OriginIcao[..Math.Min(2, e.OriginIcao.Length)], e.DestinationIcao[..Math.Min(2, e.DestinationIcao.Length)]];
        var gcNm = GeoMath.DistanceM(e.OriginPosition, e.DestinationPosition) / Nm;
        foreach (var text in store.RoutesFor(e.OriginIcao, e.DestinationIcao).Take(3))
        {
            var key = $"{e.OriginIcao}-{e.DestinationIcao}|{text}";
            if (Rejected.ContainsKey(key))
                continue;
            var tokens = VatsimRouteStore.Tokens(text);
            if (tokens.Count == 0)
                continue;
            // SID y STAR por su nombre (el primero y el último que no son fijos ni aerovías).
            string? sid = null, star = null;
            if (e.Origin is { } o && ProcedureMatch.Best(ProcedureMatch.Rank(o.Departures, tokens[0], e.DepartureRunway)) is not null)
                sid = tokens[0];
            if (e.Destination is { } d && ProcedureMatch.Best(ProcedureMatch.Rank(d.Arrivals, tokens[^1], e.Landing?.Ident)) is not null)
                star = tokens[^1];
            var body = tokens.Where((t, i) => !(i == 0 && sid is not null) && !(i == tokens.Count - 1 && star is not null)).ToList();
            var steps = await RouteExpander.ExpandAsync(db, body, hints, ct, nav: nav, origin: e.OriginPosition).ConfigureAwait(false);
            if (steps is null)
            {
                Rejected.TryAdd(key, 0);
                log?.Invoke(Tr.F("{0}-{1}: ruta de VATSIM sin fijos conocidos ({2}).", e.OriginIcao, e.DestinationIcao, text));
                continue;
            }
            var fixes = WithoutReversals(steps.Select(s => new RouteFix(s.Fix.Icao, s.Fix.Region, s.Fix.Position, s.Airway)).ToList(), e);
            if (!Plausible(e, fixes, gcNm, out var why))
            {
                Rejected.TryAdd(key, 0);
                log?.Invoke(Tr.F("{0}-{1}: ruta de VATSIM descartada ({2}): {3}.", e.OriginIcao, e.DestinationIcao, why, string.Join(" ", fixes.Select(f => f.Ident))));
                continue;
            }
            return new ResolvedRoute(fixes, text, "VATSIM", sid, star);
        }
        return null;
    }

    /// <summary>¿La ruta va de verdad de origen a destino? Sin rodeos (≤ 1,3 veces la ortodrómica + 60 NM) y empieza y termina cerca.</summary>
    internal static bool Plausible(FlightEnds e, IReadOnlyList<RouteFix> fixes, double gcNm) => Plausible(e, fixes, gcNm, out _);

    internal static bool Plausible(FlightEnds e, IReadOnlyList<RouteFix> fixes, double gcNm, out string why)
    {
        why = "";
        if (fixes.Count == 0)
        {
            why = Tr.T("sin fijos");
            return false;
        }
        var len = GeoMath.DistanceM(e.OriginPosition, fixes[0].Position);
        for (var i = 1; i < fixes.Count; i++)
            len += GeoMath.DistanceM(fixes[i - 1].Position, fixes[i].Position);
        len += GeoMath.DistanceM(fixes[^1].Position, e.DestinationPosition);
        var first = GeoMath.DistanceM(e.OriginPosition, fixes[0].Position) / Nm;
        var last = GeoMath.DistanceM(e.DestinationPosition, fixes[^1].Position) / Nm;
        why = len / Nm > 1.3 * gcNm + 60 ? Tr.F("rodeo: {0:0} NM para {1:0}", len / Nm, gcNm)
            : first >= Math.Max(250, gcNm * 0.5) ? Tr.F("empieza a {0:0} NM del origen", first)
            : last >= Math.Max(300, gcNm * 0.5) ? Tr.F("termina a {0:0} NM del destino", last) : "";
        return why.Length == 0;
    }

    /// <summary>
    /// Por aerovías del simulador (A* perezoso) desde el último fijo de la SID que sale hacia el destino hasta la entrada de
    /// la STAR que viene del origen; sin SID o STAR, desde/hacia el fijo con aerovías más cercano. null si no hay camino.
    /// </summary>
    /// <summary>
    /// Por aerovías del simulador entre una salida de SID y una entrada de STAR. Prueba la SID elegida primero y, si da
    /// rodeo o no hay camino, las otras SID de la pista y las otras STAR (ADR-089: SCIE-SCEL desde SOSTA daba 429 NM y el
    /// vuelo quedaba en directo; por LODOS, UQ808 a EROLO, como en VATSIM).
    /// </summary>
    private static async Task<ResolvedRoute?> AirwayRouteAsync(FlightEnds e, AirwayDatabase db, NavIndex? nav, Action<string>? log,
        CancellationToken ct)
    {
        var gcNm = GeoMath.DistanceM(e.OriginPosition, e.DestinationPosition) / Nm;
        if (gcNm < 60)
            return null; // vuelo corto: SID, directo y STAR
        var exits = new List<NavPoint?>();
        if (ChooseSid(e, [], null)?.ExitFix is { } first)
            exits.Add(first);
        foreach (var x in SidExits(e))
        {
            if (!exits.Any(p => p?.Ident == x.Ident))
                exits.Add(x);
        }
        if (exits.Count == 0)
            exits.Add(null);
        var entries = RankStars(e, [], null, 0).Select(r => (NavPoint?)r.Choice.EntryFix).DistinctBy(p => p?.Ident).Take(3).ToList();
        if (entries.Count == 0)
            entries.Add(null);
        // Se prueban las combinaciones y queda la más corta (con la SID hasta la salida y la STAR desde la entrada).
        var tries = 0;
        ResolvedRoute? best = null;
        var bestM = double.MaxValue;
        foreach (var exit in exits.Take(MaxSidTries))
        {
            foreach (var entry in entries)
            {
                if (++tries > MaxAirwayTries)
                    break;
                if (await AirwayRouteFromAsync(e, db, nav, log, exit, entry, gcNm, ct).ConfigureAwait(false) is not { } r)
                    continue;
                var m = GeoMath.DistanceM(e.OriginPosition, r.Fixes[0].Position) + GeoMath.DistanceM(r.Fixes[^1].Position, e.DestinationPosition);
                for (var i = 1; i < r.Fixes.Count; i++)
                    m += GeoMath.DistanceM(r.Fixes[i - 1].Position, r.Fixes[i].Position);
                if (m < bestM)
                {
                    bestM = m;
                    best = r;
                }
            }
        }
        return best;
    }

    /// <summary>Giro (°) en un fijo de la ruta que se considera media vuelta: el fijo sobra (se pasa de largo y se vuelve).</summary>
    public const double ReversalDeg = 120;

    /// <summary>
    /// Quita los fijos donde la ruta da media vuelta (ADR-089: SCEL-SCTC pasaba de ELABA a NIA y volvía, 166°): de ahí se va
    /// directo al siguiente, como haría un despachador. El primero y el último se miran contra el origen y el destino.
    /// </summary>
    internal static List<RouteFix> WithoutReversals(List<RouteFix> fixes, FlightEnds e)
    {
        var list = new List<RouteFix>(fixes);
        for (var guard = 0; guard < 20 && list.Count >= 2; guard++)
        {
            var removed = false;
            for (var i = 0; i < list.Count; i++)
            {
                var prev = i == 0 ? e.OriginPosition : list[i - 1].Position;
                var next = i == list.Count - 1 ? e.DestinationPosition : list[i + 1].Position;
                if (GeoMath.DistanceM(prev, list[i].Position) < 1 || GeoMath.DistanceM(list[i].Position, next) < 1)
                    continue;
                var turn = Math.Abs(GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(prev, list[i].Position),
                    GeoMath.InitialBearingDeg(list[i].Position, next)));
                if (turn <= ReversalDeg)
                    continue;
                if (i + 1 < list.Count)
                    list[i + 1] = list[i + 1] with { Airway = "" }; // de ahí, directo
                list.RemoveAt(i);
                removed = true;
                break;
            }
            if (!removed)
                break;
        }
        return list;
    }

    /// <summary>Cuántas salidas de SID y cuántas combinaciones SID-STAR se prueban como mucho por aerovías.</summary>
    public const int MaxSidTries = 4;
    public const int MaxAirwayTries = 8;

    /// <summary>Los fijos donde terminan las SID de la pista, sin las que salen hacia atrás, de la más alineada a la menos.</summary>
    private static List<NavPoint> SidExits(FlightEnds e)
    {
        var list = new List<(NavPoint Fix, double Off)>();
        if (e.Origin is not { } o || e.DepartureRunway is not { } runway)
            return [];
        var bearing = GeoMath.InitialBearingDeg(o.Reference, e.DestinationPosition);
        foreach (var sid in o.Departures)
        {
            if (!ProcedureGeometry.SidFitsRunway(sid, o, runway))
                continue;
            foreach (var t in sid.EnrouteTransitions.Count == 0 ? [null] : sid.EnrouteTransitions.Select(t => (string?)t.Name).ToList())
            {
                var shape = ProcedureGeometry.Sid(sid, o, runway, t);
                if (Flatten(shape).Count < 2 || LastFix(shape) is not { } exit || list.Any(x => x.Fix.Ident == exit.Ident))
                    continue;
                var off = Math.Abs(GeoMath.AngleDiffDeg(bearing, GeoMath.InitialBearingDeg(o.Reference, exit.Position)));
                if (off <= 100)
                    list.Add((exit, off));
            }
        }
        return [.. list.OrderBy(x => x.Off).Select(x => x.Fix)];
    }

    private static async Task<ResolvedRoute?> AirwayRouteFromAsync(FlightEnds e, AirwayDatabase db, NavIndex? nav, Action<string>? log,
        NavPoint? exit, NavPoint? entry, double gcNm, CancellationToken ct)
    {
        var track = GeoMath.InitialBearingDeg(e.OriginPosition, e.DestinationPosition);
        var back = GeoMath.InitialBearingDeg(e.DestinationPosition, e.OriginPosition);
        var start = await WithAirwaysAsync(db, nav, exit, exit?.Position ?? e.OriginPosition, track, ct).ConfigureAwait(false);
        var goalFix = await WithAirwaysAsync(db, nav, entry, entry?.Position ?? e.DestinationPosition, back, ct).ConfigureAwait(false);
        if (start is null || goalFix is null)
        {
            log?.Invoke(Tr.F("{0}-{1}: sin fijo con aerovías cerca de {2}.", e.OriginIcao, e.DestinationIcao, (start is null ? Tr.F("la salida ({0})", exit?.Ident ?? Tr.T("sin SID")) : Tr.F("la llegada ({0})", entry?.Ident ?? Tr.T("sin STAR")))));
            return null;
        }
        var rejectedKey = $"aw|{e.OriginIcao}-{e.DestinationIcao}|{start.Icao}|{goalFix.Icao}";
        if (Rejected.ContainsKey(rejectedKey))
            return null;
        var cruise = FlightPlanner.CruiseAltitude(PerformanceCategory.MediumJet, gcNm);
        // Con las aerovías ya descargadas (ADR-064) cada paso es instantáneo: se puede explorar mucho.
        var limit = (int)Math.Clamp(gcNm * 4, 500, 20_000);
        var target = goalFix.Position;
        bool Exact(AirwayFix f) => f.Icao == goalFix.Icao && f.Region == goalFix.Region;
        // Si no se llega exacto, a un fijo a menos de 30 NM de la entrada (y de ahí directo).
        bool Near(AirwayFix f) => GeoMath.DistanceM(f.Position, target) < 30 * Nm;
        List<AirwayStep>? steps = null;
        foreach (var level in new bool?[] { cruise > 24_500, cruise <= 24_500, null })
        {
            steps = await AirwayRouter.FindAsync(db, start, target, Exact, level, limit, ct).ConfigureAwait(false)
                    ?? await AirwayRouter.FindAsync(db, start, target, Near, level, limit, ct).ConfigureAwait(false);
            if (steps is { Count: >= 2 })
                break;
        }
        if (steps is null || steps.Count < 2)
        {
            Rejected.TryAdd(rejectedKey, 0);
            log?.Invoke(Tr.F("{0}-{1}: sin camino por aerovías de {2} a {3} ({4} fijos conocidos).", e.OriginIcao, e.DestinationIcao, start.Icao, goalFix.Icao, db.Count));
            return null;
        }
        var fixes = steps.Select(s => new RouteFix(s.Fix.Icao, s.Fix.Region, s.Fix.Position, s.Airway)).ToList();
        if (entry is { } en && fixes[^1].Ident != en.Ident)
            fixes.Add(new RouteFix(en.Ident, en.Region, en.Position, ""));
        fixes = WithoutReversals(fixes, e);
        if (!Plausible(e, fixes, gcNm, out var why))
        {
            Rejected.TryAdd(rejectedKey, 0);
            log?.Invoke(Tr.F("{0}-{1}: ruta por aerovías descartada ({2}; de {3} ", e.OriginIcao, e.DestinationIcao, why, start.Icao) +
                        $"({start.Position.LatDeg:0.0},{start.Position.LonDeg:0.0}) a {goalFix.Icao} ({target.LatDeg:0.0},{target.LonDeg:0.0})): " +
                        string.Join(" ", fixes.Take(40).Select(f => $"{f.Ident}({f.Position.LatDeg:0.0},{f.Position.LonDeg:0.0})")));
            return null;
        }
        return new ResolvedRoute(fixes, RouteText(fixes), "aerovías del simulador");
    }

    /// <summary>
    /// El fijo dado si tiene aerovías; si no, el fijo con aerovías más cercano a <paramref name="near"/> hacia
    /// <paramref name="trackDeg"/> (a menos de 60 NM). null si no hay.
    /// </summary>
    private static async Task<AirwayFix?> WithAirwaysAsync(AirwayDatabase db, NavIndex? nav, NavPoint? fix, GeoPoint near, double trackDeg,
        CancellationToken ct)
    {
        if (fix is { Region.Length: > 0 } f && await db.GetAsync(f.Ident, f.Region, ct).ConfigureAwait(false) is { Links.Count: > 0 } have)
            return have;
        if (nav is null)
            return null;
        var dLat = 2.5;
        var dLon = 1.0 / Math.Max(0.2, Math.Cos(near.LatDeg * Math.PI / 180));
        var list = new List<NavFix>();
        nav.InBox(near.LatDeg - dLat, near.LatDeg + dLat, near.LonDeg - dLon, near.LonDeg + dLon, list, 2000);
        // Oceánico (RASEP al salir de Gran Canaria): la aerovía más cercana puede estar a más de 100 NM.
        foreach (var c in list.Where(c => c.Kind != NavKind.Ndb && GeoMath.DistanceM(c.Position, near) < 150 * Nm
                         && AirwayRouter.Ahead(near, trackDeg, c.Position, 80) && (c.Kind == NavKind.Vor || c.Ident.Length == 5))
                     .OrderBy(c => GeoMath.DistanceM(c.Position, near) < 15 * Nm ? double.MaxValue : GeoMath.DistanceM(c.Position, near)).Take(60))
        {
            // Con la región vacía el simulador devuelve el primero con ese nombre en el mundo (BCA de Bahía Blanca en vez del
            // de cerca de Barcelona): solo vale si está donde dice el índice.
            if (await db.GetAsync(c.Ident, c.Region, ct).ConfigureAwait(false) is { Links.Count: > 0 } w
                && GeoMath.DistanceM(w.Position, c.Position) < 5 * Nm)
                return w;
        }
        return null;
    }

    /// <summary>Como en un plan de vuelo: cada aerovía con el fijo donde se deja ("ALGEK UQ808 EROLO DCT TBN").</summary>
    public static string RouteText(IReadOnlyList<RouteFix> fixes)
    {
        if (fixes.Count == 0)
            return "DCT";
        var sb = new System.Text.StringBuilder(fixes[0].Ident);
        for (var i = 1; i < fixes.Count; i++)
        {
            var aw = fixes[i].Airway.Length > 0 ? fixes[i].Airway : "DCT";
            var nextSame = i + 1 < fixes.Count && fixes[i + 1].Airway == fixes[i].Airway && aw != "DCT";
            if (nextSame)
                continue;
            // Cambio de aerovía (o directo): "aerovía fijo".
            sb.Append(' ').Append(aw).Append(' ').Append(fixes[i].Ident);
        }
        return sb.ToString();
    }

    // --- 2. Procedimientos ---

    private sealed record SidChoice(string Name, ProcedureShape Shape, NavPoint ExitFix, int JoinIndex);
    private sealed record StarChoice(string Name, ProcedureShape Shape, NavPoint EntryFix, int JoinIndex);
    private readonly record struct NavPoint(string Ident, string Region, GeoPoint Position);

    /// <summary>Parecido (0-100) de cada procedimiento con el nombre que trae el plan ("DONTI5B" ≈ "DONT5B" del simulador).</summary>
    private static Dictionary<Procedure, int> Named(IReadOnlyList<Procedure> procedures, string? token, string runway) =>
        token is null ? [] : ProcedureMatch.Rank(procedures, token, runway).Where(c => c.Score >= ProcedureMatch.MinAutoScore)
            .ToDictionary(c => c.Procedure, c => c.Score);

    private static NavPoint? LastFix(ProcedureShape s)
    {
        for (var i = s.Waypoints.Count - 1; i >= 0; i--)
        {
            var w = s.Waypoints[i];
            if (!w.Approximate && !w.Ident.StartsWith("RW", StringComparison.Ordinal))
                return new NavPoint(w.Ident, w.Leg?.FixRegion ?? "", w.Position);
        }
        return null;
    }

    private static NavPoint? FirstFix(ProcedureShape s)
    {
        foreach (var w in s.Waypoints)
        {
            if (!w.Approximate && !w.Ident.StartsWith("RW", StringComparison.Ordinal))
                return new NavPoint(w.Ident, w.Leg?.FixRegion ?? "", w.Position);
        }
        return null;
    }

    /// <summary>
    /// SID: de las de la pista, (1) la que nombra el plan y termina en un fijo de la ruta; (2) cualquiera que termine en un
    /// fijo de la ruta (el más temprano); (3) la que termina más cerca de los primeros fijos de la ruta sin volver atrás;
    /// (4) sin ruta, la que sale hacia el destino. <see cref="SidChoice.JoinIndex"/>: el fijo de la ruta donde empalma.
    /// </summary>
    private static SidChoice? ChooseSid(FlightEnds e, IReadOnlyList<RouteFix> route, string? token)
    {
        if (e.Origin is not { } o || e.DepartureRunway is not { } runway)
            return null;
        var named = Named(o.Departures, token, runway);
        var bearing = GeoMath.InitialBearingDeg(o.Reference, e.DestinationPosition);
        SidChoice? best = null;
        var bestScore = double.MaxValue;
        foreach (var sid in o.Departures)
        {
            if (!ProcedureGeometry.SidFitsRunway(sid, o, runway))
                continue;
            foreach (var t in sid.EnrouteTransitions.Count == 0 ? [null] : sid.EnrouteTransitions.Select(t => (string?)t.Name).ToList())
            {
                var shape = ProcedureGeometry.Sid(sid, o, runway, t);
                if (Flatten(shape).Count < 2 || LastFix(shape) is not { } exit)
                    continue;
                var name = t is null ? sid.Name : $"{sid.Name}.{t}";
                double score;
                var join = -1;
                for (var i = 0; i < route.Count && join < 0; i++)
                {
                    if (route[i].Ident == exit.Ident && GeoMath.DistanceM(route[i].Position, exit.Position) < 2 * Nm)
                        join = i;
                }
                // La SID no puede salir hacia atrás (más de 100° del destino).
                var off = Math.Abs(GeoMath.AngleDiffDeg(bearing, GeoMath.InitialBearingDeg(o.Reference, exit.Position)));
                if (join >= 0)
                {
                    score = join * 10 - named.GetValueOrDefault(sid) * 0.3;
                }
                else if (route.Count > 0)
                {
                    // El fijo de la ruta más cercano a su salida, entre los primeros (no se saltan media ruta).
                    var nearest = double.MaxValue;
                    for (var i = 0; i < Math.Min(route.Count, 4); i++)
                    {
                        var d = GeoMath.DistanceM(exit.Position, route[i].Position);
                        if (d < nearest)
                        {
                            nearest = d;
                            join = i;
                        }
                    }
                    if (nearest > 40 * Nm || off > 100)
                        continue;
                    score = 2000 + nearest / Nm * 10 + join * 50;
                }
                else
                {
                    if (off > 100)
                        continue;
                    score = 5000 + off;
                    join = -1;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new SidChoice(name, shape, exit, join);
                }
            }
        }
        // Ninguna empalma con la ruta (empieza lejos, como la de VATSIM GCLP-LEBL en AKUDA): la que sale hacia su primer fijo.
        if (best is null && route.Count > 0)
            return ChooseSid(e with { DestinationPosition = route[0].Position }, [], token);
        return best;
    }

    /// <summary>
    /// STAR: de las que sirven la pista de llegada, (1) la que nombra el plan y empieza en un fijo de la ruta; (2) cualquiera
    /// que empiece en un fijo de la ruta (el más tardío); (3) la que empieza más cerca de los últimos fijos; (4) sin ruta, la
    /// que entra por el lado del origen y da la ruta total más corta. Nunca una que empalme antes de <paramref name="minJoin"/>.
    /// </summary>
    private static List<(StarChoice Choice, double Score)> RankStars(FlightEnds e, IReadOnlyList<RouteFix> route, string? token, int minJoin)
    {
        var ranked = new List<(StarChoice, double)>();
        if (e.Destination is not { } d || e.Landing is not { } rwy)
            return ranked;
        var named = Named(d.Arrivals, token, rwy.Ident);
        var fromBearing = GeoMath.InitialBearingDeg(d.Reference, e.OriginPosition);
        var gcNm = GeoMath.DistanceM(e.OriginPosition, e.DestinationPosition) / Nm;
        foreach (var star in d.Arrivals)
        {
            if (star.RunwayTransitions.Count > 0 && !ProcedureMatch.ServesRunway(star, rwy.Ident))
                continue;
            foreach (var t in star.EnrouteTransitions.Count == 0 ? [null] : star.EnrouteTransitions.Select(t => (string?)t.Name).ToList())
            {
                var shape = ProcedureGeometry.Star(star, d, rwy.Ident, t);
                var pts = Flatten(shape);
                if (pts.Count < 2 || FirstFix(shape) is not { } entry)
                    continue;
                var name = t is null ? star.Name : $"{t}.{star.Name}";
                var join = -1;
                for (var i = route.Count - 1; i >= Math.Max(0, minJoin) && join < 0; i--)
                {
                    if (route[i].Ident == entry.Ident && GeoMath.DistanceM(route[i].Position, entry.Position) < 2 * Nm)
                        join = i;
                }
                double score;
                if (join >= 0)
                {
                    // Empieza en un fijo de la ruta ("conecta con tu ruta"); la que nombra el plan, un poco mejor.
                    score = (route.Count - join) * 10 - named.GetValueOrDefault(star) * 0.3;
                }
                else if (route.Count > 0)
                {
                    var nearest = double.MaxValue;
                    for (var i = route.Count - 1; i >= Math.Max(minJoin, route.Count - 4); i--)
                    {
                        var dd = GeoMath.DistanceM(entry.Position, route[i].Position);
                        if (dd < nearest)
                        {
                            nearest = dd;
                            join = i;
                        }
                    }
                    if (nearest > 40 * Nm)
                        continue;
                    score = 200 + nearest / Nm * 3;
                }
                else
                {
                    // Sin ruta: la entrada del lado del origen (vuelos largos) y la ruta total más corta.
                    var side = gcNm > 60
                        ? Math.Abs(GeoMath.AngleDiffDeg(fromBearing, GeoMath.InitialBearingDeg(d.Reference, entry.Position))) / 180 * 60
                        : 0;
                    // La entrada no puede quedar más lejos del destino que el origen.
                    if (GeoMath.DistanceM(entry.Position, d.Reference) > GeoMath.DistanceM(e.OriginPosition, d.Reference))
                        continue;
                    // Lo que se desvía para entrar por ahí (no la distancia total: un vuelo desde Madrid no debe preferir ir
                    // directo a la final solo porque la entrada de la STAR suma miles de NM).
                    var detour = (GeoMath.DistanceM(e.OriginPosition, entry.Position) - GeoMath.DistanceM(e.OriginPosition, d.Reference)) / Nm;
                    score = 400 + side + detour;
                }
                ranked.Add((new StarChoice(name, shape, entry, join), score));
            }
        }
        ranked.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return ranked;
    }

    /// <summary>
    /// Aproximación a la pista: entre las de mejor guía (ILS, RNAV, LOC, VOR…), la que empalma con <paramref name="fromFix"/>
    /// (una transición que empieza ahí o la contiene, o la final que lo contiene). Sin empalme, la transición cuyo inicio queda
    /// más cerca. Devuelve los puntos desde el empalme.
    /// </summary>
    private static (Approach Approach, List<PathInput> Points, double GlideDeg)? ChooseApproach(AirportData d, LandingRunway rwy, string? fromFix,
        GeoPoint fromPosition, double? inboundDeg)
    {
        (Approach, List<PathInput>, double)? best = null;
        var bestScore = double.MaxValue;
        foreach (var a in d.Approaches)
        {
            if (ProcedureMatch.ApproachRunway(a) != rwy.Ident)
                continue;
            List<string?> transitions = a.Transitions.Count == 0 ? [null] : [.. a.Transitions.Select(t => (string?)t.Name), null];
            foreach (var t in transitions)
            {
                var shape = ProcedureGeometry.Approach(a, d, t);
                var points = FlightPlanner.Inputs(shape, PathSection.Approach);
                if (points.Count < 2)
                    continue;
                // Desde el fijo de empalme (si está en esta forma), sin lo anterior.
                var at = fromFix is null ? -1 : points.FindIndex(p => p.Name == fromFix);
                // Empalmar ahí obligaría a dar media vuelta sobre el fijo (ER6EPA termina en PADOP volando al norte y la ILS
                // 17L sigue al sur): no es empalme; Approach lo lleva al inicio por viento en cola y base.
                if (at >= 0 && at + 1 < points.Count && inboundDeg is { } inb
                    && Math.Abs(GeoMath.AngleDiffDeg(inb, GeoMath.InitialBearingDeg(points[at].Position, points[at + 1].Position))) > 120)
                    at = -1;
                double score = Rank(a.Type) * 100;
                if (at >= 0)
                {
                    points = points[at..];
                    score += at == 0 ? 0 : 5; // empieza justo ahí: lo ideal
                }
                else
                {
                    // Sin empalme: "direct to" al inicio que quede más cerca. Un ILS a pocas millas gana a un VOR que empalma.
                    score += 30 + GeoMath.DistanceM(fromPosition, points[0].Position) / Nm;
                }
                if (score < bestScore)
                {
                    var glide = 3.0;
                    foreach (var leg in a.FinalLegs)
                    {
                        if (Math.Abs(leg.VerticalAngleDeg) is >= 2 and <= 4.5 and var angle)
                            glide = angle;
                    }
                    bestScore = score;
                    best = (a, points, glide);
                }
            }
        }
        return best;
    }

    private static int Rank(ApproachType t) => t switch
    {
        ApproachType.Ils => 0,
        ApproachType.Rnav or ApproachType.Gps => 1,
        ApproachType.Localizer => 2,
        _ => 3,
    };

    // --- 3. Armado ---

    /// <summary>El plan completo: SID, ruta, STAR, aproximación y final, con las esquinas redondeadas y el perfil vertical.</summary>
    public static PlannedFlight Assemble(FlightEnds e, ResolvedRoute route, PerformanceCategory category)
    {
        var fixes = route.Fixes;
        var points = new List<PathInput>();

        // Salida.
        var sid = ChooseSid(e, fixes, route.SidToken);
        var first = 0;
        if (sid is not null)
        {
            points.AddRange(FlightPlanner.Inputs(sid.Shape, PathSection.Sid));
            first = sid.JoinIndex >= 0 ? sid.JoinIndex + 1 : 0;
            // Si empalmó en un fijo de la ruta que no es su final, ese fijo también se vuela.
            if (sid.JoinIndex >= 0 && fixes[sid.JoinIndex].Ident != sid.ExitFix.Ident)
                first = sid.JoinIndex;
        }
        else if (e.Origin is { } o && e.DepartureRunway is { } dr && ProcedureGeometry.FindRunwayEnd(o, dr) is { } end)
        {
            points.Add(new PathInput(end.LiftoffEnd, "", PathSection.Sid));
            points.Add(new PathInput(GeoMath.Destination(end.LiftoffEnd, end.HeadingDeg, 5 * Nm), "", PathSection.Sid));
        }
        else
        {
            points.Add(new PathInput(e.OriginPosition, e.OriginIcao, PathSection.Sid));
        }

        // Llegada: STAR y aproximación se eligen juntas (la STAR no puede empalmar antes que la SID). De las mejores STAR por la
        // ruta (y "sin STAR", con vectores) gana la llegada completa más corta y limpia: sin medias vueltas ni pasar sobre el
        // aeropuerto (prueba de Marco: JAT630 por ER6EPA cruzaba SCEL al norte y volvía; EROL7F empalma limpio en PUMAR).
        StarChoice? star = null;
        Tail? tail = null;
        var lastEnroute = fixes.Count - 1;
        if (e.Destination is { } d && e.Landing is { } rwy)
        {
            var bestTotal = double.MaxValue;
            var options = RankStars(e, fixes, route.StarToken, first).Take(8).Select(x => ((StarChoice?)x.Choice, x.Score)).ToList();
            // Sin STAR: solo si no hay ninguna que sirva, o como comparación cuando la ruta no llega a ninguna.
            options.Add((null, options.Count == 0 ? 0 : 600));
            foreach (var (choice, baseScore) in options)
            {
                var last = fixes.Count - 1;
                if (choice is { JoinIndex: >= 0 })
                    last = fixes[choice.JoinIndex].Ident == choice.EntryFix.Ident ? choice.JoinIndex - 1 : choice.JoinIndex;
                var before = last >= first && last < fixes.Count ? fixes[last].Position
                    : points.Count > 0 ? points[^1].Position : e.OriginPosition;
                var t = BuildTail(d, rwy, choice, before);
                var (penalty, _) = ShapeIssues(t.Points, rwy, d.Reference, before);
                var total = baseScore + LengthNm(before, t.Points) + penalty;
                if (total < bestTotal)
                {
                    bestTotal = total;
                    star = choice;
                    tail = t;
                    lastEnroute = last;
                }
            }
        }
        for (var i = first; i <= lastEnroute && i < fixes.Count; i++)
            points.Add(new PathInput(fixes[i].Position, fixes[i].Ident, PathSection.Enroute, Region: fixes[i].Region));

        string? approachName = null, approachWord = null;
        var approachJoined = false;
        var glideDeg = 3.0;
        if (tail is not null)
        {
            points.AddRange(tail.Points);
            approachName = tail.ApproachName;
            approachWord = tail.ApproachWord;
            approachJoined = tail.Joined;
            glideDeg = tail.GlideDeg;
        }
        else
        {
            points.Add(new PathInput(e.DestinationPosition, e.DestinationIcao, PathSection.Enroute));
        }

        var gcNm = GeoMath.DistanceM(e.OriginPosition, e.DestinationPosition) / Nm;
        var initialTrack = GeoMath.InitialBearingDeg(e.OriginPosition, e.DestinationPosition);
        // Nunca por debajo de la mayor altitud mínima de la ruta (SCEL-SAME: GUVOL ≥ FL250 para cruzar los Andes).
        var floor = 0.0;
        foreach (var p in points)
            floor = Math.Max(floor, p.MinAltFt);
        var cruise = FlightPlanner.CruiseAltitude(category, gcNm, initialTrack, floor);
        var text = $"{sid?.Name ?? ""} {route.Text} {star?.Name ?? ""}".Trim();
        var (_, issues) = ShapeIssues(points, e.Landing, e.Destination?.Reference);
        var reasons = new List<string>();
        if (sid is not null)
            reasons.Add(sid.JoinIndex >= 0 && fixes[sid.JoinIndex].Ident == sid.ExitFix.Ident
                ? Tr.F("SID {0}: termina en {1}, donde empieza la ruta", sid.Name, sid.ExitFix.Ident)
                : Tr.F("SID {0}: sale hacia el destino", sid.Name));
        if (star is not null)
            reasons.Add(star.JoinIndex >= 0 && fixes[star.JoinIndex].Ident == star.EntryFix.Ident
                ? Tr.F("STAR {0}: empieza en {1}, donde termina la ruta", star.Name, star.EntryFix.Ident)
                : Tr.F("STAR {0}: la que entra por el lado de donde viene", star.Name));
        if (approachName is not null)
            reasons.Add(approachJoined ? Tr.F("{0}: empalma con el final de la STAR", approachName) : Tr.F("{0}: directo o vectores a su inicio (mejor guía)", approachName));
        var path = new FlightPath(FlightPlanner.RoundCorners(points))
        {
            GlideDeg = glideDeg, SidName = sid?.Name, StarName = star?.Name, ApproachName = approachName, ApproachWord = approachWord,
            RouteText = text,
        };
        var endFt = e.Landing is { } l ? l.ElevationFt + AirborneAgent.ThresholdCrossingFt : e.DestinationElevationFt + 1500;
        path.ComputeDescentProfile(endFt, FlightPlanner.GlideFtPerNm(glideDeg), cruise, climbAtCeiling: true);
        return new PlannedFlight(path, sid?.Name, star?.Name, approachName, text, route.Source, cruise) { Issues = issues, Reasons = reasons };
    }

    /// <summary>La llegada desde el final de la ruta: STAR (si hay), unión, aproximación y final hasta el umbral.</summary>
    private sealed record Tail(List<PathInput> Points, string? ApproachName, string? ApproachWord, bool Joined, double GlideDeg);

    private static Tail BuildTail(AirportData d, LandingRunway rwy, StarChoice? star, GeoPoint from)
    {
        var points = new List<PathInput>();
        string? fromFix = null;
        if (star is not null)
        {
            points.AddRange(FlightPlanner.Inputs(star.Shape, PathSection.Star));
            fromFix = LastFix(star.Shape)?.Ident;
        }
        var end = points.Count > 0 ? points[^1].Position : from;
        var inbound = InboundDeg(points, from);
        string? approachName = null, approachWord = null;
        var joined = false;
        var glideDeg = 3.0;
        var app = ChooseApproach(d, rwy, fromFix, end, inbound);
        if (app is { } ap)
        {
            approachName = ProcedureMatch.ApproachName(ap.Approach);
            approachWord = FlightPlanner.ApproachWord(ap.Approach.Type);
            glideDeg = ap.GlideDeg;
            joined = fromFix is not null && ap.Points[0].Name == fromFix;
            // Si el inicio de la aproximación queda "detrás" (del lado de la pista), viento en cola y base.
            foreach (var dj in FlightPlanner.DownwindJoin(end, ap.Points[0].Position, rwy))
                points.Add(new PathInput(dj, "", PathSection.Approach));
            points.AddRange(ap.Points);
        }
        else if (star is null)
        {
            foreach (var v in FlightPlanner.VectorsToFinal(rwy, end))
                points.Add(new PathInput(v, "", PathSection.Approach));
        }
        // Final recta si la aproximación no deja alineado.
        var reciprocal = GeoMath.NormalizeDeg(rwy.HeadingDeg + 180);
        var last = points.Count > 0 ? points[^1].Position : from;
        if (approachName is null || !FlightPlanner.IsOnFinal(rwy, last, 2 * Nm))
        {
            if (approachName is null && star is not null)
            {
                foreach (var dj in FlightPlanner.DownwindJoin(last, GeoMath.Destination(rwy.Threshold, reciprocal, 12 * Nm), rwy))
                    points.Add(new PathInput(dj, "", PathSection.Approach));
            }
            points.Add(new PathInput(GeoMath.Destination(rwy.Threshold, reciprocal, 12 * Nm), "", PathSection.Approach));
            points.Add(new PathInput(GeoMath.Destination(rwy.Threshold, reciprocal, 8 * Nm), "", PathSection.Final));
        }
        // Trombón: Approach da base cuando toca, no al final del tramo de viento en cola (AIP LEBL: "Direct to" en las
        // transiciones y vectores del sector final; en los datos, el tramo FM "hasta vectores" tras BL530).
        points = ShortenDownwind(points, rwy);
        // Sin puntos de la aproximación más allá del umbral (la frustrada no se vuela).
        var thresholdAt = points.FindLastIndex(p => p.Name == "RW" + rwy.Ident);
        if (thresholdAt >= 0)
            points.RemoveRange(thresholdAt, points.Count - thresholdAt);
        points.Add(new PathInput(rwy.Threshold, "RW" + rwy.Ident, PathSection.Final));
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.Section == PathSection.Approach && GeoMath.DistanceM(p.Position, rwy.Threshold) <= 12 * Nm
                && FlightPlanner.IsOnFinal(rwy, p.Position, 0.5 * Nm))
                points[i] = p with { Section = PathSection.Final };
        }
        return new Tail(points, approachName, approachWord, joined, glideDeg);
    }

    /// <summary>Distancia mínima del umbral (NM) a la que Approach da base para interceptar la final. APROXIMADO.</summary>
    public const double BaseTurnMinNm = 9;

    /// <summary>
    /// Acorta un tramo de viento en cola (trombón de secuenciación, como las transiciones BL5xx de LEBL): sigue el viento en
    /// cola hasta estar a <see cref="BaseTurnMinNm"/> (o 1,5 NM más allá del primer fijo de la final) y de ahí base al eje y
    /// final, sin volar el resto del trombón ni la media vuelta. Sin tráfico, así lo hace el ATC; la secuencia puede
    /// alargarlo con esperas o velocidad. Si el viento en cola es corto, se alarga hasta ese punto.
    /// </summary>
    internal static List<PathInput> ShortenDownwind(List<PathInput> points, LandingRunway rwy)
    {
        var outbound = GeoMath.NormalizeDeg(rwy.HeadingDeg + 180);
        (double Along, double Cross) Axis(GeoPoint p)
        {
            var d = GeoMath.DistanceM(rwy.Threshold, p);
            var rel = GeoMath.AngleDiffDeg(outbound, GeoMath.InitialBearingDeg(rwy.Threshold, p)) * Math.PI / 180;
            return (d * Math.Cos(rel), d * Math.Sin(rel));
        }
        bool Downwind(int i) =>
            Math.Abs(GeoMath.AngleDiffDeg(outbound, GeoMath.InitialBearingDeg(points[i].Position, points[i + 1].Position))) <= 40;

        // El primer tramo de viento en cola (paralelo a la final, alejándose, a más de 1,5 NM del eje).
        var start = -1;
        for (var i = 0; i + 1 < points.Count && start < 0; i++)
        {
            if (points[i].Section is not (PathSection.Star or PathSection.Approach)
                || GeoMath.DistanceM(points[i].Position, points[i + 1].Position) < 300)
                continue;
            if (Downwind(i) && Math.Abs(Axis(points[i].Position).Cross) >= 1.5 * Nm && Axis(points[i].Position).Along > 0)
                start = i;
        }
        if (start < 0)
            return points;
        var end = start;
        for (var j = start; j + 1 < points.Count; j++)
        {
            if (GeoMath.DistanceM(points[j].Position, points[j + 1].Position) < 300 || Downwind(j))
                end = j + 1;
            else
                break;
        }
        // El primer punto de la final después del viento en cola (PERUK en la ILS Y 06R).
        var final = -1;
        for (var k = end + 1; k < points.Count && final < 0; k++)
        {
            if (FlightPlanner.IsOnFinal(rwy, points[k].Position, 0.6 * Nm) && Axis(points[k].Position).Along > 0)
                final = k;
        }
        if (final < 0)
            return points;
        var finalAlong = Axis(points[final].Position).Along;
        var baseAlong = Math.Max(finalAlong + 1.5 * Nm, BaseTurnMinNm * Nm);
        var cut = -1;
        for (var k = start; k <= end && cut < 0; k++)
        {
            if (Axis(points[k].Position).Along >= baseAlong)
                cut = k;
        }
        var result = new List<PathInput>(points.Count);
        result.AddRange(points.Take((cut >= 0 ? cut : end) + 1));
        var (lastAlong, lastCross) = Axis(result[^1].Position);
        if (cut < 0 && lastAlong < baseAlong)
        {
            // Viento en cola corto: se sigue hasta el punto de base.
            var abeam = GeoMath.Destination(GeoMath.Destination(rwy.Threshold, outbound, baseAlong), outbound + Math.Sign(lastCross) * 90,
                Math.Abs(lastCross));
            result.Add(new PathInput(abeam, "", PathSection.Approach));
            lastAlong = baseAlong;
        }
        // Base hasta el eje y final.
        var onAxis = GeoMath.Destination(rwy.Threshold, outbound, Math.Max(lastAlong, finalAlong + Nm));
        result.Add(new PathInput(onAxis, "", PathSection.Approach));
        result.AddRange(points.Skip(final));
        return result;
    }

    /// <summary>Rumbo con el que se llega al último punto (del último tramo de más de 300 m), o null.</summary>
    private static double? InboundDeg(List<PathInput> points, GeoPoint from)
    {
        if (points.Count == 0)
            return null;
        var end = points[^1].Position;
        for (var i = points.Count - 2; i >= 0; i--)
        {
            if (GeoMath.DistanceM(points[i].Position, end) > 300)
                return GeoMath.InitialBearingDeg(points[i].Position, end);
        }
        return GeoMath.DistanceM(from, end) > 300 ? GeoMath.InitialBearingDeg(from, end) : null;
    }

    private static double LengthNm(GeoPoint from, List<PathInput> points)
    {
        var len = 0.0;
        var prev = from;
        foreach (var p in points)
        {
            len += GeoMath.DistanceM(prev, p.Position);
            prev = p.Position;
        }
        return len / Nm;
    }

    /// <summary>Penalización (NM equivalentes) por cada giro de más de 120° en un fijo.</summary>
    public const double ReversalPenaltyNm = 60;

    /// <summary>Penalización por pasar sobre el aeropuerto de destino fuera de la final.</summary>
    public const double OverflightPenaltyNm = 30;

    /// <summary>
    /// Validador de planes: busca formas que un avión real no vuela (media vuelta sobre un fijo, pasar sobre el aeropuerto
    /// de destino antes de la final, saltos de más de 600 NM) y devuelve la penalización y la lista legible. Se mira sobre los
    /// puntos sin redondear (los arcos de las cartas vienen densos y no cuentan como giros cerrados).
    /// </summary>
    public static (double PenaltyNm, List<string> Issues) ShapeIssues(IReadOnlyList<PathInput> points, LandingRunway? rwy, GeoPoint? airport,
        GeoPoint? from = null)
    {
        var issues = new List<string>();
        var penalty = 0.0;
        var list = new List<PathInput>(points.Count + 1);
        if (from is { } f)
            list.Add(new PathInput(f, "", PathSection.Enroute));
        foreach (var p in points)
        {
            // Sin puntos repetidos (rompen el rumbo).
            if (list.Count == 0 || GeoMath.DistanceM(list[^1].Position, p.Position) > 300)
                list.Add(p);
        }
        for (var i = 1; i + 1 < list.Count; i++)
        {
            var a = list[i - 1].Position;
            var b = list[i].Position;
            var c = list[i + 1].Position;
            var turn = Math.Abs(GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(a, b), GeoMath.InitialBearingDeg(b, c)));
            if (turn > FlightPlanner.ReversalDeg)
            {
                penalty += ReversalPenaltyNm;
                issues.Add(Tr.F("media vuelta de {0:0}° en {1} ({2})", turn, (list[i].Name.Length > 0 ? list[i].Name : Tr.T("un punto")), list[i].Section));
            }
            if (GeoMath.DistanceM(b, c) > 600 * Nm)
                issues.Add(Tr.F("tramo de {0:0} NM sin fijos", GeoMath.DistanceM(b, c) / Nm));
        }
        if (airport is { } ap && rwy is not null)
        {
            foreach (var p in list)
            {
                if (p.Section is PathSection.Star or PathSection.Enroute or PathSection.Approach
                    && GeoMath.DistanceM(p.Position, ap) < 2.5 * Nm && !FlightPlanner.IsOnFinal(rwy, p.Position, 0.5 * Nm))
                {
                    penalty += OverflightPenaltyNm;
                    issues.Add(Tr.F("pasa sobre el aeropuerto en {0} ({1}) antes de la final", (p.Name.Length > 0 ? p.Name : Tr.T("un punto")), p.Section));
                    break;
                }
            }
        }
        return (penalty, issues);
    }


    private static List<GeoPoint> Flatten(ProcedureShape shape)
    {
        var list = new List<GeoPoint>();
        foreach (var seg in shape.Segments)
            list.AddRange(seg.Points);
        return list;
    }
}
