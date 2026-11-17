using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>Qué cabeceras se usan para aterrizar y cuáles para despegar, y por qué.</summary>
public sealed record RunwayPlan(IReadOnlyList<string> Arrival, IReadOnlyList<string> Departure, string Reason)
{
    /// <summary>¿Llegadas y salidas en pistas distintas?</summary>
    public bool Segregated => !Arrival.Intersect(Departure).Any();

    /// <summary>
    /// Pistas de reserva sin usar (índices del aeropuerto) de un par que no se usa a la vez con una en uso: sirven de calle
    /// de rodaje (ADR-160, Gatwick 08L/26R).
    /// </summary>
    public IReadOnlyList<int> Standby { get; init; } = [];
}

/// <summary>
/// Reparto automático de pistas para cualquier aeropuerto (ADR-091). Con las cabeceras aptas para aviones de línea que el
/// viento permite (rumbo a menos de 60° de la pista en uso, cola y cruzado dentro de los límites), prueba todos los repartos
/// de cada pista en {sin usar, llegadas, salidas, ambas} y se queda con el de menor costo:
/// <list type="bullet">
/// <item>rodaje medio de las puertas a la pista de salida más cercana y de la zona de salidas de la pista de llegada a las
///   puertas, por el grafo de rodaje (cruzar otra pista cuesta 1500 m: es un cruce que hay que autorizar);</item>
/// <item>2500 m por cada pista con llegadas y salidas a la vez (operación mixta: colas y motor y al aire);</item>
/// <item>300 m por pista en uso (más pistas, más complejidad);</item>
/// </list>
/// y nunca dos pistas que se cruzan a la vez. Un aeropuerto de una pista la usa para todo. Prueba de Marco en LEMD
/// (2026-11-07): con 18L/18R para todo hubo bloqueos y 12 motor y al aire; Barajas opera segregado.
/// Se calcula solo al cambiar la pista en uso (no en el tick).
/// </summary>
public static class RunwayPlanner
{
    public const double CrossRunwayPenaltyM = 1500;
    public const double MixedPenaltyM = 2500;
    public const double RunwayInUsePenaltyM = 300;

    /// <summary>
    /// Llegadas más salidas por hora hasta las que una pista mixta es lo normal (ADR-137): con el flujo bajo (ADR-130) cabe
    /// de sobra. APROXIMADO.
    /// </summary>
    public const double LowDemandMovementsPerHour = 24;

    /// <summary>
    /// Con mucha demanda, ¿se consideran repartos con una pista mixta además de los segregados? (ADR-138: una de salidas y
    /// otra mixta en un pico de salidas). La mixta paga <see cref="MixedPenaltyM"/>: solo gana si la capacidad lo justifica.
    /// </summary>
    public static bool PeakMixedAllowed { get; set; } = true;

    /// <summary>Con poca demanda, cada pista en uso cuesta más: se prefieren menos pistas (una o dos mixtas). APROXIMADO.</summary>
    public const double LowDemandRunwayInUsePenaltyM = 900;

    /// <summary>Costo de una pista mixta con poca demanda (m de rodaje equivalentes); con mucha, <see cref="MixedPenaltyM"/>.</summary>
    public const double LowDemandMixedPenaltyM = 400;

    /// <summary>
    /// Costo de que la pista del plan quede mixta con demanda normal (m de rodaje equivalentes): menos que una mixta cualquiera,
    /// para que gane cuando dar vuelta los roles saldría caro (LEMG: la 13 mixta) y no cuando cambiar de pista es barato
    /// (EGLL: salidas por la 09R y llegadas por la 09L). APROXIMADO, ajustado con el Headless.
    /// </summary>
    public const double PlanMixedPenaltyM = 1300;

    /// <summary>Lo que pesa a favor la pista del plan del piloto en su rol (m de rodaje equivalentes). APROXIMADO.</summary>
    public const double PlanBonusM = 1500;
    public const double MaxOffCurrentDeg = 60;
    private const double Unreachable = 20_000;

    /// <summary>
    /// De esas cabeceras, las que tienen alguna aproximación publicada en los datos del simulador; si ninguna la tiene, todas.
    /// Para la llegada del piloto (ADR-129): en LEMD las 14 no tienen aproximación (solo se despega por ellas) y no se le
    /// ofrecen para aterrizar. Regla general, sin datos por aeropuerto.
    /// </summary>
    public static IReadOnlyList<string> WithApproach(AirportData airport, IReadOnlyList<string> ends)
    {
        var with = new List<string>();
        foreach (var end in ends)
        {
            foreach (var a in airport.Approaches)
            {
                if (string.Equals(SimATC.Core.Plan.ProcedureMatch.ApproachRunway(a), end, StringComparison.OrdinalIgnoreCase))
                {
                    with.Add(end);
                    break;
                }
            }
        }
        return with.Count > 0 ? with : ends;
    }

