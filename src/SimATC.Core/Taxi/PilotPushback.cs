using SimATC.Core.Text;
using SimATC.Core.Engine;
using SimATC.Core.Geo;

namespace SimATC.Core.Taxi;

/// <summary>Hacia dónde va la cola en el giro del pushback (vista desde la cabina).</summary>
public enum PushTurn
{
    Straight,
    TailLeft,
    TailRight,
}

/// <summary>Lo que el piloto ajusta en el planificador (RF-21c): tramo recto, lado y ángulo del giro, radio y tramo final.</summary>
public readonly record struct PushbackParams(double StraightM, PushTurn Turn, double TurnDeg, double RadiusM, double FinalStraightM = 5);

/// <summary>Por qué no se puede hacer el pushback planeado (se dibuja en rojo).</summary>
public enum PushbackProblem
{
    None,
    TooShort,
    RadiusTooSmall,
    TurnTooLarge,
    CrossesRunway,
    /// <summary>La posición final arrastrada no se alcanza con un tramo recto, un giro de ese radio y otro recto.</summary>
    Unreachable,
}

/// <summary>
/// El pushback planeado: la línea por la que pasa el punto de referencia del avión (de la posición actual al final,
/// marcha atrás), el rumbo final de la nariz, si se puede hacer y si termina sobre una calle de rodaje.
/// </summary>
public sealed record PushbackPlan(Polyline Path, double EndHeadingDeg, PushbackProblem Problem, bool EndsOnTaxiway)
{
    public bool CanStart => Problem == PushbackProblem.None;

    /// <summary>Los ajustes con que se hizo (null si es la línea roja de una posición inalcanzable).</summary>
    public PushbackParams? Params { get; init; }

    /// <summary>Lado del giro cuando el plan no salió de <see cref="Params"/> (el del planificador de los IA).</summary>
    public PushTurn? Side { get; init; }

    /// <summary>"Cola a la izquierda, terminas mirando al este (090°)".</summary>
    public string Summary => ((Side ?? Params?.Turn) switch
    {
        PushTurn.TailLeft => Tr.T("Cola a la izquierda"),
        PushTurn.TailRight => Tr.T("Cola a la derecha"),
        _ => Tr.T("Recto hacia atrás"),
    }) + Tr.F(", terminas mirando hacia {0} ({1:000}°)", PilotPushback.Cardinal(EndHeadingDeg), EndHeadingDeg);

    /// <summary>Explicación para la ventana, en español.</summary>
    public string Message => Problem switch
    {
        PushbackProblem.TooShort => "Demasiado corto: alarga el tramo recto.",
        PushbackProblem.RadiusTooSmall => $"Giro demasiado cerrado: el radio mínimo es {PilotPushback.MinRadiusM:0} m.",
        PushbackProblem.TurnTooLarge => $"Giro de más de {PilotPushback.MaxTurnDeg:0}°.",
        PushbackProblem.CrossesRunway => "La maniobra entra a una pista.",
        PushbackProblem.Unreachable => "No se llega a esa posición con este radio: aleja el avión final, cambia su rumbo o el radio.",
        _ => EndsOnTaxiway ? "Se puede hacer. Enter para empezar." : "Se puede hacer, pero termina lejos de una calle de rodaje.",
    };
}

/// <summary>
/// Pushback del piloto (RF-21c): la geometría del plan y su validación. Marcha atrás: el avión se mueve hacia su cola
/// (rumbo + 180°). Con la cola a la izquierda, el movimiento gira en sentido horario y la nariz termina a la derecha del
/// rumbo inicial (rumbo + giro); con la cola a la derecha, al revés.
/// </summary>
public static class PilotPushback
{
    /// <summary>Radio mínimo del giro remolcado para un jet comercial mediano (m). APROXIMADO.</summary>
    public const double MinRadiusM = 15;

    public const double MaxTurnDeg = 180;

    /// <summary>A esta distancia de una calle de rodaje (m), el final se considera "sobre la calle".</summary>
    public const double TaxiwayToleranceM = 25;

    public const double MinLengthM = 5;

    /// <summary>Lo que muestra la ventana al abrirse: 20 m recto y la cola a la izquierda 90° con radio 25 m.</summary>
    public static PushbackParams Default => new(20, PushTurn.TailLeft, 90, 25);

