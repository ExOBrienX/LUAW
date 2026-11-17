using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

// Headless (TESTING.md §3): el motor completo con FakeSimBridge, acelerado, con escenarios y métricas.
// Uso: dotnet run --project tools/SimATC.Headless -- --scenario scel-3-departures --speed 20 [--hours 2] [--out m.json] [--radio]

var scenarioName = "scel-3-departures";
var speed = 20.0;
double? hours = null;
string? outPath = null;
// Radio de carga: los escenarios se calibraron con 40 NM (ADR-089: la app usa 60 por defecto; --radius para probar otro).
double radiusNm = 40;
int? seedOverride = null;
string[] trace = [];
double traceFrom = 0, traceTo = double.MaxValue;
var lastAirTrace = double.MinValue;
var lastTrace = -10.0;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--scenario": scenarioName = args[i + 1]; break;
        case "--speed": speed = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--hours": hours = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--out": outPath = args[i + 1]; break;
        case "--radius": radiusNm = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--seed": seedOverride = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        // --trace HDL008,HDL009 --trace-from 700 --trace-to 800: posición, fase y espera cada 2 s (para depurar).
        case "--trace": trace = [.. args[i + 1].Split(',')]; break;
        case "--trace-from": traceFrom = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--trace-to": traceTo = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
    }
}

var scenarioPath = Path.Combine(AppContext.BaseDirectory, "Scenarios", scenarioName + ".json");
if (!File.Exists(scenarioPath))
{
    Console.Error.WriteLine($"No existe el escenario {scenarioName} ({scenarioPath}).");
    return 2;
}
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(scenarioPath), jsonOptions)!;
if (seedOverride is { } so)
    scenario = scenario with { Seed = so };
var duration = (hours ?? scenario.Hours) * 3600;

var cachePath = new AirportCache(AppPaths.AirportCache).PathFor(scenario.Airport);
if (!File.Exists(cachePath))
{
    Console.Error.WriteLine($"Falta la caché de {scenario.Airport} ({cachePath}): abre la app una vez con MSFS para generarla.");
    return 2;
}
var airport = AirportCache.Deserialize(File.ReadAllText(cachePath))!.Airport;
var graph = TaxiGraph.Build(airport);
var rng = new Random(scenario.Seed);
var runwayPlanText = "";
IReadOnlyList<int> standbyRunways = [];
// "auto" (ADR-091): pista en uso por el viento del escenario y reparto automático de llegadas y salidas, como en la app.
if (scenario.Runways is ["auto"])
{
    var selector = new SimATC.Core.Atc.ActiveRunwaySelector(airport);
    selector.Update(scenario.WindFromDeg, scenario.WindKt, 0);
    var arrPh = scenario.Arrivals is { } af ? 3600 / af.IntervalS : 0;
    var depPh = scenario.Flow is { } ff ? 3600 / ff.IntervalS : 0;
    SimATC.Core.Atc.RunwayPlanner.PeakMixedAllowed = scenario.PeakMixed;
    var plan = SimATC.Core.Atc.RunwayPlanner.Plan(airport, graph, selector.Current!.Ends, scenario.WindFromDeg, scenario.WindKt,
        arrivalsPerHour: arrPh, departuresPerHour: depPh, planDeparture: scenario.PlanDeparture, planArrival: scenario.PlanArrival);
    Console.WriteLine($"Pista en uso {selector.Current.Label}; reparto: {plan.Reason}.");
    runwayPlanText = $"llegadas {string.Join("/", plan.Arrival)}, salidas {string.Join("/", plan.Departure)}";
    standbyRunways = plan.Standby;
    // Con mucha demanda, como en la app: la paralela de la de llegadas también (si no es de salidas).
    var busyParallel = false;
    var landingSet = SimATC.Core.Flight.ParallelRunways.LandingSet(airport, plan.Arrival, SimATC.Core.Flight.ParallelRunways.OnAtArrivals, ref busyParallel);
    if (landingSet.Any(plan.Departure.Contains))
        landingSet = plan.Arrival;
    Console.WriteLine($"Llegadas por {string.Join("/", landingSet)}.");
    scenario = scenario with
    {
        Runways = [.. plan.Departure],
        Arrivals = scenario.Arrivals is { } a0 ? a0 with { Runways = [.. landingSet] } : null,
    };
}
// Sentidos de circulación de las pistas de salida (ADR-067), como en la app; --no-flows para comparar.
if (!args.Contains("--no-flows") && scenario.Runways.Length > 0)
{
    var ends = new List<(int, bool)>();
    foreach (var ident in scenario.Runways)
    {
        for (var r = 0; r < airport.Runways.Count; r++)
        {
            if (airport.Runways[r].Primary.Ident == ident)
                ends.Add((r, true));
            else if (airport.Runways[r].Secondary.Ident == ident)
                ends.Add((r, false));
        }
    }
    // La pista de reserva sin usar sirve de calle (ADR-160), como en la app.
    graph.SetStandbyRunways(standbyRunways);
    graph.SetFlows(ends);
    // Y las calles de salida de las pistas de llegada, alejándose de la pista (ADR-092), como en la app.
    var arrEnds = new List<(int, bool)>();
    foreach (var ident in scenario.Arrivals?.Runways ?? [])
    {
        for (var r = 0; r < airport.Runways.Count; r++)
        {
            if (airport.Runways[r].Primary.Ident == ident)
                arrEnds.Add((r, true));
            else if (airport.Runways[r].Secondary.Ident == ident)
                arrEnds.Add((r, false));
        }
    }
    if (!args.Contains("--no-exit-flows"))
        graph.SetArrivalExitFlows(arrEnds);
    Console.WriteLine($"Sentidos de circulación para {string.Join("/", scenario.Runways)}: {graph.FlowEdgeCount} tramos; " +
                      $"{graph.ExitFlowEdgeCount} de salida de las pistas de llegada.");
}

