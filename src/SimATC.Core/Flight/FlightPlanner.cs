using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;
using SimATC.Core.Units;

namespace SimATC.Core.Flight;

/// <summary>
/// Rutas de los vuelos IA (etapas 6 y 7). Los procedimientos salen de la API de facilities (ProcedureGeometry); en ruta va
/// directo por ortodrómica hasta que se verifiquen las aerovías (RF-20b, V-21 a V-25).
/// </summary>
public static class FlightPlanner
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    /// <summary>Recorrido mínimo en la pista antes de la salida, por categoría (m). APROXIMADO.</summary>
    public static double MinRolloutM(PerformanceCategory c) => c switch
    {
        PerformanceCategory.Turboprop => 900,
        PerformanceCategory.HeavyJet => 1900,
        _ => 1400,
    };

    /// <summary>
    /// Salida: la SID de la pista cuyo final apunta más hacia el destino (con su transición si la hay) y después directo.
    /// Sin SID, 5 NM por el rumbo de pista y directo. Devuelve también el nombre de la SID (o null).
    /// </summary>
    public static (FlightPath Path, string? Sid) Departure(AirportData origin, string runway, GeoPoint destination, string destinationName,
        IReadOnlyList<PathInput>? enroute = null, string? routeText = null)
    {
        var bearing = GeoMath.InitialBearingDeg(origin.Reference, destination);
        ProcedureShape? best = null;
        string? bestName = null;
        var bestScore = double.MaxValue;
        foreach (var sid in origin.Departures)
        {
            if (!ProcedureGeometry.SidFitsRunway(sid, origin, runway))
                continue;
            var transitions = sid.EnrouteTransitions.Count == 0 ? [null] : sid.EnrouteTransitions.Select(t => (string?)t.Name).ToList();
            foreach (var t in transitions)
            {
                var shape = ProcedureGeometry.Sid(sid, origin, runway, t);
                var pts = Flatten(shape);
                if (pts.Count < 2)
                    continue;
                var exit = pts[^1];
                var score = Math.Abs(GeoMath.AngleDiffDeg(bearing, GeoMath.InitialBearingDeg(origin.Reference, exit)));
                // Una SID que se aleja del destino no sirve (hacia atrás más de 90°).
                if (score < bestScore)
                {
                    bestScore = score;
                    best = shape;
                    bestName = t is null ? sid.Name : $"{sid.Name}.{t}";
                }
            }
        }

        var points = new List<PathInput>();
        if (best is not null && bestScore <= 120)
        {
            points.AddRange(Inputs(best, PathSection.Sid));
        }
        else if (ProcedureGeometry.FindRunwayEnd(origin, runway) is { } end)
        {
            bestName = null;
            points.Add((end.LiftoffEnd, "", PathSection.Sid));
            points.Add((GeoMath.Destination(end.LiftoffEnd, end.HeadingDeg, 5 * Nm), "", PathSection.Sid));
        }
        if (enroute is not null)
            points.AddRange(enroute);
        points.Add((destination, destinationName, PathSection.Enroute));
        return (new FlightPath(RoundCorners(points)) { SidName = bestName, RouteText = routeText }, bestName);
    }

    /// <summary>
    /// Nivel de crucero según la distancia (6000 ft + 100 ft por NM: FL160 a 100 NM, FL310 a 250 NM; entre 10.000 ft y el
    /// techo de la categoría) y nunca bajo <paramref name="minimumFt"/> (la mayor altitud mínima de la ruta), redondeado al
    /// nivel semicircular. APROXIMADO: un vuelo corto no sube a FL350.
    /// </summary>
    public static double CruiseAltitude(PerformanceCategory category, double distanceNm, double? trackDeg = null, double minimumFt = 0)
    {
        var ceiling = AirborneProfile.For(category).CruiseAltFt;
        var wanted = Math.Max(Math.Clamp(6000 + distanceNm * 100, 10_000, ceiling), Math.Min(minimumFt, ceiling));
        var fl = (int)Math.Ceiling(wanted / 1000 - 0.25);
        // Regla semicircular (OACI, Anexo 2 apéndice 3): rumbo 000-179 niveles impares (FL350), 180-359 pares (FL360).
        if (trackDeg is { } t && fl <= 41)
        {
            var east = GeoMath.NormalizeDeg(t) < 180;
            if (east != (fl % 2 == 1))
                fl += (fl - 1) * 1000 >= minimumFt ? -1 : 1; // nunca bajo la mínima de la ruta
        }
        return fl * 1000.0;
    }

    /// <summary>Directo por ortodrómica (vuelos de paso y destinos que no son el aeropuerto cargado).</summary>
    public static FlightPath Direct(GeoPoint from, GeoPoint to, string toName, double? trackDeg = null)
    {
        var points = new List<PathInput>();
        foreach (var p in trackDeg is { } t ? TurnFrom(from, t, to, 5000) : [from])
            points.Add(new PathInput(p, "", PathSection.Enroute));
        points.Add(new PathInput(to, toName, PathSection.Enroute));
        return new FlightPath(points);
    }

    /// <summary>
    /// Giro desde el rumbo actual hacia <paramref name="next"/>, como el "direct to" del FMS: sigue un poco por su rumbo y
    /// gira con un arco (con un paso lateral si el giro es de más de 110°). Devuelve los puntos sin <paramref name="next"/>.
    /// </summary>
    public static List<GeoPoint> TurnFrom(GeoPoint from, double trackDeg, GeoPoint next, double radiusM)
    {
        var toNext = GeoMath.InitialBearingDeg(from, next);
        var turn = GeoMath.AngleDiffDeg(trackDeg, toNext);
        if (Math.Abs(turn) < 15 || GeoMath.DistanceM(from, next) < 3 * radiusM)
            return [from];
        var lead = GeoMath.Destination(from, trackDeg, radiusM);
        List<GeoPoint> corners = Math.Abs(turn) > 110
            ? [from, lead, GeoMath.Destination(lead, trackDeg + Math.Sign(turn) * 90, 2 * radiusM), next]
            : [from, lead, next];
        var arc = RouteGeometry.FlyBy(corners, radiusM);
        return arc.Take(arc.Count - 1).ToList();
    }

    /// <summary>
    /// Desde este giro (°) en un fijo se vuela una lágrima en vez de un viraje: una media vuelta de verdad. Un giro grande de
    /// la carta (128° al final del arco DME de la SAUR4D de LEVC) se vuela como viraje normal (prueba de Marco: rizo en R300R).
    /// </summary>
    public const double ReversalDeg = 150;

    /// <summary>Radio de giro por tramo (25° de alabeo a la velocidad típica de cada tramo), en metros. APROXIMADO.</summary>
    private static double TurnRadiusM(PathSection s) => s switch
    {
        PathSection.Enroute => 5000,
        PathSection.Star or PathSection.Sid => 3500,
        _ => 2200,
    };

    /// <summary>
    /// Cada esquina de la ruta como el arco que el avión vuela (fly-by, como lo dibuja el ND): las esquinas de los tramos
    /// TF de la carta, los vectores y el motor y al aire. Los arcos que ya vienen densos (RF, giros) no se tocan.
    /// </summary>
    public static List<PathInput> RoundCorners(List<PathInput> points)
    {
        if (points.Count < 3)
            return points;
        var result = new List<PathInput>(points.Count * 2) { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var prev = points[i - 1].Position;
            var here = points[i];
            var next = points[i + 1].Position;
            if (GeoMath.DistanceM(prev, here.Position) < 1500 || GeoMath.DistanceM(here.Position, next) < 1500
                || Math.Abs(GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(prev, here.Position), GeoMath.InitialBearingDeg(here.Position, next))) < 8
                || here.Section == PathSection.Final)
            {
                result.Add(here);
                continue;
            }
            var inBearing = GeoMath.InitialBearingDeg(prev, here.Position);
            var turnDeg = GeoMath.AngleDiffDeg(inBearing, GeoMath.InitialBearingDeg(here.Position, next));
            if (Math.Abs(turnDeg) > ReversalDeg)
            {
                // Inversión (viene del sur a una entrada que sigue hacia el sur): pasa el fijo, se abre al lado del giro
                // y vuelve en lágrima, como un viraje reglamentario; nunca una vuelta completa sobre el fijo.
                var r = TurnRadiusM(here.Section);
                var side = Math.Sign(turnDeg);
                var past = GeoMath.Destination(here.Position, inBearing, r);
                var wide = GeoMath.Destination(past, inBearing + side * 90, 2 * r);
                result.Add(here);
                var tear = RouteGeometry.FlyBy([here.Position, past, wide, next], r);
                for (var k = 1; k < tear.Count - 1; k++)
                    result.Add(new PathInput(tear[k], "", here.Section));
                continue;
            }
            var arc = RouteGeometry.FlyBy([prev, here.Position, next], TurnRadiusM(here.Section));
            var mid = (arc.Count - 1) / 2;
            for (var k = 1; k < arc.Count - 1; k++)
                result.Add(k == mid ? here with { Position = arc[k] } : new PathInput(arc[k], "", here.Section));
        }
        result.Add(points[^1]);
        return result;
    }

    /// <summary>
    /// Pista de aterrizaje y salida de pista para un vuelo que llega al aeropuerto cargado: la primera salida después del
    /// recorrido mínimo de su categoría. null si la pista no existe.
    /// </summary>
    public static LandingRunway? LandingFor(AirportData airport, TaxiGraph? graph, string runway, PerformanceCategory category)
    {
        for (var i = 0; i < airport.Runways.Count; i++)
        {
            var r = airport.Runways[i];
            var primary = r.Primary.Ident == runway;
            if (!primary && r.Secondary.Ident != runway)
                continue;
            var end = primary ? r.Primary : r.Secondary;
            var minRoll = Math.Min(MinRolloutM(category), r.LengthM * 0.75);
            var exitAlong = r.LengthM - 150; // sin calles: hasta el final de la pista
            var exits = new List<double>();
            var exitList = new List<RunwayExit>();
            if (graph is not null)
            {
                for (var k = 0; k < graph.RunwayEntries.Count; k++)
                {
                    var e = graph.RunwayEntries[k];
                    if (e.RunwayIndex != i)
                        continue;
                    var fromThreshold = primary ? e.AlongM + r.LengthM / 2 : r.LengthM / 2 - e.AlongM;
                    if (fromThreshold > r.LengthM || fromThreshold < 300)
                        continue;
                    if (DescribeExit(graph, e, k, end.HeadingTrueDeg, fromThreshold) is { } x)
                        exitList.Add(x);
                }
                exitList.Sort((a, b) => a.TurnStartM.CompareTo(b.TurnStartM));
                exits.AddRange(exitList.Select(x => x.TurnStartM));
            }
            var chosen = exitList.FirstOrDefault(x => x.TurnStartM >= minRoll);
            if (chosen == default && exitList.Count > 0)
                chosen = exitList[^1];
            if (chosen != default)
                exitAlong = chosen.TurnStartM;
            var exitPoint = GeoMath.Destination(end.Position, end.HeadingTrueDeg, exitAlong);
            return new LandingRunway(i, primary, end.Ident, end.Position, end.HeadingTrueDeg, r.ElevationFt, exitAlong, exitPoint, exits, r.LengthM)
            {
                ExitList = exitList.Count > 0 ? exitList : null,
                ExitEntry = chosen != default ? chosen.EntryIndex : -1,
                ExitSpeedKt = chosen != default ? chosen.SpeedKt : AirborneAgent.ExitSpeedKt,
            };
        }
        return null;
    }

    /// <summary>
    /// La misma pista solo con las salidas desde las que se llega al puesto sin dar media vuelta en una calle (ADR-171, KMIA
    /// 2026-11-25: AAL1335 salió por una calle que curva al este y volvió al oeste por la misma). Si ninguna sirve después del
    /// recorrido mínimo, queda igual.
    /// </summary>
    public static LandingRunway BestExit(TaxiGraph graph, LandingRunway landing, int stand, double minRollM)
    {
        if (landing.ExitList is not { Count: > 1 } list || stand < 0)
            return landing;
        var good = new List<RunwayExit>(list.Count);
        foreach (var x in list)
        {
            var e = graph.RunwayEntries[x.EntryIndex];
            if (graph.FindRoute(e.EntryNode, graph.ParkingNode(stand)) is { } r && !graph.HasUTurn(r, e.RunwayNode))
                good.Add(x);
        }
        if (good.Count == list.Count)
            return landing;
        foreach (var x in good)
        {
            if (x.TurnStartM >= minRollM)
                return (landing with { ExitList = good }).WithExit(x);
        }
        return landing;
    }

    /// <summary>
    /// Describe una salida de pista para el que aterriza con rumbo <paramref name="landingHeadingDeg"/>: el ángulo entre la
    /// pista y la calle (del nodo del eje a la entrada), el lado y la velocidad a la que se toma (salida rápida ≤ 50°: 30 kt;
    /// hasta 75°: 20 kt; a 90°: 15 kt). Las que vuelven hacia atrás (más de 100°) no sirven para salir. El viraje empieza
    /// antes del nodo, a la distancia de tangencia del giro, para que la curva salga del eje sin saltos. APROXIMADO.
    /// </summary>
    internal static RunwayExit? DescribeExit(TaxiGraph graph, RunwayEntry e, int index, double landingHeadingDeg, double nodeAlongM)
    {
        var node = graph.Position(e.RunwayNode);
        var entry = graph.Position(e.EntryNode);
        if (LocalPoint.Distance(node, entry) < 5)
            return null;
        var bearing = GeoMath.NormalizeDeg(Math.Atan2(entry.EastM - node.EastM, entry.NorthM - node.NorthM) * GeoMath.RadToDeg);
        var turn = GeoMath.AngleDiffDeg(landingHeadingDeg, bearing);
        var angle = Math.Abs(turn);
        if (angle > 100)
            return null;
        var speed = angle <= 50 ? 30.0 : angle <= 75 ? 20.0 : 15.0;
        // Radio de giro a esa velocidad con ~1,5 m/s² lateral (al menos 40 m) y tangencia r·tan(θ/2), al menos 40 m de recta.
        var v = speed * 0.514444;
        var radius = Math.Max(40, v * v / 1.5);
        var lead = Math.Max(40, radius * Math.Tan(angle / 2 * GeoMath.DegToRad));
        return new RunwayExit(nodeAlongM - lead, nodeAlongM, angle, turn >= 0 ? 1 : -1, speed, index);
    }

    /// <summary>
    /// Llegada al aeropuerto cargado desde <paramref name="from"/>: STAR que sirva la pista y entre por el lado más cercano,
    /// aproximación de esa pista (ILS si hay) y final recta hasta el umbral. Sin procedimientos, final de 12 NM.
    /// </summary>
    /// <param name="trackDeg">Rumbo actual del avión (si ya vuela): nunca se elige una STAR cuya entrada le queda detrás.</param>
    /// <param name="origin">Aeropuerto de origen: la STAR se elige también por la dirección de donde viene el vuelo.</param>
    /// <param name="enroute">Fijos por aerovías hasta la entrada de la STAR (ruta en ruta), o null.</param>
    /// <param name="onlyStar">Usar esta STAR (la elegida antes de buscar las aerovías hasta su entrada).</param>
    public static (FlightPath Path, string? Star, string? Approach) Arrival(AirportData airport, LandingRunway rwy, GeoPoint from,
        double? trackDeg = null, GeoPoint? origin = null, IReadOnlyList<PathInput>? enroute = null, string? onlyStar = null,
        string? routeText = null)
    {
        var points = new List<PathInput> { (from, "", PathSection.Enroute) };
        if (enroute is not null)
            points.AddRange(enroute);
        // Cerca del aeropuerto no se vuelve a la entrada de una STAR: Approach da vectores a la final (prueba de Marco:
        // LAN757 daba media vuelta para ir a buscar VTN).
        var fromAirportNm = GeoMath.DistanceM(from, airport.Reference) / Nm;
        var vectors = fromAirportNm < VectorsWithinNm;

        // STAR: la que entra más cerca de donde viene (la entrada pesa más que el largo).
        ProcedureShape? star = null;
        string? starName = null;
        string? starEndFix = null;
        var bestScore = double.MaxValue;
        foreach (var s in airport.Arrivals)
        {
            if (s.RunwayTransitions.Count > 0 && !ProcedureMatch.ServesRunway(s, rwy.Ident))
                continue;
            var transitions = s.EnrouteTransitions.Count == 0 ? [null] : s.EnrouteTransitions.Select(t => (string?)t.Name).ToList();
            foreach (var t in transitions)
            {
                var shape = ProcedureGeometry.Star(s, airport, rwy.Ident, t);
                var pts = Flatten(shape);
                if (pts.Count < 2)
                    continue;
                if (vectors)
                    break;
                if (onlyStar is not null && onlyStar != (t is null ? s.Name : $"{t}.{s.Name}"))
                    continue;
                // Una entrada detrás del avión (según su rumbo) obligaría a dar la vuelta.
                if (trackDeg is { } trk && GeoMath.DistanceM(from, pts[0]) > 5 * Nm
                    && Math.Abs(GeoMath.AngleDiffDeg(trk, GeoMath.InitialBearingDeg(from, pts[0]))) > 100)
                    continue;
                // La ruta total más corta (hasta la entrada, la STAR y de su final al umbral): una STAR que obliga a pasar el
                // aeropuerto y volver queda atrás (prueba de Marco: ARG1412 daba la vuelta en MEBRA).
                var score = GeoMath.DistanceM(from, pts[0]) + ProcedureAdvisor.LengthNm(shape) * Nm + GeoMath.DistanceM(pts[^1], rwy.Threshold);
                // A igual largo, la de su lado: la entrada en la dirección de su origen (SCIE por el sur, Mendoza por el este…).
                if (origin is { } o && GeoMath.DistanceM(o, airport.Reference) > 60 * Nm)
                    score += Math.Abs(GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(airport.Reference, o),
                        GeoMath.InitialBearingDeg(airport.Reference, pts[0]))) / 180 * 30 * Nm;
                // La entrada no puede quedar detrás del avión respecto del aeropuerto (daría la vuelta).
                if (GeoMath.DistanceM(from, airport.Reference) > 30 * Nm
                    && GeoMath.DistanceM(pts[0], airport.Reference) > GeoMath.DistanceM(from, airport.Reference) + 10 * Nm)
                    continue;
                if (score < bestScore)
                {
                    bestScore = score;
                    star = shape;
                    starName = t is null ? s.Name : $"{t}.{s.Name}";
                    starEndFix = shape.Waypoints.Count > 0 ? shape.Waypoints[^1].Ident : null;
                }
            }
        }
        // Sin STAR que sirva: vectores (nunca directo a la final del otro lado).
        if (star is null)
            vectors = true;
        if (star is not null && !vectors)
        {
            points.AddRange(Inputs(star, PathSection.Star));
        }
        else if (vectors)
        {
            starName = null;
            starEndFix = null;
            foreach (var v in VectorsToFinal(rwy, from))
                points.Add((v, "", PathSection.Approach));
        }

        // Aproximación de la pista: ILS primero, luego RNAV/GPS, luego cualquiera. Transición que empiece donde termina la STAR.
        Approach? approach = null;
        foreach (var a in airport.Approaches)
        {
            if (ProcedureMatch.ApproachRunway(a) != rwy.Ident)
                continue;
            if (approach is null || Rank(a.Type) < Rank(approach.Type))
                approach = a;
        }
        string? approachName = null;
        var glideDeg = 3.0;
        if (approach is not null)
        {
            string? transition = null;
            foreach (var t in approach.Transitions)
            {
                if (t.Legs.Count > 0 && t.Legs[0].FixIcao == starEndFix)
                    transition = t.Name;
            }
            var shape = ProcedureGeometry.Approach(approach, airport, transition);
            if (Flatten(shape).Count >= 2)
            {
                approachName = ProcedureMatch.ApproachName(approach);
                // Con vectores, solo la parte de la aproximación que queda en los últimos 12 NM (sin volver atrás).
                var approachPoints = Inputs(shape, PathSection.Approach)
                    .Where(p => !vectors || GeoMath.DistanceM(p.Position, rwy.Threshold) <= 12.5 * Nm).ToList();
                // Si la aproximación empieza del otro lado (la STAR termina al sur y el IAF está al norte), Approach da
                // vectores: viento en cola paralelo a la final y base al inicio de la aproximación (prueba de Marco: SKU80
                // cruzaba sobre la pista de AMB a PADOP).
                if (approachPoints.Count > 0)
                {
                    foreach (var d in DownwindJoin(points[^1].Position, approachPoints[0].Position, rwy))
                        points.Add((d, "", PathSection.Approach));
                }
                points.AddRange(approachPoints);
                // Ángulo de la senda publicado en la carta (si no, 3°).
                foreach (var leg in approach.FinalLegs)
                {
                    if (Math.Abs(leg.VerticalAngleDeg) is >= 2 and <= 4.5 and var angle)
                        glideDeg = angle;
                }
            }
        }

        // Final recta: si la aproximación no deja al avión alineado, se arma desde 12 NM por el eje.
        var reciprocal = GeoMath.NormalizeDeg(rwy.HeadingDeg + 180);
        var lastPoint = points[^1].Position;
        var aligned = approachName is not null && IsOnFinal(rwy, lastPoint, 2 * Nm);
        if (!aligned)
        {
            points.Add((GeoMath.Destination(rwy.Threshold, reciprocal, 12 * Nm), "", PathSection.Approach));
            points.Add((GeoMath.Destination(rwy.Threshold, reciprocal, 8 * Nm), "", PathSection.Final));
        }
        points.Add((rwy.Threshold, "RW" + rwy.Ident, PathSection.Final));

        // Lo que ya está sobre el eje en los últimos 12 NM es la final.
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.Section == PathSection.Approach && GeoMath.DistanceM(p.Position, rwy.Threshold) <= 12 * Nm
                && IsOnFinal(rwy, p.Position, 0.5 * Nm))
                points[i] = p with { Section = PathSection.Final };
        }
        if (trackDeg is { } track && points.Count > 1)
            points = [.. TurnFrom(from, track, points[1].Position, 4000).Select(p => new PathInput(p, "", PathSection.Enroute)), .. points.Skip(1)];
        points = RoundCorners(points);
        var path = new FlightPath(points)
        {
            GlideDeg = glideDeg, StarName = starName, ApproachName = approachName, RouteText = routeText,
            ApproachWord = approach is null || approachName is null ? null : ApproachWord(approach.Type),
        };
        // Perfil de descenso que cumple las restricciones de la STAR y la aproximación; el umbral se cruza a 50 ft.
        path.ComputeDescentProfile(rwy.ElevationFt + AirborneAgent.ThresholdCrossingFt, GlideFtPerNm(glideDeg), 45_000);
        return (path, starName, approachName);
    }

    /// <summary>
    /// Vectores para llegar al inicio de la aproximación <paramref name="target"/> cuando el avión está "del lado de la pista"
    /// (más allá del IAF en el sentido del aterrizaje): viento en cola a 6 NM del eje del lado donde viene, hasta 4 NM antes
    /// del IAF, y base. Vacío si el IAF ya queda por delante (o no está sobre el eje prolongado).
    /// </summary>
    public static List<GeoPoint> DownwindJoin(GeoPoint from, GeoPoint target, LandingRunway rwy)
    {
        var course = rwy.HeadingDeg;
        if (!IsOnFinal(rwy, target, 3 * Nm))
            return [];
        var d = GeoMath.DistanceM(target, from);
        var rel = GeoMath.AngleDiffDeg(course, GeoMath.InitialBearingDeg(target, from)) * Math.PI / 180;
        var along = d * Math.Cos(rel);   // positivo: más allá del IAF en el sentido del aterrizaje
        var cross = d * Math.Sin(rel);   // positivo: a la derecha del eje
        if (along < 2 * Nm)
            return []; // el IAF ya queda por delante (o casi): se entra directo
        var side = GeoMath.NormalizeDeg(course + (cross >= 0 ? 90 : -90));
        var abeamHere = GeoMath.Destination(GeoMath.Destination(target, course, Math.Max(along, 0)), side, 6 * Nm);
        var downwindEnd = GeoMath.Destination(GeoMath.Destination(target, course + 180, 4 * Nm), side, 6 * Nm);
        var baseEnd = GeoMath.Destination(target, course + 180, 4 * Nm);
        return [abeamHere, downwindEnd, baseEnd];
    }

    /// <summary>
    /// La STAR (con su transición) que sirve la pista y empieza en un fijo de <paramref name="routeFixes"/> (el más tardío de
    /// la ruta), como la que trae un plan de vuelo; y ese fijo. null si ninguna.
    /// </summary>
    public static (string Star, string EntryFix)? StarStartingAt(AirportData airport, LandingRunway rwy, IReadOnlyList<string> routeFixes)
    {
        (string, string)? best = null;
        var bestIndex = -1;
        foreach (var s in airport.Arrivals)
        {
            if (s.RunwayTransitions.Count > 0 && !ProcedureMatch.ServesRunway(s, rwy.Ident))
                continue;
            var transitions = s.EnrouteTransitions.Count == 0 ? [null] : s.EnrouteTransitions.Select(t => (string?)t.Name).ToList();
            foreach (var t in transitions)
            {
                var shape = ProcedureGeometry.Star(s, airport, rwy.Ident, t);
                if (shape.Waypoints.Count == 0)
                    continue;
                var index = -1;
                for (var i = 0; i < routeFixes.Count; i++)
                {
                    if (routeFixes[i] == shape.Waypoints[0].Ident)
                        index = i;
                }
                if (index > bestIndex)
                {
                    bestIndex = index;
                    best = (t is null ? s.Name : $"{t}.{s.Name}", shape.Waypoints[0].Ident);
                }
            }
        }
        return best;
    }

    /// <summary>A menos de esto del aeropuerto (NM) una llegada va por vectores en vez de STAR.</summary>
    public const double VectorsWithinNm = 40;

    /// <summary>
    /// Vectores a la final como los daría Approach: si viene por el lado de la aproximación (±60° del eje prolongado),
    /// directo al punto de 12 NM; si no, un tramo de viento en cola por el lado donde está, a 6 NM del eje, y base hasta la
    /// final de 12 NM.
    /// </summary>
    public static List<GeoPoint> VectorsToFinal(LandingRunway rwy, GeoPoint from)
    {
        var reciprocal = GeoMath.NormalizeDeg(rwy.HeadingDeg + 180);
        var p12 = GeoMath.Destination(rwy.Threshold, reciprocal, 12 * Nm);
        var off = GeoMath.AngleDiffDeg(reciprocal, GeoMath.InitialBearingDeg(rwy.Threshold, from));
        if (Math.Abs(off) <= 60 && GeoMath.DistanceM(from, rwy.Threshold) > 10 * Nm)
            return [p12];
        var side = GeoMath.NormalizeDeg(rwy.HeadingDeg + (off > 0 ? -90 : 90));
        // El lado del eje donde está el avión.
        var sideBearing = Math.Abs(GeoMath.AngleDiffDeg(side, GeoMath.InitialBearingDeg(rwy.Threshold, from))) < 90 ? side : GeoMath.NormalizeDeg(side + 180);
        var abeam = GeoMath.Destination(rwy.Threshold, sideBearing, 6 * Nm);
        var downwindEnd = GeoMath.Destination(abeam, reciprocal, 10 * Nm);
        return [abeam, downwindEnd, p12];
    }

    /// <summary>Motor y al aire: rumbo de pista 5 NM subiendo, viraje a un tramo de viento en cola y vuelta a la final de 12 NM.</summary>
    public static FlightPath GoAround(LandingRunway rwy, GeoPoint position)
    {
        var reciprocal = GeoMath.NormalizeDeg(rwy.HeadingDeg + 180);
        var upwind = GeoMath.Destination(rwy.Threshold, rwy.HeadingDeg, 5 * Nm);
        // Viento en cola por la derecha, a 5 NM del eje.
        var side = GeoMath.NormalizeDeg(rwy.HeadingDeg + 90);
        var crosswind = GeoMath.Destination(upwind, side, 5 * Nm);
        var downwind = GeoMath.Destination(GeoMath.Destination(rwy.Threshold, side, 5 * Nm), reciprocal, 8 * Nm);
        var baseTurn = GeoMath.Destination(rwy.Threshold, reciprocal, 12 * Nm);
        return new FlightPath(RoundCorners([
            (position, "", PathSection.Approach),
            (upwind, "", PathSection.Approach),
            (crosswind, "", PathSection.Approach),
            (downwind, "", PathSection.Approach),
            (baseTurn, "", PathSection.Approach),
            (GeoMath.Destination(rwy.Threshold, reciprocal, 8 * Nm), "", PathSection.Final),
            (rwy.Threshold, "RW" + rwy.Ident, PathSection.Final),
        ]));
    }

    /// <summary>¿El punto está sobre la prolongación del eje (antes del umbral), a menos de <paramref name="toleranceM"/>?</summary>
    public static bool IsOnFinal(LandingRunway rwy, GeoPoint p, double toleranceM)
    {
        var d = GeoMath.DistanceM(rwy.Threshold, p);
        if (d < 1)
            return true;
        var b = GeoMath.InitialBearingDeg(rwy.Threshold, p);
        var off = GeoMath.AngleDiffDeg(GeoMath.NormalizeDeg(rwy.HeadingDeg + 180), b) * Math.PI / 180;
        return Math.Cos(off) > 0 && Math.Abs(d * Math.Sin(off)) <= toleranceM;
    }

    /// <summary>Cómo se dice el tipo de aproximación en la autorización ("ILS", "RNP", "VOR"…).</summary>
    public static string ApproachWord(ApproachType t) => t switch
    {
        ApproachType.Ils => "ILS",
        ApproachType.Localizer => "localizer",
        ApproachType.Rnav or ApproachType.Gps => "RNP",
        ApproachType.Vor or ApproachType.VorDme => "VOR",
        ApproachType.Ndb or ApproachType.NdbDme => "NDB",
        _ => "",
    };

    private static int Rank(ApproachType t) => t switch
    {
        ApproachType.Ils => 0,
        ApproachType.Rnav or ApproachType.Gps => 1,
        ApproachType.Localizer => 2,
        _ => 3,
    };

    /// <summary>Pies por NM de una senda de <paramref name="deg"/> grados (3° ≈ 318).</summary>
    public static double GlideFtPerNm(double deg) => Math.Tan(deg * Math.PI / 180) * 6076.12;

    /// <summary>Los puntos de una forma con el nombre y las restricciones de cada fijo de la carta.</summary>
    internal static List<PathInput> Inputs(ProcedureShape shape, PathSection section)
    {
        var list = new List<PathInput>();
        foreach (var p in Flatten(shape))
            list.Add(new PathInput(p, "", section));
        foreach (var w in shape.Waypoints)
        {
            if (w.Approximate || w.Leg is not { } leg)
                continue;
            // El punto de la forma que es ese fijo (la forma pasa exactamente por él).
            var best = -1;
            var bestD = 30.0;
            for (var i = 0; i < list.Count; i++)
            {
                var d = GeoMath.DistanceM(list[i].Position, w.Position);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            if (best < 0)
                continue;
            var (min, max) = leg.AltitudeConstraint switch
            {
                AltitudeConstraint.At => (leg.Altitude1Ft, leg.Altitude1Ft),
                AltitudeConstraint.AtOrAbove => (leg.Altitude1Ft, 0.0),
                AltitudeConstraint.AtOrBelow => (0.0, leg.Altitude1Ft),
                AltitudeConstraint.Between => (Math.Min(leg.Altitude1Ft, leg.Altitude2Ft), Math.Max(leg.Altitude1Ft, leg.Altitude2Ft)),
                _ => (0.0, 0.0),
            };
            var speed = leg.SpeedLimitKt > 0 && leg.SpeedConstraint is SpeedConstraint.At or SpeedConstraint.AtOrBelow ? leg.SpeedLimitKt : 0;
            list[best] = list[best] with { Name = w.Ident, MinAltFt = min, MaxAltFt = max, SpeedMaxKt = speed, Region = leg.FixRegion };
        }
        return list;
    }

    private static List<GeoPoint> Flatten(ProcedureShape shape)
    {
        var list = new List<GeoPoint>();
        foreach (var seg in shape.Segments)
            list.AddRange(seg.Points);
        return list;
    }
}