    public static PushbackPlan Plan(LocalPoint start, double headingDeg, PushbackParams p, TaxiGraph? graph)
    {
        var motion = GeoMath.NormalizeDeg(headingDeg + 180);
        var points = new List<LocalPoint> { start };
        var here = start;
        if (p.StraightM > 0)
        {
            here = Polyline.Offset(here, motion, p.StraightM);
            points.Add(here);
        }
        var turn = p.Turn == PushTurn.Straight ? 0 : p.TurnDeg;
        if (turn > 0 && p.RadiusM > 0)
        {
            // Arco de a 5°: el movimiento gira en sentido horario (cola a la izquierda) o antihorario.
            var clockwise = p.Turn == PushTurn.TailLeft;
            var center = Polyline.Offset(here, motion + (clockwise ? 90 : -90), p.RadiusM);
            var radial = motion + (clockwise ? -90 : 90);
            for (var a = 5.0; a < turn + 0.01; a += 5)
                points.Add(Polyline.Offset(center, radial + (clockwise ? a : -a), p.RadiusM));
            if (turn % 5 > 0.01)
                points.Add(Polyline.Offset(center, radial + (clockwise ? turn : -turn), p.RadiusM));
            here = points[^1];
            motion = GeoMath.NormalizeDeg(motion + (clockwise ? turn : -turn));
        }
        if (p.FinalStraightM > 0)
            points.Add(Polyline.Offset(here, motion, p.FinalStraightM));

        var path = new Polyline(Polyline.Dedup(points));
        var endHeading = GeoMath.NormalizeDeg(motion + 180);
        var problem = path.Length < MinLengthM ? PushbackProblem.TooShort
            : turn > MaxTurnDeg ? PushbackProblem.TurnTooLarge
            : turn > 0 && p.RadiusM < MinRadiusM ? PushbackProblem.RadiusTooSmall
            : graph is not null && CrossesRunway(graph, path) ? PushbackProblem.CrossesRunway
            : PushbackProblem.None;
        return new PushbackPlan(path, endHeading, problem, graph is null || NearTaxiway(graph, path.End)) { Params = p };
    }

    /// <summary>
    /// El plan que deja el avión en <paramref name="end"/> mirando a <paramref name="endHeadingDeg"/> (lo que el piloto
    /// arrastra con el mouse): tramo recto, un giro de radio <paramref name="radiusM"/> y tramo final. Con el giro fijado por
    /// los rumbos, los dos tramos rectos salen de un sistema lineal de 2×2; si alguno da negativo, no se alcanza (null).
    /// </summary>
    public static PushbackParams? Solve(LocalPoint start, double headingDeg, LocalPoint end, double endHeadingDeg, double radiusM)
    {
        var m0 = GeoMath.NormalizeDeg(headingDeg + 180);
        var m1 = GeoMath.NormalizeDeg(endHeadingDeg + 180);
        var turn = GeoMath.AngleDiffDeg(m0, m1); // positivo: el movimiento gira en sentido horario (cola a la izquierda)
        var dE = end.EastM - start.EastM;
        var dN = end.NorthM - start.NorthM;
        if (Math.Abs(turn) < 1)
        {
            // Sin giro: solo si el final está sobre la línea recta hacia atrás.
            var along = dE * Math.Sin(m0 * GeoMath.DegToRad) + dN * Math.Cos(m0 * GeoMath.DegToRad);
            var across = dE * Math.Cos(m0 * GeoMath.DegToRad) - dN * Math.Sin(m0 * GeoMath.DegToRad);
            return along > 0 && Math.Abs(across) < 2 ? new PushbackParams(along, PushTurn.Straight, 0, radiusM, 0) : null;
        }
        if (Math.Abs(Math.Abs(turn) - 180) < 1)
            return null;
        // Desplazamiento del arco: del centro al inicio del arco y del centro al final.
        var clockwise = turn > 0;
        var arcE = radiusM * (Sin(m0 + (clockwise ? 90 : -90)) + Sin(m0 + (clockwise ? -90 + turn : 90 + turn)));
        var arcN = radiusM * (Cos(m0 + (clockwise ? 90 : -90)) + Cos(m0 + (clockwise ? -90 + turn : 90 + turn)));
        // (dE, dN) − arco = L1·u0 + L2·u1
        double ux0 = Sin(m0), uy0 = Cos(m0), ux1 = Sin(m1), uy1 = Cos(m1);
        var rx = dE - arcE;
        var ry = dN - arcN;
        var det = ux0 * uy1 - ux1 * uy0;
        var l1 = (rx * uy1 - ux1 * ry) / det;
        var l2 = (ux0 * ry - rx * uy0) / det;
        if (l1 < -0.5 || l2 < -0.5)
            return null;
        return new PushbackParams(Math.Max(0, l1), clockwise ? PushTurn.TailLeft : PushTurn.TailRight, Math.Abs(turn), radiusM, Math.Max(0, l2));
    }