// El "piloto" queda en el aire sobre el aeropuerto (no estorba en tierra); sirve para retirar las salidas a 45 NM.
var refPos = airport.Reference;
var pilotAir = new UserAircraftState(refPos, airport.ElevationFt + 3000, 3000, 0, 0, 0, 0, false, 118100, 2000);
// Suelo del puente falso: la pista más alta (ADR-095). Con la elevación del aeropuerto, en LFPG (343,7 ft) la 09R (347 ft)
// quedaba 3 ft sobre el suelo: las llegadas flotaban, "aterrizaje largo" y motor y al aire (como el error de ADR-093).
using var bridge = new FakeSimBridge(pilotAir)
{
    GroundElevationFt = airport.Runways.Count > 0 ? airport.Runways.Max(r => r.ElevationFt) : airport.ElevationFt,
};
bridge.Start();
var logs = new List<string>();
var exceptions = 0;
TrafficEngine? engine = null;
TrafficEngine.SetInjectRadiusNm(radiusNm);
engine = new TrafficEngine(bridge, m =>
{
    logs.Add($"[{engine?.Clock.SimTime ?? 0:F0} s] {m}");
    if (m.Contains("error en el tick", StringComparison.Ordinal))
        exceptions++;
});
// --radio: los IA hablan en la frecuencia (hito 4e-1) con las frecuencias de SCEL de data/atc (Ground, Tower, Approach).
if (args.Contains("--radio"))
    engine.Radio.Frequencies = new SimATC.Core.Atc.RadioFrequencies(122200, 118100, 119700);
bridge.Emit();

// Puestos y pistas.
var gates = Enumerable.Range(0, airport.Parkings.Count)
    .Where(i => airport.Parkings[i].Type is ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy)
    .ToList();
(int Runway, bool Primary) RunwayEnd(string ident)
{
    for (var r = 0; r < airport.Runways.Count; r++)
    {
        if (airport.Runways[r].Primary.Ident == ident) return (r, true);
        if (airport.Runways[r].Secondary.Ident == ident) return (r, false);
    }
    throw new ArgumentException($"No hay cabecera {ident} en {airport.Icao}.");
}
int StandIndex(string name)
{
    var parts = name.Split(' ');
    return airport.Parkings.ToList().FindIndex(p => p.Name.ToString() == parts[0] && p.Number == int.Parse(parts[1], CultureInfo.InvariantCulture));
}

var agents = new List<DepartureAgent>();
var launchedAt = new Dictionary<uint, double>();
var standOf = new Dictionary<uint, int>();
uint nextId = 1;
var launched = 0;
var departed = new HashSet<uint>();
engine.AgentRemoved += id => standOf.Remove(id);