    /// <summary>
    /// Las cabeceras para aterrizar cuando las elegidas por el viento no tienen ninguna aproximación publicada: las del
    /// aeropuerto que sí la tienen y quedan a menos de 90° del viento elegido (en LEMD con las 14 en uso: 18L y 18R). Vacío si
    /// las elegidas ya sirven o no hay otras.
    /// </summary>
    public static IReadOnlyList<string> ArrivalAlternatives(AirportData airport, IReadOnlyList<string> ends)
    {
        if (ends.Count == 0 || !ReferenceEquals(WithApproach(airport, ends), ends) || HasApproach(airport, ends[0]))
            return [];
        if (Find(airport, ends[0]) is not { } current)
            return [];
        var list = new List<(string Ident, double Off)>();
        foreach (var r in airport.Runways)
        {
            foreach (var e in new[] { r.Primary, r.Secondary })
            {
                var off = Math.Abs(Geo.GeoMath.AngleDiffDeg(current.End.HeadingTrueDeg, e.HeadingTrueDeg));
                if (off < 90 && HasApproach(airport, e.Ident))
                    list.Add((e.Ident, off));
            }
        }
        list.Sort((a, b) => a.Off.CompareTo(b.Off));
        return [.. list.Select(x => x.Ident)];
    }

    /// <summary>¿Esa cabecera tiene una aproximación con localizador (ILS, LOC, LDA, SDF)? La pista principal la suele tener.</summary>
    private static bool HasLocalizer(AirportData airport, string end)
    {
        foreach (var a in airport.Approaches)
        {
            if (a.Type is ApproachType.Ils or ApproachType.Localizer or ApproachType.Lda or ApproachType.Sdf
                && string.Equals(SimATC.Core.Plan.ProcedureMatch.ApproachRunway(a), end, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Costo de usar una pista de reserva (m de rodaje equivalentes, ADR-160): si otra pista que no se puede usar a la vez es
    /// la principal (tiene localizador y esta no, o es un 20 % más larga con el mismo equipamiento), se usa esa aunque el rodaje
    /// sea algo más largo (Gatwick: 26L y no 26R, la de reserva, más cerca de las terminales).
    /// </summary>
    public const double SecondaryRunwayPenaltyM = 3000;

    /// <summary>Una pista es un 20 % más larga que otra para contar como la principal del par.</summary>
    public const double MainRunwayLengthRatio = 1.2;

    private static bool HasApproach(AirportData airport, string end)
    {
        foreach (var a in airport.Approaches)
        {
            if (string.Equals(SimATC.Core.Plan.ProcedureMatch.ApproachRunway(a), end, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <param name="currentEnds">Las cabeceras que eligió el selector por el viento (siempre candidatas).</param>
    /// <param name="arrivalsPerHour">Demanda de llegadas por hora (y <paramref name="departuresPerHour"/> de salidas): con más
    /// de lo que una pista absorbe se abren más pistas para ese rol.</param>
    /// <param name="planDeparture">La pista de salida del plan del piloto en este aeropuerto (o null): pesa a favor de que sea
    /// de salidas (ADR-137), sin forzarla.</param>
    /// <param name="planArrival">La pista de llegada del plan del piloto en este aeropuerto (o null): pesa a favor de que sea
    /// de llegadas.</param>
    public static RunwayPlan Plan(AirportData airport, TaxiGraph graph, IReadOnlyList<string> currentEnds, double windFromTrueDeg = 0,
        double windKt = 0, RunwaySelectionLimits? limits = null, double arrivalsPerHour = 0, double departuresPerHour = 0,
        bool dependentParallels = false, string? planDeparture = null, string? planArrival = null)
    {
        limits ??= RunwaySelectionLimits.Default;
        // En EE. UU. la FAA opera paralelas cercanas en modo dependiente (Newark 04R llegadas y 04L salidas, ADR-145).
        var faaParallels = Comms.AtcWords.IsUnitedStates(airport.Icao);
        var current = Find(airport, currentEnds.FirstOrDefault());
        if (current is null)
            return new RunwayPlan(currentEnds, currentEnds, Tr.T("sin datos de pistas"));
        var heading = current.Value.End.HeadingTrueDeg;
        var airline = PlannerRunwayIndices(airport);
        // Por pista apta, la cabecera en el sentido del flujo (si el viento la permite).
        var cands = new List<(int Runway, bool Primary, RunwayEnd End)>();
        foreach (var r in airline)
        {
            var rw = airport.Runways[r];
            var (end, primary) = Math.Abs(GeoMath.AngleDiffDeg(rw.Primary.HeadingTrueDeg, heading)) <= Math.Abs(GeoMath.AngleDiffDeg(rw.Secondary.HeadingTrueDeg, heading))
                ? (rw.Primary, true) : (rw.Secondary, false);
            if (Math.Abs(GeoMath.AngleDiffDeg(end.HeadingTrueDeg, heading)) > MaxOffCurrentDeg)
                continue;
            var w = WindComponents.For(end.HeadingTrueDeg, windFromTrueDeg, windKt);
            var isCurrent = currentEnds.Contains(end.Ident);
            if (!isCurrent && windKt >= limits.CalmKt && (w.TailwindKt > limits.MaxTailwindKt || w.CrosswindKt > limits.MaxCrosswindKt))
                continue;
            cands.Add((r, primary, end));
        }
        // Lo que permite el escenario en cada cabecera (ADR-137): sin despegues no es de salidas, sin aterrizajes no es de
        // llegadas, cerrada no se usa. Solo si el aeropuerto trae alguna cabecera para cada cosa (si no, el dato no sirve).
        var masks = new int[cands.Count];
        var anyTakeoff = cands.Exists(c => !c.End.Closed && c.End.TakeoffAllowed);
        var anyLanding = cands.Exists(c => !c.End.Closed && c.End.LandingAllowed);
        // Para llegar hace falta una aproximación publicada en esa cabecera, si alguna de las candidatas la tiene (en LEMD las
        // 14 son solo de despegue: sin esto, con poca demanda salía "14R mixta").
        var anyApproach = cands.Exists(c => HasApproach(airport, c.End.Ident));
        for (var k = 0; k < cands.Count; k++)
        {
            var e = cands[k].End;
            masks[k] = !anyTakeoff || !anyLanding ? 3 : e.Closed ? 0 : (e.LandingAllowed ? 1 : 0) | (e.TakeoffAllowed ? 2 : 0);
            if (anyApproach && !HasApproach(airport, e.Ident))
                masks[k] &= 2;
        }
        for (var k = cands.Count - 1; k >= 0 && cands.Count > 1; k--)
        {
            if (masks[k] == 0)
            {
                cands.RemoveAt(k);
                masks = [.. masks[..k], .. masks[(k + 1)..]];
            }
        }
        if (cands.Count == 0)
            return new RunwayPlan(currentEnds, currentEnds, Tr.T("sin otras pistas aptas"));
        if (cands.Count == 1)
            return new RunwayPlan([cands[0].End.Ident], [cands[0].End.Ident], Tr.T("una sola pista apta: llegadas y salidas"));

        var gates = new List<int>();
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            if (airport.Parkings[i].Type is ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy or ParkingType.GateExtra)
                gates.Add(graph.ParkingNode(i));
        }
        if (gates.Count == 0)
        {
            for (var i = 0; i < airport.Parkings.Count; i++)
                gates.Add(graph.ParkingNode(i));
        }
        var n = cands.Count;
        // Centro de los puestos de pasajeros: la terminal, para saber cuál de un par de paralelas es la interior.
        double gx = 0, gy = 0;
        var gn = 0;
        foreach (var p in airport.Parkings)
        {
            if (p.Type is not (ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy or ParkingType.GateExtra))
                continue;
            var lp = graph.Projection.ToLocal(p.Position);
            gx += lp.EastM;
            gy += lp.NorthM;
            gn++;
        }
        var terminal = gn > 0 ? new LocalPoint(gx / gn, gy / gn) : (LocalPoint?)null;
        var dep = new double[n][];
        var arr = new double[n][];
        for (var k = 0; k < n; k++)
        {
            var (r, primary, _) = cands[k];
            var len = airport.Runways[r].LengthM;
            var threshold = primary ? -len / 2 : len / 2;
            var dir = primary ? 1 : -1;
            // Salidas: las entradas cerca del umbral; llegadas: las salidas entre 1200 m del umbral y 300 m del final.
            var depSrc = graph.RunwayEntries.Where(e => e.RunwayIndex == r && Math.Abs(e.AlongM - threshold) < 800).Select(e => e.EntryNode).ToList();
            var arrSrc = graph.RunwayEntries.Where(e => e.RunwayIndex == r)
                .Where(e => (e.AlongM - threshold) * dir is var s && s > 1200 && s < len - 300).Select(e => e.EntryNode).ToList();
            dep[k] = GateCosts(graph, depSrc, r, gates);
            arr[k] = GateCosts(graph, arrSrc.Count > 0 ? arrSrc : depSrc, r, gates);
        }
        // Pares de pistas que se cruzan: no se usan a la vez. Paralelas a menos de 760 m (ADR-118): solo en modo dependiente,
        // una para llegadas y la otra para salidas (como SFO o Incheon); nunca las dos para lo mismo ni mixtas.
        var cross = new bool[n, n];
        var close = new bool[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                cross[i, j] = cross[j, i] = Intersect(airport, graph, cands[i].Runway, cands[j].Runway);
                var near = !cross[i, j] && CloseParallels(airport, graph, cands[i].Runway, cands[j].Runway);
                // Modo dependiente: a pedido (ADR-118) o con la FAA, si están al menos a 700 ft (213 m) entre ejes.
                var dependent = dependentParallels
                                || faaParallels && ParallelLateralM(airport, graph, cands[i].Runway, cands[j].Runway) >= FaaMinDependentM;
                // Sin el modo dependiente: como antes, no se usan a la vez.
                if (near && !dependent)
                    cross[i, j] = cross[j, i] = true;
                close[i, j] = close[j, i] = near && dependent;
            }
        }
        // De un par que no se puede usar a la vez, ¿esta es la de reserva? (ADR-160)
        var secondary = new bool[n];
        var localizer = new bool[n];
        for (var k = 0; k < n; k++)
            localizer[k] = HasLocalizer(airport, cands[k].End.Ident);
        for (var k = 0; k < n; k++)
        {
            for (var j = 0; j < n && !secondary[k]; j++)
            {
                if (j == k || !cross[k, j] || !Parallel(airport, cands[k].Runway, cands[j].Runway))
                    continue;
                var lenK = airport.Runways[cands[k].Runway].LengthM;
                var lenJ = airport.Runways[cands[j].Runway].LengthM;
                secondary[k] = localizer[j] && !localizer[k] || localizer[j] == localizer[k] && lenJ >= lenK * MainRunwayLengthRatio;
            }
        }
        // Según el rol: trayectorias que se acercan (aproximación, motor y al aire, salida) aunque el pavimento no se cruce.
        var conflict = new bool[n, n, 4, 4];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                    continue;
                for (var ri = 1; ri < 4; ri++)
                {
                    for (var rj = 1; rj < 4; rj++)
                        conflict[i, j, ri, rj] = cross[i, j] || close[i, j] && !(ri == 1 && rj == 2 || ri == 2 && rj == 1)
                                                 || Converging(airport, graph, cands[i], ri, cands[j], rj);
                }
            }
        }
        // Una vez, no en cada reparto: ¿las salidas por d quedan detrás de las llegadas por a, mirando desde la terminal?
        var behind = new bool[n, n];
        for (var a = 0; a < n && terminal is { } term; a++)
        {
            for (var d = 0; d < n; d++)
                behind[a, d] = a != d && DeparturesBehindArrivals(airport, graph, term, cands[a].Runway, cands[d].Runway);
        }
        var bestCost = double.MaxValue;
        var best = Array.Empty<int>();
        var roles = new int[n]; // 0 sin usar, 1 llegadas, 2 salidas, 3 ambas
        var total = (int)Math.Pow(4, n);
        // Primero solo repartos segregados (ninguna pista mixta); la operación mixta, solo si no hay ninguno posible. Con poca
        // demanda (ADR-137), la mixta también desde el principio y cuesta poco: una o dos pistas mixtas, como se opera de verdad.
        var lowDemand = arrivalsPerHour + departuresPerHour <= LowDemandMovementsPerHour;
        var mixedPenalty = lowDemand ? LowDemandMixedPenaltyM : MixedPenaltyM;
        var inUsePenalty = lowDemand ? LowDemandRunwayInUsePenaltyM : RunwayInUsePenaltyM;
        for (var pass = 0; pass < 2 && best.Length == 0; pass++)
        for (var code = 1; code < total; code++)
        {
            var c = code;
            var mixed = false;
            for (var k = 0; k < n; k++)
            {
                roles[k] = c % 4;
                mixed |= roles[k] == 3;
                c /= 4;
            }
            // Con mucha demanda, la mixta también se considera desde el principio, pero con su costo alto (ADR-138): gana solo si
            // la capacidad lo justifica, como en un pico de salidas (una pista de salidas y otra mixta).
            if (pass == 0 && mixed && !lowDemand && !OnlyPlanMixed(roles, cands, planDeparture, planArrival)
                && (!PeakMixedAllowed || !DeparturePeakShape(roles, arrivalsPerHour, departuresPerHour))
                || !Valid(roles, conflict) || !Allowed(roles, masks))
                continue;
            var cost = Cost(roles, dep, arr, gates.Count, mixedPenalty, inUsePenalty) + CapacityCost(roles, arrivalsPerHour, departuresPerHour, close);
            // El plan del piloto pesa a favor (ADR-137): su pista de salida, de salidas; la de llegada, de llegadas. La pista del
            // plan puede quedar mixta con el costo bajo (en LEMG la 13 mixta, como de verdad, en vez de dar vuelta los roles). Pero
            // el plan nunca manda llegadas a una pista que el viento no eligió (en LEMG daba "llegadas 12" y caían los despegues).
            var planOk = true;
            for (var k = 0; k < n && (planDeparture is not null || planArrival is not null); k++)
            {
                if ((roles[k] & 1) != 0 && !currentEnds.Contains(cands[k].End.Ident))
                    planOk = false;
            }
            for (var k = 0; k < n && planOk; k++)
            {
                if (roles[k] == 3 && !lowDemand && IsPlan(cands[k].End.Ident, planDeparture, planArrival))
                    cost -= MixedPenaltyM - PlanMixedPenaltyM;
                if ((roles[k] & 2) != 0 && string.Equals(cands[k].End.Ident, planDeparture, StringComparison.OrdinalIgnoreCase))
                    cost -= PlanBonusM;
                if ((roles[k] & 1) != 0 && string.Equals(cands[k].End.Ident, planArrival, StringComparison.OrdinalIgnoreCase))
                    cost -= PlanBonusM;
            }
            // Cada par dependiente en uso cuesta: se abre solo si la demanda o el rodaje lo justifican.
            for (var k = 0; k < n; k++)
            {
                for (var j = k + 1; j < n; j++)
                {
                    if (close[k, j] && roles[k] != 0 && roles[j] != 0)
                        cost += DependentPairPenaltyM;
                }
            }
            // Salidas nunca "detrás" de una pista de llegadas paralela, mirando desde la terminal (ADR-145): para llegar a ella
            // cruzarían la de llegadas, que solo tiene huecos entre aviones en final. Así se aterriza por la exterior y se
            // despega por la interior (KEWR 04R/04L, KATL); con la terminal entre las dos (EGLL), no aplica.
            for (var a = 0; a < n; a++)
            {
                if ((roles[a] & 1) == 0)
                    continue;
                for (var d = 0; d < n; d++)
                {
                    if (behind[a, d] && (roles[d] & 2) != 0)
                        cost += InboardArrivalPenaltyM;
                }
            }
            // La de reserva de un par de paralelas que no se usan a la vez, solo si la principal no sirve (ADR-160).
            for (var k = 0; k < n; k++)
            {
                if (roles[k] != 0 && secondary[k])
                    cost += SecondaryRunwayPenaltyM;
            }
            // Desempate: preferir la pista que eligió el viento para llegar.
            for (var k = 0; k < n; k++)
            {
                if ((roles[k] & 1) != 0 && currentEnds.Contains(cands[k].End.Ident))
                    cost -= 1;
            }
            if (cost < bestCost)
            {
                bestCost = cost;
                best = (int[])roles.Clone();
            }
        }
        var arrivals = new List<string>();
        var departures = new List<string>();
        for (var k = 0; k < n; k++)
        {
            if ((best[k] & 1) != 0)
                arrivals.Add(cands[k].End.Ident);
            if ((best[k] & 2) != 0)
                departures.Add(cands[k].End.Ident);
        }
        arrivals.Sort(StringComparer.Ordinal);
        departures.Sort(StringComparer.Ordinal);
        var standby = new List<int>();
        for (var k = 0; k < n; k++)
        {
            if (best[k] != 0 || !secondary[k])
                continue;
            for (var j = 0; j < n; j++)
            {
                if (best[j] != 0 && cross[k, j] && !standby.Contains(cands[k].Runway))
                    standby.Add(cands[k].Runway);
            }
        }
        var plan = new RunwayPlan(arrivals, departures, "") { Standby = standby };
        var planNote = planDeparture is null && planArrival is null ? ""
            : Tr.F("; plan: {0}{1}{2}", (planDeparture is null ? "" : Tr.F("salida {0}", planDeparture)), (planDeparture is not null && planArrival is not null ? ", " : ""), (planArrival is null ? "" : Tr.F("llegada {0}", planArrival)));
        var crossing = new List<string>();
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (cross[i, j])
                    crossing.Add($"{cands[i].End.Ident}×{cands[j].End.Ident}");
                else if (close[i, j])
                    crossing.Add($"{cands[i].End.Ident}≈{cands[j].End.Ident}");
                else if (conflict[i, j, 3, 3])
                    crossing.Add($"{cands[i].End.Ident}~{cands[j].End.Ident}");
            }
        }
        var considered = Tr.F("candidatas {0}", string.Join(", ", cands.Select(c => c.End.Ident)))
            + (crossing.Count > 0
                ? Tr.F("; no a la vez ({0}) {1}", Tr.T("× se cruzan o muy juntas, ~ trayectorias convergentes")
                    + (dependentParallels || faaParallels ? Tr.T(", ≈ paralelas cercanas: solo una llegadas y otra salidas") : ""), string.Join(", ", crossing))
                : "");
        return plan with
        {
            Reason = plan.Segregated
                ? Tr.F("automático: llegadas {0}, salidas {1} ({2}{3})", string.Join("/", arrivals), string.Join("/", departures), considered, planNote)
                : MixedText(arrivals, departures) + $" ({considered}{planNote})",
        };
    }

    private static string MixedText(List<string> arrivals, List<string> departures)
    {
        var both = arrivals.Intersect(departures).ToList();
        var onlyArr = arrivals.Except(departures).ToList();
        var onlyDep = departures.Except(arrivals).ToList();
        var text = Tr.F("automático: {0} mixta{1}", string.Join("/", both), (both.Count > 1 ? "s" : ""));
        if (onlyArr.Count > 0)
            text += Tr.F(", llegadas también {0}", string.Join("/", onlyArr));
        if (onlyDep.Count > 0)
            text += Tr.F(", salidas también {0}", string.Join("/", onlyDep));
        return text;
    }

    private static bool Valid(int[] roles, bool[,,,] conflict)
    {
        var anyArr = false;
        var anyDep = false;
        for (var k = 0; k < roles.Length; k++)
        {
            anyArr |= (roles[k] & 1) != 0;
            anyDep |= (roles[k] & 2) != 0;
            for (var j = k + 1; j < roles.Length && roles[k] != 0; j++)
            {
                if (roles[j] != 0 && conflict[k, j, roles[k], roles[j]])
                    return false;
            }
        }
        return anyArr && anyDep;
    }

    /// <summary>Movimientos por hora que absorbe una pista en un solo rol (una mixta, la mitad para cada uno). APROXIMADO.</summary>
    public const double CapacityPerRunwayHour = 28;

    /// <summary>Costo (m) por cada movimiento por hora que no cabe en las pistas de ese rol.</summary>
    public const double ExcessPenaltyM = 400;

    /// <summary>Capacidad de una pista de un par dependiente frente a una independiente (se coordinan). APROXIMADO.</summary>
    public const double DependentCapacityFactor = 0.75;

    /// <summary>Costo (m) de usar un par de paralelas cercanas en modo dependiente (coordinación, motor y al aire paralelos).</summary>
    public const double DependentPairPenaltyM = 2000;

    /// <summary>Costo (m) de aterrizar por la interior de un par dependiente y despegar por la exterior (ADR-145).</summary>
    public const double InboardArrivalPenaltyM = 20000;

    private static double CapacityCost(int[] roles, double arrivalsPerHour, double departuresPerHour, bool[,]? close = null)
    {
        double arrCap = 0, depCap = 0;
        for (var k = 0; k < roles.Length; k++)
        {
            var r = roles[k];
            var dependent = false;
            for (var j = 0; close is not null && j < roles.Length; j++)
                dependent |= j != k && roles[j] != 0 && close[k, j];
            var cap = CapacityPerRunwayHour * (dependent ? DependentCapacityFactor : 1);
            if (r == 1)
                arrCap += cap;
            else if (r == 2)
                depCap += cap;
            else if (r == 3)
            {
                arrCap += cap / 2;
                depCap += cap / 2;
            }
        }
        return (Math.Max(0, arrivalsPerHour - arrCap) + Math.Max(0, departuresPerHour - depCap)) * ExcessPenaltyM;
    }

    /// <summary>
    /// ¿Es el reparto de un pico de salidas (ADR-138)? Más salidas que llegadas, una sola pista mixta y todas las demás en uso
    /// solo de salidas (al menos una). En EDDF, permitir "mixta + otra de llegadas" bajaba los despegues de 11 a 7.
    /// </summary>
    private static bool DeparturePeakShape(int[] roles, double arrivalsPerHour, double departuresPerHour)
    {
        if (departuresPerHour <= arrivalsPerHour)
            return false;
        int mixedCount = 0, departureOnly = 0;
        foreach (var r in roles)
        {
            if (r == 3)
                mixedCount++;
            else if (r == 2)
                departureOnly++;
            else if (r == 1)
                return false;
        }
        return mixedCount == 1 && departureOnly >= 1;
    }

    private static bool IsPlan(string ident, string? planDeparture, string? planArrival) =>
        string.Equals(ident, planDeparture, StringComparison.OrdinalIgnoreCase) || string.Equals(ident, planArrival, StringComparison.OrdinalIgnoreCase);

    /// <summary>¿Las únicas pistas mixtas son las del plan?</summary>
    private static bool OnlyPlanMixed(int[] roles, List<(int Runway, bool Primary, RunwayEnd End)> cands, string? planDeparture, string? planArrival)
    {
        if (planDeparture is null && planArrival is null)
            return false;
        for (var k = 0; k < roles.Length; k++)
        {
            if (roles[k] == 3 && !IsPlan(cands[k].End.Ident, planDeparture, planArrival))
                return false;
        }
        return true;
    }

    private static bool Allowed(int[] roles, int[] masks)
    {
        for (var k = 0; k < roles.Length; k++)
        {
            if ((roles[k] & ~masks[k]) != 0)
                return false;
        }
        return true;
    }

    private static double Cost(int[] roles, double[][] dep, double[][] arr, int gates, double mixedPenalty, double inUsePenalty)
    {
        double sum = 0;
        for (var g = 0; g < gates; g++)
        {
            double d = Unreachable, a = Unreachable;
            for (var k = 0; k < roles.Length; k++)
            {
                if ((roles[k] & 2) != 0)
                    d = Math.Min(d, dep[k][g]);
                if ((roles[k] & 1) != 0)
                    a = Math.Min(a, arr[k][g]);
            }
            sum += d + a;
        }
        var cost = sum / Math.Max(1, gates);
        foreach (var r in roles)
        {
            if (r != 0)
                cost += inUsePenalty;
            if (r == 3)
                cost += mixedPenalty;
        }
        return cost;
    }

    /// <summary>Distancia por el grafo (Dijkstra) desde las entradas de la pista a cada puerta; cruzar otra pista cuesta más.</summary>
    private static double[] GateCosts(TaxiGraph graph, List<int> sources, int ownRunway, List<int> gates)
    {
        var dist = new double[graph.NodeCount];
        Array.Fill(dist, double.MaxValue);
        var queue = new PriorityQueue<int, double>();
        foreach (var s in sources)
        {
            dist[s] = 0;
            queue.Enqueue(s, 0);
        }
        while (queue.TryDequeue(out var u, out var du))
        {
            if (du > dist[u] || graph.IsParking(u))
                continue; // a un puesto se llega, no se pasa por él (ADR-128)
            foreach (var (v, _, _) in graph.Neighbors(u))
            {
                var w = LocalPoint.Distance(graph.Position(u), graph.Position(v));
                if (graph.RunwayAt(v) is >= 0 and var rv && rv != ownRunway && graph.RunwayAt(u) != rv)
                    w += CrossRunwayPenaltyM;
                if (du + w < dist[v])
                {
                    dist[v] = du + w;
                    queue.Enqueue(v, dist[v]);
                }
            }
        }
        var costs = new double[gates.Count];
        for (var i = 0; i < gates.Count; i++)
            costs[i] = dist[gates[i]] == double.MaxValue ? Unreachable : dist[gates[i]];
        return costs;
    }

    /// <summary>Separación mínima (m) entre ejes de paralelas para usarlas a la vez (OACI, operaciones segregadas).</summary>
    public const double MinParallelSeparationM = 760;

    /// <summary>
    /// Pistas que entran al reparto (ADR-093): al menos 1800 m, o el 80 % de la más larga si ninguna llega a eso. Antes se
    /// exigía además el 70 % de la más larga y en JFK o Boston quedaban fuera pistas de 3000 m (una sola pista para todo).
    /// </summary>
    public static List<int> PlannerRunwayIndices(AirportData a)
    {
        var longest = a.Runways.Count == 0 ? 0 : a.Runways.Max(r => r.LengthM);
        var min = Math.Min(ActiveRunwaySelector.AirlineMinLengthM, longest * 0.8);
        return [.. Enumerable.Range(0, a.Runways.Count).Where(i => a.Runways[i].LengthM >= min - 1)];
    }

    /// <summary>
    /// ¿Paralelas (rumbo a menos de 10°) a menos de <see cref="MinParallelSeparationM"/> entre ejes? Entonces no se usan a la
    /// vez, como en Gatwick (ADR-093): se tratan como pistas que se cruzan.
    /// </summary>
    /// <summary>
    /// Separación mínima entre ejes (m) para operar paralelas cercanas en modo dependiente con reglas FAA: 700 ft (FAA Order
    /// JO 7110.65, separación de pistas paralelas para operaciones simultáneas de llegada y salida). APROXIMADO.
    /// </summary>
    public const double FaaMinDependentM = 213;

    /// <summary>
    /// ¿La pista <paramref name="dep"/> queda detrás de la <paramref name="arr"/> mirando desde la terminal? Solo entre pistas
    /// casi paralelas (10°): la de llegadas está entre la terminal y la de salidas, del mismo lado.
    /// </summary>
    private static bool DeparturesBehindArrivals(AirportData airport, TaxiGraph graph, LocalPoint terminal, int arr, int dep)
    {
        if (arr == dep)
            return false;
        var ra = airport.Runways[arr];
        var rd = airport.Runways[dep];
        var diff = Math.Abs(GeoMath.AngleDiffDeg(ra.HeadingTrueDeg, rd.HeadingTrueDeg));
        if (Math.Min(diff, 180 - diff) > 10)
            return false;
        // Normal al rumbo de la pista de llegadas: coordenada lateral con signo de cada cosa.
        var h = ra.HeadingTrueDeg * GeoMath.DegToRad;
        var (nx, ny) = (Math.Cos(h), -Math.Sin(h));
        double Side(LocalPoint p) => p.EastM * nx + p.NorthM * ny;
        var t = Side(terminal);
        var a = Side(graph.Projection.ToLocal(ra.Center)) - t;
        var d = Side(graph.Projection.ToLocal(rd.Center)) - t;
        return Math.Sign(a) == Math.Sign(d) && Math.Abs(a) < Math.Abs(d);
    }

    /// <summary>Distancia lateral (m) entre los ejes de dos pistas casi paralelas.</summary>
    private static double ParallelLateralM(AirportData airport, TaxiGraph graph, int a, int b)
    {
        var ra = airport.Runways[a];
        var ca = graph.Projection.ToLocal(ra.Center);
        var cb = graph.Projection.ToLocal(airport.Runways[b].Center);
        var bearing = Math.Atan2(cb.EastM - ca.EastM, cb.NorthM - ca.NorthM) * GeoMath.RadToDeg;
        return LocalPoint.Distance(ca, cb) * Math.Abs(Math.Sin(GeoMath.AngleDiffDeg(ra.HeadingTrueDeg, bearing) * GeoMath.DegToRad));
    }

    /// <summary>¿Las dos pistas son paralelas (o casi, 10°)?</summary>
    private static bool Parallel(AirportData airport, int a, int b)
    {
        var diff = Math.Abs(GeoMath.AngleDiffDeg(airport.Runways[a].HeadingTrueDeg, airport.Runways[b].HeadingTrueDeg));
        return diff <= 10 || diff >= 170;
    }

    private static bool CloseParallels(AirportData airport, TaxiGraph graph, int a, int b)
    {
        var ra = airport.Runways[a];
        var rb = airport.Runways[b];
        var diff = Math.Abs(GeoMath.AngleDiffDeg(ra.HeadingTrueDeg, rb.HeadingTrueDeg));
        if (Math.Min(diff, 180 - diff) > 10)
            return false;
        var ca = graph.Projection.ToLocal(ra.Center);
        var cb = graph.Projection.ToLocal(rb.Center);
        var d = LocalPoint.Distance(ca, cb);
        var bearing = Math.Atan2(cb.EastM - ca.EastM, cb.NorthM - ca.NorthM) * GeoMath.RadToDeg;
        var lateral = d * Math.Abs(Math.Sin(GeoMath.AngleDiffDeg(ra.HeadingTrueDeg, bearing) * GeoMath.DegToRad));
        return lateral < MinParallelSeparationM;
    }

    /// <summary>Tramo de aproximación final considerado antes del umbral (m). APROXIMADO.</summary>
    public const double ApproachCheckM = 3000;


    /// <summary>Tramo después del final de la pista para el ascenso inicial de una salida (m). APROXIMADO.</summary>
    public const double DepartureCheckM = 3000;

    /// <summary>Distancia mínima (m) entre trayectorias de pistas no paralelas para usarlas a la vez.</summary>
    public const double ConvergingClearM = 300;

    /// <summary>
    /// ¿Las trayectorias de dos pistas no paralelas, según su rol, pasan a menos de <see cref="ConvergingClearM"/>? (ADR-093,
    /// KBOS 22R/27, KMIA 12/08R). Llegada: aproximación + pista; salida: pista + ascenso inicial. El motor y al aire no
    /// cuenta: los procedimientos de frustrada giran (LEMD aterriza por las 18 y despega por las 14). Las paralelas se rigen
    /// por <see cref="MinParallelSeparationM"/>.
    /// </summary>
    private static bool Converging(AirportData airport, TaxiGraph graph, (int Runway, bool Primary, RunwayEnd End) a, int roleA,
        (int Runway, bool Primary, RunwayEnd End) b, int roleB)
    {
        var diff = Math.Abs(GeoMath.AngleDiffDeg(a.End.HeadingTrueDeg, b.End.HeadingTrueDeg));
        if (Math.Min(diff, 180 - diff) <= 10)
            return false;
        Span<(LocalPoint, LocalPoint)> sa = stackalloc (LocalPoint, LocalPoint)[2];
        Span<(LocalPoint, LocalPoint)> sb = stackalloc (LocalPoint, LocalPoint)[2];
        var na = Footprint(airport, graph, a, roleA, sa);
        var nb = Footprint(airport, graph, b, roleB, sb);
        for (var i = 0; i < na; i++)
        {
            for (var j = 0; j < nb; j++)
            {
                if (SegmentDistance(sa[i].Item1, sa[i].Item2, sb[j].Item1, sb[j].Item2) < ConvergingClearM)
                    return true;
            }
        }
        return false;
    }

    private static int Footprint(AirportData airport, TaxiGraph graph, (int Runway, bool Primary, RunwayEnd End) c, int role, Span<(LocalPoint, LocalPoint)> output)
    {
        var t = graph.Projection.ToLocal(c.End.Position);
        var h = c.End.HeadingTrueDeg * GeoMath.DegToRad;
        var (ux, uy) = (Math.Sin(h), Math.Cos(h));
        var len = LocalPoint.Distance(t, graph.Projection.ToLocal(c.Primary ? airport.Runways[c.Runway].Secondary.Position
            : airport.Runways[c.Runway].Primary.Position));
        LocalPoint At(double along) => new(t.EastM + ux * along, t.NorthM + uy * along);
        var count = 0;
        if ((role & 1) != 0)
            output[count++] = (At(-ApproachCheckM), At(len));
        if ((role & 2) != 0)
            output[count++] = (At(0), At(len + DepartureCheckM));
        return count;
    }

    private static double SegmentDistance(LocalPoint p1, LocalPoint p2, LocalPoint q1, LocalPoint q2)
    {
        if (SegmentsCross(p1, p2, q1, q2))
            return 0;
        return Math.Min(Math.Min(PointSegment(p1, q1, q2), PointSegment(p2, q1, q2)),
            Math.Min(PointSegment(q1, p1, p2), PointSegment(q2, p1, p2)));
    }

    private static double PointSegment(LocalPoint p, LocalPoint a, LocalPoint b)
    {
        double dx = b.EastM - a.EastM, dy = b.NorthM - a.NorthM;
        var l2 = dx * dx + dy * dy;
        var u = l2 == 0 ? 0 : Math.Clamp(((p.EastM - a.EastM) * dx + (p.NorthM - a.NorthM) * dy) / l2, 0, 1);
        return LocalPoint.Distance(p, new LocalPoint(a.EastM + u * dx, a.NorthM + u * dy));
    }

    private static bool SegmentsCross(LocalPoint p1, LocalPoint p2, LocalPoint q1, LocalPoint q2)
    {
        static double Cross(LocalPoint o, LocalPoint x, LocalPoint y) =>
            (x.EastM - o.EastM) * (y.NorthM - o.NorthM) - (x.NorthM - o.NorthM) * (y.EastM - o.EastM);
        return Cross(q1, q2, p1) * Cross(q1, q2, p2) < 0 && Cross(p1, p2, q1) * Cross(p1, p2, q2) < 0;
    }

    private static bool Intersect(AirportData airport, TaxiGraph graph, int a, int b)
    {
        var p1 = graph.Projection.ToLocal(airport.Runways[a].Primary.Position);
        var p2 = graph.Projection.ToLocal(airport.Runways[a].Secondary.Position);
        var q1 = graph.Projection.ToLocal(airport.Runways[b].Primary.Position);
        var q2 = graph.Projection.ToLocal(airport.Runways[b].Secondary.Position);
        static double Cross(LocalPoint o, LocalPoint x, LocalPoint y) =>
            (x.EastM - o.EastM) * (y.NorthM - o.NorthM) - (x.NorthM - o.NorthM) * (y.EastM - o.EastM);
        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>
    /// ¿Se puede aterrizar por <paramref name="arrivals"/> y despegar por <paramref name="departures"/> a la vez? No si es la
    /// misma pista en sentido contrario (EBBR 2026-11-14: llegadas 01 y salidas 19 durante el cambio de pista), si las pistas
    /// se cruzan o son paralelas muy juntas, o si las trayectorias convergen. Sirve para el cambio de pista progresivo.
    /// </summary>
    public static bool CanMixDuringChange(AirportData airport, TaxiGraph graph, IReadOnlyList<string> arrivals, IReadOnlyList<string> departures)
    {
        foreach (var arr in arrivals)
        {
            foreach (var dep in departures)
            {
                if (Find(airport, arr) is not { } a || Find(airport, dep) is not { } d)
                    continue;
                var ia = IndexOf(airport, a.Runway);
                var id = IndexOf(airport, d.Runway);
                if (ia == id)
                {
                    if (!ReferenceEquals(a.End, d.End))
                        return false;
                    continue;
                }
                if (Intersect(airport, graph, ia, id) || CloseParallels(airport, graph, ia, id)
                    || Converging(airport, graph, (ia, ReferenceEquals(a.End, a.Runway.Primary), a.End), 1,
                        (id, ReferenceEquals(d.End, d.Runway.Primary), d.End), 2))
                    return false;
            }
        }
        return true;
    }

    private static int IndexOf(AirportData airport, Runway runway)
    {
        for (var i = 0; i < airport.Runways.Count; i++)
        {
            if (ReferenceEquals(airport.Runways[i], runway))
                return i;
        }
        return -1;
    }

    private static (Runway Runway, RunwayEnd End)? Find(AirportData airport, string? ident)
    {
        foreach (var r in airport.Runways)
        {
            if (r.Primary.Ident == ident)
                return (r, r.Primary);
            if (r.Secondary.Ident == ident)
                return (r, r.Secondary);
        }
        return null;
    }
}