    /// <summary>Lo que se dibuja cuando la posición arrastrada no se alcanza: una línea directa, en rojo.</summary>
    public static PushbackPlan Unreachable(LocalPoint start, LocalPoint end, double endHeadingDeg) =>
        new(new Polyline(LocalPoint.Distance(start, end) > 0.1 ? [start, end] : [start, new LocalPoint(start.EastM, start.NorthM - 0.1)]),
            GeoMath.NormalizeDeg(endHeadingDeg), PushbackProblem.Unreachable, false);

    private static double Sin(double deg) => Math.Sin(deg * GeoMath.DegToRad);

    private static double Cos(double deg) => Math.Cos(deg * GeoMath.DegToRad);

    /// <summary>Algún punto de la línea dentro del rectángulo de una pista.</summary>
    public static bool CrossesRunway(TaxiGraph graph, Polyline path)
    {
        foreach (var r in graph.Runways)
        {
            var c = graph.Projection.ToLocal(r.Center);
            var h = r.HeadingTrueDeg * GeoMath.DegToRad;
            for (var s = 0.0; s <= path.Length; s += 2)
            {
                var p = path.PointAt(s);
                var dx = p.EastM - c.EastM;
                var dy = p.NorthM - c.NorthM;
                var along = dx * Math.Sin(h) + dy * Math.Cos(h);
                var across = dx * Math.Cos(h) - dy * Math.Sin(h);
                if (Math.Abs(along) <= r.LengthM / 2 && Math.Abs(across) <= r.WidthM / 2)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pushback automático: prueba combinaciones de tramo recto, lado, giro, radio y tramo final y se queda con la que
    /// termina sobre una calle de rodaje, alineada con ella (en cualquier sentido), corta y, si se conoce la pista de
    /// salida, mirando hacia donde el rodaje es más corto. Null si ninguna sirve.
    /// </summary>
    public static PushbackPlan? Suggest(TaxiGraph graph, Airports.AirportData airport, LocalPoint start, double headingDeg, string? runway,
        AircraftPerformance? perf = null)
    {
        // Con pista asignada, el mismo planificador que los IA (prueba de Marco 2026-11-09: el automático del piloto era el
        // buscador antiguo): retrocede por la línea de entrada del puesto y termina sobre la calle mirando hacia el rodaje
        // a esa pista. Si no sirve (puesto raro, pista sin entrada), el buscador de combinaciones de abajo.
        if (runway is not null && LikeAi(graph, airport, start, headingDeg, runway, perf ?? AircraftPerformance.MediumJet) is { } ai
            && EndsOnTaxiway(graph, ai))
            return ai;
        var scored = new List<(double Cost, PushbackPlan Plan)>();
        foreach (var turn in new[] { PushTurn.TailLeft, PushTurn.TailRight, PushTurn.Straight })
        {
            foreach (var turnDeg in turn == PushTurn.Straight ? StraightTurn : SuggestTurnsDeg)
            {
                foreach (var radius in turn == PushTurn.Straight ? StraightRadius : SuggestRadiiM)
                {
                    for (var straight = 5.0; straight <= 60; straight += 5)
                    {
                        foreach (var final in new[] { 5.0, 10 })
                        {
                            var plan = Plan(start, headingDeg, new PushbackParams(straight, turn, turnDeg, radius, final), graph);
                            if (!plan.CanStart || NearestTaxiSegment(graph, plan.Path.End) is not { } seg || seg.DistanceM > 8)
                                continue;
                            // Alineado con la calle (en cualquiera de sus dos sentidos).
                            var misalign = Math.Abs(GeoMath.AngleDiffDeg(plan.EndHeadingDeg, seg.BearingDeg));
                            misalign = Math.Min(misalign, 180 - misalign);
                            if (misalign > 20)
                                continue;
                            scored.Add((seg.DistanceM * 4 + misalign * 1.5 + plan.Path.Length * 0.05, plan));
                        }
                    }
                }
            }
        }
        if (scored.Count == 0)
            return null;
        scored.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        if (runway is null)
            return scored[0].Plan;
        // Entre los mejores, el que deja el rodaje más corto hacia la pista (mirando hacia ella, no al revés).
        PushbackPlan? best = null;
        var bestCost = double.MaxValue;
        foreach (var (cost, plan) in scored.Take(12))
        {
            var end = graph.Projection.ToGeo(plan.Path.End);
            if (PilotTaxiRoute.From(graph, airport, end, runway, fromStand: false, headingDeg: plan.EndHeadingDeg) is not { } route)
                continue;
            var local = route.Points.Select(graph.Projection.ToLocal).ToList();
            var total = cost + new Polyline(local).Length * 0.05;
            // El rodaje tiene que empezar hacia adelante: si el primer tramo va hacia la cola, habría que dar la vuelta.
            var first = local.Skip(1).FirstOrDefault(q => LocalPoint.Distance(q, plan.Path.End) > 15);
            if (local.Count > 1 && Math.Abs(GeoMath.AngleDiffDeg(plan.EndHeadingDeg, Polyline.Bearing(plan.Path.End, first))) > 100)
                total += 1000;
            if (total < bestCost)
                (best, bestCost) = (plan, total);
        }
        return best ?? scored[0].Plan;
    }

    /// <summary>
    /// ¿Termina sobre una calle (a menos de 8 m de su eje) y alineado con ella (20° o menos, en cualquier sentido)? Lo que se
    /// le pide a un pushback automático (ADR-134): el de los IA a veces terminaba al costado o cruzado.
    /// </summary>
    public static bool EndsOnTaxiway(TaxiGraph graph, PushbackPlan plan)
    {
        if (NearestTaxiSegment(graph, plan.Path.End) is not { } seg || seg.DistanceM > 8)
            return false;
        var misalign = Math.Abs(GeoMath.AngleDiffDeg(plan.EndHeadingDeg, seg.BearingDeg));
        return Math.Min(misalign, 180 - misalign) <= 20;
    }

    /// <summary>A esta distancia (m) de un puesto, el avión está en él (para el planificador de los IA).</summary>
    public const double StandRadiusM = 60;

    /// <summary>El pushback que haría un IA desde ese puesto hacia esa pista, o null si no hay uno válido.</summary>
    public static PushbackPlan? LikeAi(TaxiGraph graph, Airports.AirportData airport, LocalPoint start, double headingDeg, string runway,
        AircraftPerformance perf)
    {
        if (PilotTaxiRoute.RunwayIndex(airport, runway) is not { } rwy)
            return null;
        var here = graph.Projection.ToGeo(start);
        var stand = -1;
        var bestD = StandRadiusM;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var d = GeoMath.DistanceM(here, airport.Parkings[i].Position);
            if (d < bestD)
                (stand, bestD) = (i, d);
        }
        if (stand < 0)
            return null;
        var agent = DepartureAgent.Plan(0, "PILOT", airport, graph, stand, rwy.Index, rwy.Primary, perf, new DepartureTimings(), 2,
            startPosition: here, startHeadingDeg: headingDeg);
        if (agent?.PushbackPath is not { } path || path.Length < MinLengthM || CrossesRunway(graph, path))
            return null;
        var endHeading = GeoMath.NormalizeDeg(path.HeadingAt(path.Length) + 180);
        var turn = GeoMath.AngleDiffDeg(headingDeg, endHeading); // positivo: la nariz giró a la derecha (cola a la izquierda)
        return new PushbackPlan(path, endHeading, PushbackProblem.None, NearTaxiway(graph, path.End))
        {
            Side = Math.Abs(turn) < 10 ? PushTurn.Straight : turn > 0 ? PushTurn.TailLeft : PushTurn.TailRight,
        };
    }

    private static readonly double[] SuggestTurnsDeg = [45, 60, 75, 90, 105, 120, 135];
    private static readonly double[] SuggestRadiiM = [20, 25, 30];
    private static readonly double[] StraightTurn = [0];
    private static readonly double[] StraightRadius = [25];

    /// <summary>El tramo de calle de rodaje más cercano: distancia y rumbo del tramo.</summary>
    public static (double DistanceM, double BearingDeg)? NearestTaxiSegment(TaxiGraph graph, LocalPoint p)
    {
        (double, double)? best = null;
        var bestD = double.MaxValue;
        foreach (var (a, b, _) in graph.TaxiEdgesNear(p, 100))
        {
            var pa = graph.Position(a);
            var pb = graph.Position(b);
            var d = DistanceToSegment(p, pa, pb);
            if (d < bestD && LocalPoint.Distance(pa, pb) > 1)
            {
                bestD = d;
                best = (d, Polyline.Bearing(pa, pb));
            }
        }
        return best;
    }

    /// <summary>Rumbo en palabras, para la interfaz: "el norte", "el sureste"…</summary>
    public static string Cardinal(double headingDeg) =>
        ((int)Math.Round(GeoMath.NormalizeDeg(headingDeg) / 45) % 8) switch
        {
            0 => Tr.T("el norte"),
            1 => Tr.T("el noreste"),
            2 => Tr.T("el este"),
            3 => Tr.T("el sureste"),
            4 => Tr.T("el sur"),
            5 => Tr.T("el suroeste"),
            6 => Tr.T("el oeste"),
            _ => Tr.T("el noroeste"),
        };

    /// <summary>El punto está a menos de <see cref="TaxiwayToleranceM"/> de un tramo de calle de rodaje.</summary>
    public static bool NearTaxiway(TaxiGraph graph, LocalPoint p)
    {
        foreach (var (a, b, _) in graph.TaxiEdgesNear(p, 150))
        {
            if (DistanceToSegment(p, graph.Position(a), graph.Position(b)) <= TaxiwayToleranceM)
                return true;
        }
        return false;
    }

    private static double DistanceToSegment(LocalPoint p, LocalPoint a, LocalPoint b)
    {
        var dx = b.EastM - a.EastM;
        var dy = b.NorthM - a.NorthM;
        var len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * dx + (p.NorthM - a.NorthM) * dy) / len2, 0, 1) : 0;
        return LocalPoint.Distance(p, new LocalPoint(a.EastM + dx * t, a.NorthM + dy * t));
    }
}