bool Launch(int stand, string runwayIdent)
{
    var (r, primary) = RunwayEnd(runwayIdent);
    var parking = airport.Parkings[stand];
    var heavy = parking.RadiusM >= 30 && rng.NextDouble() < 0.35;
    var perf = heavy ? AircraftPerformance.HeavyJet : AircraftPerformance.MediumJet;
    var agent = DepartureAgent.Plan(nextId, $"HDL{nextId:000}", airport, graph, stand, r, primary, perf, DepartureTimings.Default, 2,
        standOf.Values.ToHashSet(), 0, heavy ? 60 : 36,
        GeoMath.Destination(parking.Position, parking.HeadingTrueDeg, 6), parking.HeadingTrueDeg,
        heavy ? WakeCategory.Heavy : WakeCategory.Medium);
    if (agent is null)
        return false;
    bridge.Spawned[nextId] = new ParkedSpawnRequest("HDL", agent.Callsign, parking.Position, airport.ElevationFt, parking.HeadingTrueDeg, 2);
    standOf[nextId] = stand;
    agents.Add(agent);
    launchedAt[nextId] = engine.Clock.SimTime;
    engine.Add(agent);
    nextId++;
    launched++;
    return true;
}

// Llegadas (ADR-091): aparecen a StartNm NM por la prolongación del eje, con puesto, y al aterrizar ruedan a él.
var arrivalModel = new AircraftModel("HDL_A320", ModelSource.Fsltl, "A320", "", WakeCategory.Medium, 2, 34, ["GATE"], []); // TEST DATA
var arrivalsSpawned = 0;
var arrivalStandKey = new Dictionary<string, uint>();
var arrivalsLanded = 0;
var arrivalsParked = 0;
var goArounds = 0;
var holds = 0;
var nextArrival = scenario.Arrivals?.StartS ?? double.MaxValue;
var arrivalTurn = 0;
var departureWaits = new List<double>();
if (trace.Length > 0)
    engine.Sequencer.Log += m => Console.WriteLine($"  SECUENCIA {SimTime():F0} {m}");
engine.Sequencer.Event += (_, e) =>
{
    if (e == ArrivalEvent.GoAround)
        goArounds++;
    else if (e == ArrivalEvent.Holding)
        holds++;
};
engine.ArrivalPreview = (f, pl) => DepartureAgent.ArrivalRouteFor(graph, pl, f.StandIndex);
engine.ArrivalHandoff = f =>
{
    if (f.Landing is not { } l || f.StandIndex < 0)
        return null;
    RunwayEntry? exit = l.ExitEntry >= 0 && l.ExitEntry < graph.RunwayEntries.Count ? graph.RunwayEntries[l.ExitEntry] : null;
    var best = exit is null ? double.MaxValue : -1;
    foreach (var e in graph.RunwayEntries)
    {
        if (e.RunwayIndex != l.RunwayIndex)
            continue;
        var d = GeoMath.DistanceM(graph.Projection.ToGeo(graph.Position(e.RunwayNode)), l.ExitPoint);
        if (d < best)
        {
            best = d;
            exit = e;
        }
    }
    if (exit is null)
        return null;
    var g = DepartureAgent.PlanArrival(f.ObjectId, f.Callsign, airport, graph, exit, f.StandIndex, f.Position, f.HeadingDeg, f.SpeedKt,
        AircraftPerformance.MediumJet, 2, 0, 34, WakeCategory.Medium);
    if (g is not null)
    {
        agents.Add(g);
        arrivalsLanded++;
        if (arrivalStandKey.Remove(f.Callsign, out var key))
            standOf.Remove(key);
        standOf[g.ObjectId] = f.StandIndex;
    }
    return g;
};
engine.ArrivalParked += _ => arrivalsParked++;

bool SpawnArrival(string ident)
{
    var busy = standOf.Values.ToHashSet();
    var free = gates.Where(g => !busy.Contains(g)).ToList();
    if (free.Count == 0 || FlightPlanner.LandingFor(airport, graph, ident, PerformanceCategory.MediumJet) is not { } landing)
        return false;
    var stand = free[rng.Next(free.Count)];
    landing = landing.TowardSide(airport.Parkings[stand].Position, FlightPlanner.MinRolloutM(PerformanceCategory.MediumJet));
    var startNm = scenario.Arrivals!.StartNm;
    var from = GeoMath.Destination(landing.Threshold, GeoMath.NormalizeDeg(landing.HeadingDeg + 180), startNm * 1852);
    var (path, _, _) = FlightPlanner.Arrival(airport, landing, from, landing.HeadingDeg);
    if (path.Count == 0)
        return false;
    var id = nextId++;
    var f = new AirborneAgent($"HDA{id:000}", "HDA", "A320", "XXXX", airport.Icao, arrivalModel, PerformanceCategory.MediumJet, path, from,
        landing.ElevationFt + startNm * 318, landing.HeadingDeg, 210, -800, 30_000, landing)
    { ControlledArrival = true, StandIndex = stand };
    standOf[id] = stand; // el puesto queda reservado mientras llega; al aterrizar pasa al id de su rodaje
    arrivalStandKey[f.Callsign] = id;
    engine.AddFlight(f);
    arrivalsSpawned++;
    return true;
}

// Métricas.
var separationLosses = 0;
var losing = new HashSet<(uint, uint)>();
var tickMs = new List<double>(200_000);
var maxActive = 0;
var nextFlow = scenario.Flow?.StartS ?? double.MaxValue;
var runwayTurn = 0;
var fired = new bool[scenario.Departures.Count];
var incursionOn = false;
double? incursionStart = null;
var sw = new Stopwatch();
var wall = Stopwatch.StartNew();
const double frameReal = 1.0 / 30;
double SimTime() => engine.Clock.SimTime;

while (SimTime() < duration)
{
    var t = SimTime();
    // Salidas programadas.
    for (var i = 0; i < scenario.Departures.Count; i++)
    {
        if (!fired[i] && t >= scenario.Departures[i].AtS)
        {
            fired[i] = true;
            Launch(StandIndex(scenario.Departures[i].Stand), scenario.Departures[i].Runway);
        }
    }
    // Flujo continuo: una salida cada IntervalS desde un puesto libre, alternando pistas.
    if (scenario.Flow is { } flow && t >= nextFlow)
    {
        nextFlow = t + flow.IntervalS;
        // Solo las salidas en curso (las llegadas ya estacionadas no cuentan).
        var active = agents.Count(a => !a.Inbound && a.OnGround && a.Phase != DeparturePhase.Departed);
        if (active < flow.MaxOnGround)
        {
            var busy = standOf.Values.ToHashSet();
            var free = gates.Where(g => !busy.Contains(g)).ToList();
            if (free.Count > 0)
            {
                // Pistas de salida alternadas, como en la app; --nearest prueba la más cercana al puesto (empeoró: ADR-092).
                var stand = free[rng.Next(free.Count)];
                var at = graph.Projection.ToLocal(airport.Parkings[stand].Position);
                var runway = scenario.Runways.OrderBy(id =>
                {
                    var (r0, p0) = RunwayEnd(id);
                    return graph.DepartureEntry(r0, p0) is { } en ? LocalPoint.Distance(at, graph.Position(en.EntryNode)) : double.MaxValue;
                }).First();
                runwayTurn++;
                Launch(stand, args.Contains("--nearest") ? runway : scenario.Runways[(runwayTurn - 1) % scenario.Runways.Length]);
            }
        }
    }
    // Llegadas: una cada IntervalS, alternando sus pistas.
    if (scenario.Arrivals is { } arr && t >= nextArrival)
    {
        nextArrival = t + arr.IntervalS;
        SpawnArrival(arr.Runways[arrivalTurn++ % arr.Runways.Length]);
    }
    // Incursión del piloto: en tierra en el centro de la pista durante un rato.
    if (scenario.Incursion is { } inc)
    {
        // Empieza cuando el primero está en su carrera de despegue (si OnTakeoffRoll) o a la hora AtS.
        if (inc.OnTakeoffRoll && incursionStart is null && agents.Any(a => a.Phase == DeparturePhase.TakeoffRoll && a.SpeedKt > 30))
            incursionStart = t;
        var start = inc.OnTakeoffRoll ? incursionStart ?? double.MaxValue : inc.AtS;
        var on = t >= start && t < start + inc.DurationS;
        if (on != incursionOn)
        {
            incursionOn = on;
            var (r, _) = RunwayEnd(inc.Runway);
            bridge.State = on
                ? pilotAir with { Position = airport.Runways[r].Center, OnGround = true, AltitudeAglFt = 0 }
                : pilotAir;
            bridge.Emit();
        }
    }

    var before = SimTime();
    sw.Restart();
    engine.OnFrame(new FrameInfo(frameReal, speed, false));
    sw.Stop();
    var ticks = (int)Math.Round((SimTime() - before) / SimClock.TickSeconds);
    if (ticks > 0)
        tickMs.Add(sw.Elapsed.TotalMilliseconds / ticks);

    if (trace.Length > 0 && SimTime() >= traceFrom && SimTime() <= traceTo && SimTime() - lastTrace >= 2)
    {
        lastTrace = SimTime();
        var held = Enumerable.Range(0, graph.NodeCount).Select(nd => (nd, h: engine.Coordinator.HolderOf(nd))).Where(x => x.h != 0)
            .GroupBy(x => x.h).Select(gr => $"{gr.Key}:{gr.Count()}");
        Console.WriteLine($"  TRAZA {SimTime():F0} cruces tomados por id: {string.Join(" ", held)}");
        var prio = typeof(GroundCoordinator).GetMethod("PriorityOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        foreach (var a in agents.Where(x => trace.Contains(x.Callsign)))
        {
            var mine = Enumerable.Range(0, graph.NodeCount).Where(nd => engine.Coordinator.HolderOf(nd) == a.ObjectId)
                .Select(nd => $"{graph.Position(nd).EastM:F0},{graph.Position(nd).NorthM:F0}");
            Console.WriteLine($"  TRAZA {SimTime():F0} {a.Callsign} id {a.ObjectId} tiene cruces en: {string.Join(" ", mine)}");
        }
        foreach (var a in agents.Where(x => trace.Contains(x.Callsign)))
            Console.WriteLine($"  TRAZA {SimTime():F0} {a.Callsign} prioridad {prio?.Invoke(engine.Coordinator, [a])} {a.Phase} pos {a.Position.EastM:F0},{a.Position.NorthM:F0} rumbo {a.HeadingDeg:F0} " +
                              $"{a.SpeedKt:F1} kt espera {a.Hold}/{a.HoldFor} llegada {engine.Control.Tower.ArrivalSeconds(a.RunwayIndex):F0}s pista {a.RunwayIndex} límite {a.TaxiLimitS:F0} avance {a.TaxiProgress:F0}/{a.TaxiPath.Length:F0} calles {string.Join("-", a.Taxiways)}");
    }
    if (trace.Length > 0 && SimTime() >= traceFrom && SimTime() <= traceTo && SimTime() - lastAirTrace >= (traceTo - traceFrom < 60 ? 1 : 10))
    {
        lastAirTrace = SimTime();
        foreach (var g in agents.Where(x => x.OnRunwayInbound || x.CrossingRunway >= 0 || x.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll))
            Console.WriteLine($"  PISTA {SimTime():F0} {g.Callsign} {g.Phase} pista {g.RunwayIndex} cruza {g.CrossingRunway} entrante {g.OnRunwayInbound} pos {g.Position.EastM:F0},{g.Position.NorthM:F0} espera {g.Hold}/{g.HoldFor} avance {g.TaxiProgress:F0}");
        foreach (var f in engine.AirborneNow().Where(x => trace.Contains(x.Callsign)))
            Console.WriteLine($"  TRAZA {SimTime():F0} {f.Callsign} aire {f.Phase} {f.AltitudeFt:F0} ft {f.SpeedKt:F0} kt {f.RemainingNm:F1} NM salida {f.Landing?.ExitAlongM:F0} m elev {f.Landing?.ElevationFt:F0} {f.LastGoAroundReason}");
    }
    // Separación en tierra entre aviones que se mueven (no en la carrera de despegue).
    var moving = agents.Where(a => a.OnGround && a.Phase is > DeparturePhase.BeaconOn and < DeparturePhase.TakeoffRoll).ToList();
    maxActive = Math.Max(maxActive, agents.Count(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed)));
    for (var i = 0; i < moving.Count; i++)
    for (var j = i + 1; j < moving.Count; j++)
    {
        var key = (Math.Min(moving[i].ObjectId, moving[j].ObjectId), Math.Max(moving[i].ObjectId, moving[j].ObjectId));
        // En calles paralelas cuenta el margen entre puntas de ala (ADR-044).
        var close = GroundCoordinator.SeparationLost(moving[i], moving[j], scenario.MinGroundSeparationM);
        if (close && losing.Add(key))
        {
            separationLosses++;
            // Detalle para depurar: quiénes, en qué fase y a qué distancia.
            Console.WriteLine($"  pérdida de separación: {moving[i].Callsign} ({moving[i].Phase}, {moving[i].Hold}) – {moving[j].Callsign} " +
                $"({moving[j].Phase}, {moving[j].Hold}) a {LocalPoint.Distance(moving[i].Position, moving[j].Position):F0} m, a los {engine.Clock.SimTime:F0} s " +
                $"({moving[i].SpeedKt:F0} y {moving[j].SpeedKt:F0} kt; esperan a {moving[i].HoldFor} y {moving[j].HoldFor}; " +
                $"rumbos {moving[i].HeadingDeg:F0}° y {moving[j].HeadingDeg:F0}°; calles {string.Join("-", moving[i].Taxiways)} / {string.Join("-", moving[j].Taxiways)}; " +
                $"llegada {moving[i].Inbound}/{moving[j].Inbound}; avance {moving[i].TaxiProgress:F0}/{moving[i].TaxiPath.Length:F0} y {moving[j].TaxiProgress:F0}/{moving[j].TaxiPath.Length:F0} m)");
        }
        else if (!close)
            losing.Remove(key);
    }
    foreach (var a in agents)
    {
        if (a.Phase is DeparturePhase.Climb or DeparturePhase.Departed && departed.Add(a.ObjectId) && launchedAt.TryGetValue(a.ObjectId, out var at))
            departureWaits.Add(t - at);
    }
    agents.RemoveAll(a => a.Phase == DeparturePhase.Departed || (a.Phase == DeparturePhase.Climb && a.AglFt > 10_000));
}