/// <summary>
/// Orden para el remolque del simulador: la velocidad de giro del avión que escribe LUAW (°/s; positivo = la nariz
/// gira en sentido horario, la cola va a la izquierda). El remolque empuja a velocidad fija.
/// </summary>
public readonly record struct TugCommand(double YawRateDegS);

/// <summary>
/// Guía el pushback por la línea planeada. El remolque del simulador empuja recto a velocidad fija (~2.4 kt) y no se
/// puede dirigir por SimConnect (V-27); lo que sí se puede es escribir el giro del avión (<c>ROTATION VELOCITY BODY Y</c>).
/// Se compara el rumbo de la nariz con el deseado (persecución pura marcha atrás: la nariz al revés de la dirección hacia
/// un punto adelantado de la línea) y se pide un giro proporcional al error, con tope. El simulador gira bastante más
/// de lo pedido (×3 en V-27), por eso se controla mirando el rumbo y no con un valor fijo. Se suelta 2.5 m antes del final.
/// </summary>
public sealed class PushbackGuide(Polyline path)
{
    /// <summary>Distancia al punto adelantado (m).</summary>
    public const double LookaheadM = 8;

    /// <summary>Velocidad del remolque del simulador (kt), medida en V-27.</summary>
    public const double NativeSpeedKt = 2.4;

    /// <summary>Lo que recorre el remolque después de soltarlo (m), medido en V-27: se suelta a esta distancia del final.</summary>
    public const double StopAheadM = 2.5;

    /// <summary>
    /// Giro pedido por grado de error (°/s por °) y tope (°/s). El simulador gira unas 3 veces lo escrito (V-27): con 1,5 °/s
    /// el avión gira hasta ~4,5 °/s, de sobra para un radio de 15 m a 2,4 kt, y el remolque del simulador (que no sigue giros
    /// bruscos) se ve menos despegado de la rueda de nariz (prueba de Marco 2026-11-06; antes 3 °/s).
    /// </summary>
    public const double GainPerDeg = 0.3;
    public const double MaxYawRateDegS = 1.5;

    public Polyline Path { get; } = path;

    /// <summary>Lo recorrido sobre la línea (m).</summary>
    public double Progress { get; private set; }

    public double Remaining => Path.Length - Progress;

    /// <summary>Avanza lo recorrido según la posición (la proyección se busca cerca de donde iba: la línea puede volver
    /// cerca de sí misma en un giro amplio).</summary>
    public void Track(LocalPoint position) =>
        Progress = Math.Max(Progress, Path.ProjectWithin(position, Math.Max(0, Progress - 3), Math.Min(Path.Length, Progress + 20), out _));

    /// <summary>La orden para esta posición y rumbo de la nariz (grados verdaderos), o null si hay que soltarlo.</summary>
    public TugCommand? Next(LocalPoint position, double headingDeg)
    {
        Track(position);
        if (Remaining <= StopAheadM)
            return null;
        var ahead = Progress + LookaheadM;
        var target = ahead <= Path.Length
            ? Path.PointAt(ahead)
            : Polyline.Offset(Path.End, Path.HeadingAt(Path.Length), ahead - Path.Length);
        var desired = GeoMath.NormalizeDeg(Polyline.Bearing(position, target) + 180);
        var error = GeoMath.AngleDiffDeg(headingDeg, desired);
        return new TugCommand(Math.Clamp(error * GainPerDeg, -MaxYawRateDegS, MaxYawRateDegS));
    }
}