// Atascados al final: en tierra hace más de 30 min desde que empezaron.
var stuck = agents.Count(a => a.OnGround && a.Phase is > DeparturePhase.BeaconOn);
tickMs.Sort();
double Pct(double p) => tickMs.Count == 0 ? 0 : tickMs[Math.Min(tickMs.Count - 1, (int)(p * tickMs.Count))];
var tower = engine.Control.Tower;
var safety = engine.Coordinator;
var metrics = new Metrics(scenarioName, duration / 3600, speed, wall.Elapsed.TotalSeconds, launched, departed.Count, maxActive,
    separationLosses, tower.AiIncursions, tower.PilotIncursions, tower.RejectedTakeoffs, safety.DeadlocksDetected,
    safety.DeadlocksResolved, safety.DeadlocksUnresolved, exceptions, Pct(0.5), Pct(0.99))
{
    ArrivalsSpawned = arrivalsSpawned, ArrivalsLanded = arrivalsLanded, ArrivalsParked = arrivalsParked, GoArounds = goArounds,
    Holds = holds, DepartureWaitP50Min = departureWaits.Count > 0 ? departureWaits.Order().ElementAt(departureWaits.Count / 2) / 60 : 0,
    RunwayPlan = runwayPlanText, Green = false,
};

var expectPilot = scenario.Incursion is not null;
// Con llegadas: a lo más 1 motor y al aire cada 10 llegadas (ADR-091).
var gaOk = scenario.Arrivals is null || goArounds <= Math.Max(1, arrivalsSpawned / 10);
// Con flujo continuo, el tráfico tiene que moverse (ADR-093: el lote daba "verde" a EGKK con 0 de 14 despegues): al menos
// el 40 % de las salidas lanzadas despega y la mitad de las llegadas aterriza (las últimas no alcanzan a terminar).
var flowOk = (scenario.Flow is null || launched < 4 || departed.Count >= launched * 0.4)
             && (scenario.Arrivals is null || arrivalsSpawned < 4 || arrivalsLanded >= arrivalsSpawned * 0.5);
var green = metrics.SeparationLosses == 0 && metrics.AiIncursions == 0 && metrics.DeadlocksUnresolved == 0 && metrics.Exceptions == 0
    && (!expectPilot || metrics.PilotIncursions > 0) && gaOk && flowOk;

Console.WriteLine($"Escenario {scenarioName}: {metrics.SimHours:0.##} h simuladas en {metrics.WallSeconds:0} s reales (x{metrics.SimHours * 3600 / metrics.WallSeconds:0}).");
Console.WriteLine($"  Salidas: {launched} lanzadas, {departed.Count} despegaron, máx. {maxActive} a la vez, {stuck} en tierra al terminar.");
if (departureWaits.Count > 0)
{
    departureWaits.Sort();
    Console.WriteLine($"  Puesto a despegue: mediana {departureWaits[departureWaits.Count / 2] / 60:0.0} min, p90 {departureWaits[(int)(departureWaits.Count * 0.9)] / 60:0.0} min.");
}
if (scenario.Arrivals is not null)
    Console.WriteLine($"  Llegadas: {arrivalsSpawned} creadas, {arrivalsLanded} aterrizaron, {arrivalsParked} en su puesto; " +
                      $"{goArounds} motor y al aire, {holds} esperas  {(gaOk ? "✔" : "✘")}");
Console.WriteLine($"  Pérdidas de separación en tierra (< {scenario.MinGroundSeparationM} m): {metrics.SeparationLosses}  {(metrics.SeparationLosses == 0 ? "✔" : "✘")}");
Console.WriteLine($"  Incursiones causadas por la IA: {metrics.AiIncursions}  {(metrics.AiIncursions == 0 ? "✔" : "✘")}");
Console.WriteLine($"  Incursiones del piloto: {metrics.PilotIncursions} (despegues abortados: {metrics.RejectedTakeoffs})");
Console.WriteLine($"  Bloqueos: {metrics.DeadlocksDetected} detectados, {metrics.DeadlocksResolved} resueltos, {metrics.DeadlocksUnresolved} sin resolver en 120 s  {(metrics.DeadlocksUnresolved == 0 ? "✔" : "✘")}");
Console.WriteLine($"  Excepciones por avión: {metrics.Exceptions}  {(metrics.Exceptions == 0 ? "✔" : "✘")}");
Console.WriteLine($"  Tiempo del tick: p50 {metrics.TickP50Ms:0.000} ms, p99 {metrics.TickP99Ms:0.000} ms");
Console.WriteLine(green ? "RESULTADO: en verde" : "RESULTADO: con fallas");
foreach (var l in logs.Where(l => l.Contains("BUG", StringComparison.Ordinal) || l.Contains("sin resolver", StringComparison.Ordinal) || Environment.GetEnvironmentVariable("HDL_LOG") is { } f && l.Contains(f, StringComparison.Ordinal)).Take(40))
    Console.WriteLine($"  · {l}");

outPath ??= Path.Combine(Environment.CurrentDirectory, $"headless-{scenarioName}.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(metrics with { Green = green }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Métricas en {outPath}");
return green ? 0 : 1;

/// <summary>Escenario (TESTING.md §3). Semilla fija: mismo resultado en cada corrida.</summary>
internal sealed record Scenario(string Airport, int Seed, double Hours, string[] Runways, List<ScheduledDeparture> Departures,
    DepartureFlow? Flow, PilotIncursion? Incursion, double MinGroundSeparationM = 45, ArrivalFlow? Arrivals = null,
    double WindFromDeg = 180, double WindKt = 6, string? PlanDeparture = null, string? PlanArrival = null, bool PeakMixed = true);

/// <summary>Una llegada cada <paramref name="IntervalS"/> por sus pistas, desde <paramref name="StartNm"/> NM (ADR-091).</summary>
internal sealed record ArrivalFlow(double StartS, double IntervalS, string[] Runways, double StartNm = 25);

internal sealed record ScheduledDeparture(string Stand, string Runway, double AtS);

/// <summary>Una salida cada <paramref name="IntervalS"/> desde un puesto libre, sin pasar de MaxOnGround en tierra.</summary>
internal sealed record DepartureFlow(double StartS, double IntervalS, int MaxOnGround);

/// <summary>El piloto se mete en tierra al centro de la pista en AtS durante DurationS.</summary>
internal sealed record PilotIncursion(string Runway, double AtS, double DurationS, bool OnTakeoffRoll = false);

internal sealed record Metrics(string Scenario, double SimHours, double Speed, double WallSeconds, int Launched, int Departed,
    int MaxActive, int SeparationLosses, int AiIncursions, int PilotIncursions, int RejectedTakeoffs, int DeadlocksDetected,
    int DeadlocksResolved, int DeadlocksUnresolved, int Exceptions, double TickP50Ms, double TickP99Ms)
{
    public int ArrivalsSpawned { get; init; }
    public int ArrivalsLanded { get; init; }
    public int ArrivalsParked { get; init; }
    public int GoArounds { get; init; }
    public int Holds { get; init; }
    public double DepartureWaitP50Min { get; init; }
    public string RunwayPlan { get; init; } = "";
    public bool Green { get; init; }
}
