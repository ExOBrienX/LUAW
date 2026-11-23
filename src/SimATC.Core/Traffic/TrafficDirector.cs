using System.Collections.Concurrent;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;
using SimATC.Core.Units;
using SimATC.Core.World;

using SimATC.Core.Text;

namespace SimATC.Core.Traffic;

/// <summary>Vuelo y hora de salida programada (STD, generada) de un estacionado.</summary>
public sealed record DepartureSchedule(string Callsign, string Destination, DateTime Std);

/// <summary>Lo que el director necesita saber en cada vuelta (lo arma la app; todo son copias o datos de solo lectura).</summary>
/// <param name="Airport">Aeropuerto cargado (donde están el piloto y el tráfico en tierra), o null.</param>
/// <param name="ActiveRunway">Cabecera en uso para salir y aterrizar ("17R"), o null si aún no se sabe (sin viento).</param>
/// <param name="BusyStands">Puestos ocupados: estacionados, salidas en curso y llegadas que ya tienen puesto.</param>
public sealed record DirectorContext(
    AirportData? Airport,
    TaxiGraph? Graph,
    string? ActiveRunway,
    GeoPoint Pilot,
    DateTime UtcNow,
    IReadOnlyList<InjectedParked> Parked,
    IReadOnlySet<int> BusyStands,
    int Density,
    bool Departures = true,
    IReadOnlyList<string>? LandingRunways = null);

/// <summary>
/// Director del tráfico (etapas 6, 6b y 7, RF-12d; ADR-062): decide qué vuelos existen alrededor del piloto, sin preparar
/// nada a mano. Todo el tráfico lo genera el motor: vuelos reales por indicativo (standing-data, CC0) con horas generadas
/// (<c>timeSource = generated</c>), y las llegadas y salidas del aeropuerto cargado limitadas por sus puestos libres
/// (RF-12c). Cada vuelo existe desde que sale de su origen: su plan completo (SID, ruta de VATSIM o por aerovías, STAR,
/// aproximación) se arma una vez con <see cref="FlightPlanBuilder"/> y el avión aparece en el punto de ese plan que le
/// toca por su avance. Se llama cada pocos segundos fuera del motor; los relevos del motor llegan en su hilo.
/// </summary>
public sealed class TrafficDirector
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    /// <summary>Ventana lógica alrededor del piloto (RF-12d).</summary>
    public const double WindowNm = 200;

    /// <summary>Tiempo mínimo en el puesto de una llegada antes de volver a salir (min). APROXIMADO (escala corta).</summary>
    public const double TurnaroundMinutes = 35;

    private readonly WorldAirports _airports;
    private readonly string _routesPath;
    private readonly ModelMatcher _matcher;
    private readonly TrafficEngine _engine;
    private readonly Action<string> _log;
    // Lo usan el bucle del director y el hilo del motor (relevos de llegada): Random no es seguro entre hilos.
    private readonly LockedRandom _rng;

    /// <summary>Random con candado (auditoría 2026-11-03: el mismo Random se usaba desde dos hilos).</summary>
    private sealed class LockedRandom(Random inner)
    {
        private readonly Lock _lock = new();

        public int Next(int max)
        {
            lock (_lock)
                return inner.Next(max);
        }

        public int Next(int min, int max)
        {
            lock (_lock)
                return inner.Next(min, max);
        }

        public double NextDouble()
        {
            lock (_lock)
                return inner.NextDouble();
        }
    }

    private WorldRoutes? _routes;
    // Vuelos de la zona que pasan por la ventana (sin el aeropuerto cargado) y lo cerca que pasan del piloto (NM).
    private List<(WorldLeg Leg, double PassNm)> _otherLegs = [];
    private Task? _routesLoading;
    private string? _airportIcao;
    private bool _initialFillDone;
    private DateTime _nextArrival;
    private DateTime _nextDeparture;
    private int _window;

    private readonly ConcurrentDictionary<string, FlightInfo> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _reservedStands = new();
    private readonly ConcurrentDictionary<uint, DateTime> _parkedSince = new();

    /// <summary>Qué es cada vuelo activo: su tramo, su modelo y si es de la ventana (no del aeropuerto cargado).</summary>
    private sealed record FlightInfo(WorldLeg Leg, ModelMatch? Match, bool Window, ResolvedRoute? Route = null, PlannedFlight? Plan = null,
        string? PlanRunway = null);

    /// <summary>Plan de vuelo de un estacionado con hora de salida (ruta resuelta y plan completo con la pista en uso).</summary>
    private sealed record ParkedPlan(string Callsign, ResolvedRoute Route, PlannedFlight Plan, string Runway, DateTime At = default);

    private readonly ConcurrentDictionary<uint, ParkedPlan> _parkedPlans = new();

    // Rutas ya resueltas por par de aeropuertos ("SCIE-SCEL"): se resuelven una vez por sesión.
    private readonly ConcurrentDictionary<string, (ResolvedRoute Route, DateTime At)> _routeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Una ruta que quedó en directo (faltaban datos: aerovías o rutas de VATSIM todavía cargándose) se vuelve a buscar
    /// pasado este tiempo (min), también la de un estacionado antes de salir (ADR-089: LAN218 salió en directo).
    /// </summary>
    public const double RetryDirectMinutes = 3;

    public TrafficDirector(WorldAirports airports, string routesPath, ModelMatcher matcher, TrafficEngine engine, Action<string> log,
        int? seed = null)
    {
        _airports = airports;
        _routesPath = routesPath;
        _matcher = matcher;
        _engine = engine;
        _log = log;
        _rng = new LockedRandom(seed is { } s ? new Random(s) : new Random());
        engine.FlightFinished += OnFlightFinished;
        engine.AdoptFailed += (f, stand) =>
        {
            if (_reservedStands.TryGetValue(stand, out var cs) && cs == f.Callsign)
                _reservedStands.TryRemove(stand, out _);
        };
    }

    /// <summary>
    /// Lanza la salida de un estacionado con ese indicativo (la app hace el relevo del Parked ATC y arma la ruta de
    /// rodaje). true si salió.
    /// </summary>
    public Func<InjectedParked, string, Task<bool>>? LaunchDeparture { get; set; }

    /// <summary>
    /// Lee un aeropuerto del simulador (con la caché de aeropuertos de la app) para los procedimientos de los vuelos que
    /// van a otro aeropuerto; null si no se puede. Sin esto, esos vuelos van directo.
    /// </summary>
    public Func<string, CancellationToken, Task<AirportData?>>? LoadAirport { get; set; }

    // Aeropuertos de destino ya leídos (null = no se pudo) y los que faltan por leer.
    private readonly ConcurrentDictionary<string, AirportData?> _otherAirports = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _toLoad = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

    /// <summary>Aeropuertos leídos por vuelta (s simulador entrega cada uno en 1-3 s la primera vez; después, de la caché).</summary>
    public const int AirportLoadsPerUpdate = 6;

    /// <summary>Hasta cuántos minutos se sigue intentando llenar las llegadas iniciales (los orígenes se leen de a poco).</summary>
    public const double InitialFillMinutes = 8;

    private DateTime _initialFillStart = DateTime.MinValue;
    private int _initialCreated;
    private double _initialDistance = InitialFirstNm;

    /// <summary>La primera llegada inicial se pone a esta distancia (NM) por su ruta.</summary>
    public const double InitialFirstNm = 40;

    /// <summary>Hueco mínimo (NM) entre dos llegadas iniciales: ninguna nace encima de otra (ADR-129).</summary>
    public const double InitialMinGapNm = 9;

    /// <summary>Parte de las llegadas de una hora que ya vienen en camino al empezar (entre 40 y 170 NM). APROXIMADO.</summary>
    public const double InitialShare = 0.4;
    private string? _initialFillAirport;

    private AirportData? OtherAirport(string icao)
    {
        if (_otherAirports.TryGetValue(icao, out var a))
            return a;
        if (LoadAirport is not null && _queued.TryAdd(icao, 0))
            _toLoad.Enqueue(icao);
        return null;
    }

    private async Task LoadPendingAirportsAsync(CancellationToken ct)
    {
        for (var i = 0; i < AirportLoadsPerUpdate && LoadAirport is not null && _toLoad.TryDequeue(out var icao); i++)
        {
            AirportData? data = null;
            try
            {
                data = await LoadAirport(icao, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log(Tr.F("{0}: no se pudo leer del simulador ({1}); sus vuelos van directo.", icao, ex.Message));
            }
            _otherAirports[icao] = data;
        }
    }

    /// <summary>Puestos reservados por llegadas en el aire (para no estacionar ni asignar ahí).</summary>
    public IReadOnlyCollection<int> ReservedStands => (IReadOnlyCollection<int>)_reservedStands.Keys;

    /// <summary>Resumen para la interfaz.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Olvida todo (al limpiar el tráfico o cambiar de aeropuerto).</summary>
    public void Reset()
    {
        _schedule.Clear();
        _scheduledLegs.Clear();
        _lastStd = DateTime.MinValue;
        _active.Clear();
        _parkedPlans.Clear();
        _reservedStands.Clear();
        _parkedSince.Clear();
        _window = 0;
        _initialFillDone = false;
    }

    /// <summary>
    /// Cambio de aeropuerto activo en pleno vuelo del piloto (ADR-076): se olvida lo de tierra del anterior (horarios,
    /// estacionados, puestos reservados) y se vuelve a llenar el nuevo; los vuelos en el aire siguen igual.
    /// </summary>
    public void ResetAirport()
    {
        _schedule.Clear();
        _scheduledLegs.Clear();
        _lastStd = DateTime.MinValue;
        _parkedPlans.Clear();
        _reservedStands.Clear();
        _parkedSince.Clear();
        _initialFillDone = false;
    }

    /// <summary>Reserva un puesto (el del piloto en su destino, ADR-085): no se estaciona ni se asigna a llegadas IA.</summary>
    public void ReserveStand(int stand, string callsign) => _reservedStands[stand] = callsign;

    // Llegadas por pista en la última vuelta (para repartir entre paralelas, ADR-082).
    private readonly Dictionary<string, int> _arrivalsPerRunway = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ADR-082: las llegadas al aeropuerto activo cuya pista ya no está entre las de llegada (cambio 17 ↔ 35, o paralelas que
    /// se cierran) reciben una ruta nueva hacia la pista de llegada con menos aviones, desde donde están. No en final ni a
    /// menos de 8 NM. Cuenta además cuántas van a cada pista (para repartir las nuevas).
    /// </summary>
    private void ReplanArrivals(AirportData airport, DirectorContext ctx)
    {
        _arrivalsPerRunway.Clear();
        if (ctx.LandingRunways is not { Count: > 0 } allowed)
            return;
        foreach (var r in allowed)
            _arrivalsPerRunway[r] = 0;
        var arrivals = _engine.AirborneNow().Where(f => f.ControlledArrival && f.Destination == airport.Icao && f.Landing is not null).ToList();
        foreach (var f in arrivals)
        {
            if (_arrivalsPerRunway.ContainsKey(f.Landing!.Ident))
                _arrivalsPerRunway[f.Landing.Ident]++;
        }
        foreach (var f in arrivals)
        {
            if (allowed.Contains(f.Landing!.Ident, StringComparer.OrdinalIgnoreCase) || f.RemainingNm < 8
                || f.Phase is FlightPhase.Final or FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.GoAround)
                continue;
            var ident = LeastUsedLanding(allowed);
            if (FlightPlanner.LandingFor(airport, ctx.Graph, ident, f.Category) is not { } landing)
                continue;
            var (path, _, _) = FlightPlanner.Arrival(airport, landing, f.Position, f.HeadingDeg);
            _engine.RequestReroute(f, path, landing);
            _arrivalsPerRunway[ident]++;
            _log(Tr.F("{0}: cambia a la pista {1} (pistas de llegada {2}).", f.Callsign, ident, string.Join("/", allowed)));
        }
        // Paralelas abiertas (ADR-090): se reparten también las que ya venían, de a una por vuelta y solo lejos (más de
        // 30 NM por su ruta, antes de su aproximación). En SCEL se abrieron 17L y 17R pero 3 de 4 siguieron a 17L y se
        // hicieron motor y al aire por pista ocupada.
        if (allowed.Count < 2)
            return;
        var busiest = allowed.MaxBy(r => _arrivalsPerRunway.GetValueOrDefault(r))!;
        var least = LeastUsedLanding(allowed);
        AirborneAgent? move;
        if (_arrivalsPerRunway.GetValueOrDefault(busiest) - _arrivalsPerRunway.GetValueOrDefault(least) < 2)
        {
            // Parejo en cantidad, pero dos de la misma pista aterrizarían demasiado juntas y en la otra hay hueco a esa hora
            // (ADR-131): la de atrás pasa a la otra, en vez de terminar en espera.
            move = TooCloseInTime(arrivals, allowed, out least);
            if (move is null)
                return;
            busiest = move.Landing!.Ident;
        }
        else
        {
            move = arrivals.Where(f => string.Equals(f.Landing!.Ident, busiest, StringComparison.OrdinalIgnoreCase) && f.RemainingNm > RebalanceMinNm
                    && f.Phase is FlightPhase.Cruise or FlightPhase.Descent && !f.ApproachCleared)
                .OrderByDescending(f => f.RemainingNm).FirstOrDefault();
        }
        if (move is null || FlightPlanner.LandingFor(airport, ctx.Graph, least, move.Category) is not { } to)
            return;
        if (move.StandIndex >= 0)
            to = to.TowardSide(airport.Parkings[move.StandIndex].Position, FlightPlanner.MinRolloutM(move.Category));
        var (newPath, _, _) = FlightPlanner.Arrival(airport, to, move.Position, move.HeadingDeg);
        if (newPath.Count == 0)
            return;
        _engine.RequestReroute(move, newPath, to);
        _arrivalsPerRunway[busiest]--;
        _arrivalsPerRunway[least]++;
        _log(Tr.F("{0}: pasa a la pista {1} para repartir las llegadas entre paralelas.", move.Callsign, least));
    }

    /// <summary>
    /// La llegada que aterrizaría a menos de <see cref="ParallelRunways.CompressedGapS"/> de la anterior en su misma pista,
    /// si todavía se puede mover (lejos, sin aproximación autorizada) y en otra pista de llegada nadie aterriza a esa hora.
    /// </summary>
    private static AirborneAgent? TooCloseInTime(List<AirborneAgent> arrivals, IReadOnlyList<string> allowed, out string to)
    {
        to = "";
        var gap = ParallelRunways.CompressedGapS;
        foreach (var f in arrivals.OrderBy(ArrivalSequencer.EtaS))
        {
            if (f.RemainingNm <= RebalanceMinNm || f.Phase is not (FlightPhase.Cruise or FlightPhase.Descent) || f.ApproachCleared)
                continue;
            var eta = ArrivalSequencer.EtaS(f);
            var crowded = arrivals.Any(o => !ReferenceEquals(o, f) && SameRunway(o, f.Landing!.Ident)
                                            && ArrivalSequencer.EtaS(o) <= eta && eta - ArrivalSequencer.EtaS(o) < gap);
            if (!crowded)
                continue;
            foreach (var r in allowed)
            {
                if (SameRunway(f, r) || arrivals.Any(o => SameRunway(o, r) && Math.Abs(ArrivalSequencer.EtaS(o) - eta) < gap))
                    continue;
                to = r;
                return f;
            }
        }
        return null;
    }

    private static bool SameRunway(AirborneAgent f, string ident) => string.Equals(f.Landing?.Ident, ident, StringComparison.OrdinalIgnoreCase);

    /// <summary>Más cerca que esto (NM por su ruta) una llegada ya no se cambia de paralela para repartir.</summary>
    public const double RebalanceMinNm = 30;

    /// <summary>
    /// A menos de esto (NM por su ruta) un vuelo se toma como llegada solo si ya va a una pista de llegada en uso (sigue a la
    /// misma, sin maniobras raras); antes terminaba a 3 NM y desaparecía (ADR-085, ADR-088).
    /// </summary>
    public const double TakeOverMinNm = 12;

    /// <summary>
    /// ADR-085 (prueba de Marco 2026-11-06, SCEL→SCIE): al cambiar el aeropuerto activo en vuelo, los vuelos que ya iban a él
    /// (de paso o salidas del aeropuerto anterior) pasan a ser llegadas controladas, con pista, puesto y secuencia; antes
    /// terminaban a 3 NM y uno quedó congelado en el aire. Las llegadas controladas a otro aeropuerto (el anterior) se
    /// liberan: la Torre del nuevo ya no les habla ni las secuencia.
    /// </summary>
    private void TakeOverArrivals(AirportData airport, DirectorContext ctx, string runway)
    {
        IReadOnlyList<string> allowed = ctx.LandingRunways is { Count: > 0 } l ? l : [runway];
        foreach (var f in _engine.AirborneNow())
        {
            if (f.ControlledArrival)
            {
                if (!string.Equals(f.Destination, airport.Icao, StringComparison.OrdinalIgnoreCase) && _released.TryAdd(f, 0))
                {
                    foreach (var (held, cs) in _reservedStands)
                    {
                        if (cs == f.Callsign)
                            _reservedStands.TryRemove(held, out _);
                    }
                    _engine.RequestRelease(f);
                }
                continue;
            }
            if (!string.Equals(f.Destination, airport.Icao, StringComparison.OrdinalIgnoreCase) || f.Model is not { } model
                || _reservedStands.Values.Contains(f.Callsign)
                || f.Phase is FlightPhase.Climb or FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed
                    or FlightPhase.Finished or FlightPhase.GoAround)
                continue;
            // Si su ruta ya termina en una pista en uso, la conserva (su STAR y su aproximación, ADR-089); cerca (o en final)
            // solo así: una ruta nueva desde ahí daría vueltas.
            var keepsRoute = f.Landing?.Ident is { } current && allowed.Contains(current, StringComparer.OrdinalIgnoreCase);
            var near = keepsRoute || f.RemainingNm < TakeOverMinNm || f.Phase == FlightPhase.Final;
            if (near && !keepsRoute)
                continue;
            var stand = PickStand(ctx, airport, model, f.Airline);
            if (stand < 0)
                continue;
            var ident = near ? f.Landing!.Ident : LeastUsedLanding(allowed);
            if (FlightPlanner.LandingFor(airport, ctx.Graph, ident, f.Category) is not { } landing)
                continue;
            landing = landing.TowardSide(airport.Parkings[stand].Position, FlightPlanner.MinRolloutM(f.Category));
            // Cerca: sigue su ruta (null); lejos: ruta de llegada nueva desde donde está.
            FlightPath? path = near ? null : FlightPlanner.Arrival(airport, landing, f.Position, f.HeadingDeg).Path;
            if (path is { Count: 0 } || !_reservedStands.TryAdd(stand, f.Callsign))
                continue;
            if (_active.TryGetValue(f.Callsign, out var info))
            {
                if (info.Window)
                    Interlocked.Decrement(ref _window);
                _active[f.Callsign] = info with { Window = false };
            }
            else
            {
                _active[f.Callsign] = new FlightInfo(new WorldLeg(f.Callsign, f.Airline, f.Origin, f.Destination),
                    new ModelMatch(model, MatchKind.AirlineLivery), Window: false);
            }
            _arrivalsPerRunway[ident] = _arrivalsPerRunway.GetValueOrDefault(ident) + 1;
            _engine.RequestAdopt(f, path, landing, stand);
            _log(Tr.F("{0}: va a {1} (activo): llegada controlada a la pista {2}, puesto {3}.", f.Callsign, airport.Icao, ident, airport.Parkings[stand].DisplayName));
        }
    }

    // Llegadas ya liberadas (para pedirlo una sola vez).
    private readonly ConcurrentDictionary<AirborneAgent, byte> _released = new(ReferenceEqualityComparer.Instance);

    private string LeastUsedLanding(IReadOnlyList<string> allowed)
    {
        var best = allowed[0];
        foreach (var r in allowed)
        {
            if (_arrivalsPerRunway.GetValueOrDefault(r) < _arrivalsPerRunway.GetValueOrDefault(best))
                best = r;
        }
        return best;
    }

    /// <summary>Vuelos en el aire ahora (posición y altitud), para no crear uno encima de otro.</summary>
    private readonly List<(GeoPoint Position, double AltitudeFt)> _airNow = [];

    /// <summary>Separación mínima al crear un vuelo: 8 NM o 2000 ft (APROXIMADO; el radar exige 3-5 NM).</summary>
    public const double SpawnSeparationNm = 8;
    public const double SpawnSeparationFt = 2000;

    private bool TooClose(GeoPoint position, double altitudeFt)
    {
        foreach (var (p, alt) in _airNow)
        {
            if (Math.Abs(alt - altitudeFt) < SpawnSeparationFt && GeoMath.DistanceM(p, position) < SpawnSeparationNm * Nm)
                return true;
        }
        return false;
    }

    public async Task UpdateAsync(DirectorContext ctx, CancellationToken ct)
    {
        _lastContext = ctx;
        _airNow.Clear();
        foreach (var a in _engine.Snapshot())
        {
            if (a.Airborne)
                _airNow.Add((a.Position, a.AltitudeFt));
        }
        if (ctx.Airport?.Icao != _airportIcao)
        {
            _airportIcao = ctx.Airport?.Icao;
            _initialFillDone = false;
            _nextArrival = _nextDeparture = ctx.UtcNow;
        }
        if (!await EnsureRoutesAsync(ctx.Pilot).ConfigureAwait(false))
            return;
        var routes = _routes!;

        if (ctx.Airport is { } airport && ctx.ActiveRunway is { } runway)
        {
            ReplanArrivals(airport, ctx);
            TakeOverArrivals(airport, ctx, runway);
            var rate = HourlyRate(airport, ctx, routes.ArrivalsTo(airport.Icao).Count);
            if (!_initialFillDone)
            {
                if (_initialFillStart == DateTime.MinValue || _initialFillAirport != airport.Icao)
                {
                    (_initialFillStart, _initialFillAirport) = (ctx.UtcNow, airport.Icao);
                    (_initialCreated, _initialDistance) = (0, InitialFirstNm);
                }
                _initialFillDone = true;
                await InitialArrivalsAsync(ctx, routes, airport, runway, rate, ct).ConfigureAwait(false);
            }
            if (ctx.UtcNow >= _nextArrival && rate > 0)
            {
                // Flujo regulado (ADR-130), en cualquier aeropuerto: no entra otra si hay alguna en espera hacia aquí, o si
                // ya vienen las que caben. Se vuelve a mirar al minuto.
                var (inbound, holding) = InboundTo(airport.Icao);
                var most = Math.Max(2, (int)Math.Ceiling(rate * InboundShare));
                if (holding > 0 || inbound >= most)
                {
                    if (holding > 0 && (ctx.UtcNow - _lastMeterLog).TotalMinutes >= 5)
                    {
                        _lastMeterLog = ctx.UtcNow;
                        _log(Tr.F("{0}: {1} en espera y {2} llegadas en camino: no entran más hasta que baje.", airport.Icao, holding, inbound));
                    }
                    _nextArrival = ctx.UtcNow + TimeSpan.FromSeconds(60);
                }
                // Una llegada nueva entra a la ventana por su ruta (lejos: nadie la ve aparecer).
                else if (await TryArrivalAsync(ctx, routes, airport, runway, _rng.Next(150, 190), ct).ConfigureAwait(false))
                    _nextArrival = ctx.UtcNow + Jitter(3600 / rate);
            }
            ScheduleDepartures(ctx, routes, airport, rate);
            await PlanParkedAsync(ctx, airport, ct).ConfigureAwait(false);
            if (ctx.Departures && LaunchDeparture is not null)
                await TryDepartureAsync(ctx, airport, ct).ConfigureAwait(false);
        }

        await LoadPendingAirportsAsync(ct).ConfigureAwait(false);
        await FillWindowAsync(ctx, ct).ConfigureAwait(false);
        Status = Tr.F("{0} vuelos activos ({1} de paso), {2} rutas en la zona", _active.Count, _window, routes.LegCount);
    }

    /// <summary>Carga las rutas de la zona (si no están) sin decidir nada.</summary>
    public Task<bool> EnsureZoneAsync(GeoPoint pilot) => EnsureRoutesAsync(pilot);

    /// <summary>
    /// Lista de rutas de un aeropuerto armada con la base mundial (para estacionar aviones donde no hay lista manual,
    /// RF-11 y RF-12d): aerolíneas que salen de ahí y sus destinos. null si la zona no está cargada o no hay salidas.
    /// </summary>
    public RouteList? RouteListFor(string icao)
    {
        var legs = _routes?.DeparturesFrom(icao);
        if (legs is null || legs.Count == 0)
            return null;
        var airlines = legs.GroupBy(l => l.Airline)
            .Select(g => new RouteAirline(g.Key, g.Key, true, false, [], g.Select(l => l.Destination).Distinct().ToList(), []))
            .ToList();
        return new RouteList(icao, new RouteSource("vradarserver/standing-data (CC0)", "https://github.com/vradarserver/standing-data", 0,
            "2026-11-01"), Reviewed: false, "Generada de la base mundial de rutas por indicativo.", airlines);
    }

    /// <summary>Carga en segundo plano las rutas de la zona del piloto; la vuelve a cargar al alejarse 100 NM.</summary>
    private async Task<bool> EnsureRoutesAsync(GeoPoint pilot)
    {
        if (_routes is { } r && GeoMath.DistanceM(r.Center, pilot) < 100 * Nm)
            return true;
        if (_routesLoading is null)
        {
            _routesLoading = Task.Run(() =>
            {
                var radius = (WindowNm + 150) * Nm;
                var zone = _airports.Within(pilot, radius).Select(a => a.Icao).ToHashSet(StringComparer.Ordinal);
                var routes = WorldRoutes.LoadZone(_routesPath, zone, pilot, radius);
                var near = _airports.Within(pilot, WindowNm * Nm).Select(a => a.Icao).ToHashSet(StringComparer.Ordinal);
                // Solo los que de verdad cruzan la ventana (antes: todo lo que tocaba un aeropuerto cercano, y Mendoza-Buenos
                // Aires llenaba el lado argentino sin acercarse nunca a Chile).
                var list = new List<(WorldLeg, double)>();
                foreach (var l in routes.All)
                {
                    if (_airports.Find(l.Origin) is not { } o || _airports.Find(l.Destination) is not { } d)
                        continue;
                    var pass = ClosestApproachNm(o.Position, d.Position, pilot);
                    if (pass < WindowNm)
                        list.Add((l, pass));
                }
                _otherLegs = list;
                _routes = routes;
                _log(Tr.F("Rutas de la zona: {0} tramos de {1} aeropuertos.", routes.LegCount, zone.Count));
            });
        }
        try
        {
            await _routesLoading.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(Tr.F("No se pudo leer la base de rutas ({0}).", ex.Message));
            return false;
        }
        finally
        {
            _routesLoading = null;
        }
        return _routes is not null;
    }

    /// <summary>
    /// Tramos de la base de rutas por cada vuelo diario real (ADR-088). Calibrado contra llegadas diarias reales aproximadas:
    /// SCEL 1369 tramos / ~250, KMIA 2076 / ~550, KJFK 2219 / ~650, SAEZ 494 / ~150, SCIE 149 / ~20. APROXIMADO.
    /// </summary>
    public const double RouteLegsPerDailyFlight = 4.5;

    /// <summary>Suma de los factores horarios del día (5 h × 0,25 + 1,5 h × 0,6 + 16 h × 1 + 1,5 h × 0,5).</summary>
    private const double DayWeight = 18.9;

    /// <summary>
    /// Llegadas por hora que caben en una pista sin esperas (ADR-129: 14; ADR-145: 20). Con 5 NM de separación a 160 kt caben
    /// ~28 y el flujo no llega parejo; el Headless kewr-busy descargó 30 por hora por una pista sin esperas, así que 20 deja
    /// margen. Con 30 por pista, Barajas pedía 60 por hora y casi todas terminaban en espera. APROXIMADO.
    /// </summary>
    public const double ArrivalsPerRunwayHour = 20;

    /// <summary>
    /// "Promedio bajo" (pedido de Marco, ADR-130: 0,6; ADR-145: 0,8, con la segunda pista abierta ya no hay esperas): de las
    /// llegadas por hora que dan los horarios reales se pone esta parte.
    /// Con más, llegan desincronizadas y terminan en espera; un cielo con menos aviones y sin esperas se ve más real. APROXIMADO.
    /// </summary>
    public const double LowAverage = 0.8;

    /// <summary>Llegadas en camino a la vez, como parte de las de una hora (las que caben en ~40 minutos de vuelo). APROXIMADO.</summary>
    public const double InboundShare = 0.65;

    /// <summary>
    /// Llegadas por hora del aeropuerto (y otras tantas salidas), ADR-088: según cuántos vuelos diarios reales tiene (la base
    /// de rutas reales: un aeropuerto chico como SCIE recibe ~2 por hora, SCEL ~16 y KMIA ~25 en hora punta), repartidos por
    /// la hora local aproximada (por la longitud), con tope por pistas y puestos, y por la densidad. Sin rutas en la base,
    /// según sus puestos de pasajeros. APROXIMADO.
    /// </summary>
    public static double HourlyRate(AirportData airport, DirectorContext ctx, int routeLegsIn = 0)
    {
        var gates = airport.Parkings.Count(p => p.Type is ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy
            or ParkingType.GateExtra);
        var localHour = (ctx.UtcNow.Hour + ctx.UtcNow.Minute / 60.0 + airport.Reference.LonDeg / 15 + 24) % 24;
        var factor = localHour switch
        {
            < 5 => 0.25,
            < 6.5 => 0.6,
            < 22.5 => 1,
            _ => 0.5,
        };
        double peak;
        if (routeLegsIn > 0)
        {
            // Pistas cruzadas o paralelas cercanas no suman capacidad completa: como mucho, dos pistas de llegadas.
            // Por pista de llegada realmente en uso (ADR-131): una, o dos con las paralelas abiertas. Antes contaba las pistas
            // del aeropuerto, y con una sola para aterrizar (Kuala Lumpur, Newark en el lote) el tope de dos la saturaba.
            var runways = Math.Clamp(ctx.LandingRunways?.Count ?? 1, 1, Math.Clamp(Atc.ActiveRunwaySelector.AirlineRunwayCount(airport), 1, 2));
            var cap = Math.Min(ArrivalsPerRunwayHour * runways, Math.Max(2, gates * 0.6));
            peak = Math.Clamp(routeLegsIn / RouteLegsPerDailyFlight / DayWeight * LowAverage, 0.5, cap);
        }
        else
        {
            peak = Math.Clamp(gates * 0.25 * LowAverage, 1, 12);
        }
        return peak * factor * Math.Clamp(ctx.Density, 0, 300) / 100.0;
    }

    /// <summary>Llegadas por hora previstas ahora en <paramref name="airport"/> (para dejar puestos libres al estacionar).</summary>
    public double ArrivalsPerHour(AirportData airport, int density)
    {
        // Con las pistas de llegada que el aeropuerto PUEDE abrir (hasta dos), no con la que está en uso: si no, la demanda
        // salía con el tope de una pista, el reparto veía poca demanda y dejaba una sola pista mixta, que a su vez topaba la
        // demanda (KEWR 2026-11-15: 12 llegadas por hora a las 18 Z, con 35-40 reales).
        var potential = new string[Math.Clamp(Atc.ActiveRunwaySelector.AirlineRunwayCount(airport), 1, 2)];
        var ctx = new DirectorContext(airport, null, null, airport.Reference, DateTime.UtcNow, [], new HashSet<int>(), density)
        {
            LandingRunways = potential,
        };
        return HourlyRate(airport, ctx, _routes?.ArrivalsTo(airport.Icao).Count ?? 0);
    }

    /// <summary>
    /// Salidas por hora según el horario de salidas (ADR-139): las STD de la próxima hora en el aeropuerto cargado, más las
    /// atrasadas que todavía no salen. Null si ese aeropuerto aún no tiene horario (entonces se usa la de llegadas, como
    /// antes). Solo cuenta; no cambia cuándo sale nadie ni qué puestos quedan libres.
    /// </summary>
    public double? ScheduledDeparturesPerHour(string icao, DateTime utcNow)
    {
        if (_lastContext?.Airport?.Icao != icao || _schedule.IsEmpty)
            return null;
        var limit = utcNow.AddHours(1);
        var count = 0;
        foreach (var s in _schedule.Values)
        {
            if (s.Std <= limit)
                count++;
        }
        return count;
    }

    private DateTime _lastMeterLog;

    /// <summary>Cuántas llegadas vienen a ese aeropuerto (en el motor o por entrar) y cuántas de ellas están en espera.</summary>
    private (int Inbound, int Holding) InboundTo(string icao)
    {
        var inbound = 0;
        var holding = 0;
        foreach (var f in _engine.AirborneNow())
        {
            if (!f.ControlledArrival || f.Destination != icao)
                continue;
            inbound++;
            if (f.Phase == Flight.FlightPhase.Holding)
                holding++;
        }
        var planned = _active.Values.Count(f => !f.Window && f.Leg.Destination == icao);
        return (Math.Max(inbound, planned), holding);
    }

    private TimeSpan Jitter(double seconds) => TimeSpan.FromSeconds(seconds * (0.7 + 0.6 * _rng.NextDouble()));

    // --- Llegadas al aeropuerto cargado (etapa 7) ---

    /// <summary>Al empezar: llegadas ya en camino, repartidas entre 35 y 170 NM por su ruta (≈ 40 min de llegadas).</summary>
    private async Task InitialArrivalsAsync(DirectorContext ctx, WorldRoutes routes, AirportData airport, string runway, double rate,
        CancellationToken ct)
    {
        // Las ya creadas en vueltas anteriores cuentan (ADR-129): al pasar al motor dejan de estar en _active, y cada vuelta
        // volvía a pedir todas y a ponerlas desde las 35 NM, encima de las anteriores (vuelo LEMG-LEMD: 112 llegadas a
        // Barajas en 8 minutos y 54 esperas).
        var inbound = _active.Values.Count(f => !f.Window && f.Leg.Destination == airport.Icao);
        var wanted = (int)Math.Round(rate * InitialShare) - Math.Max(inbound, _initialCreated);
        var distance = _initialDistance;
        var created = 0;
        var budget = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < wanted && distance < 170 && budget.Elapsed.TotalSeconds < 60; i++)
        {
            if (await TryArrivalAsync(ctx, routes, airport, runway, distance, ct).ConfigureAwait(false))
                created++;
            // El hueco de la hora (~260 kt de media) y nunca menos de InitialMinGapNm entre una y la siguiente.
            distance += Math.Max(InitialMinGapNm, 260 / Math.Max(rate, 1)) + _rng.Next(0, 10);
        }
        _initialCreated += created;
        _initialDistance = distance;
        _nextArrival = ctx.UtcNow + Jitter(3600 / Math.Max(rate, 0.1));
        _nextDeparture = ctx.UtcNow + TimeSpan.FromSeconds(_rng.Next(30, 120));
        if (created > 0)
            _log(Tr.F("{0}: {1} llegadas en camino ({2:0.#} por hora, pista {3}).", airport.Icao, created, rate, runway));
        // Faltan (los procedimientos de los orígenes todavía se están leyendo): otra vuelta, hasta tener ~80 % de las que pide
        // la hora o pasados unos minutos (prueba de Marco en RKSI, ADR-115: con una sola creada se daba por terminado y el
        // cielo quedaba vacío; las siguientes entraban de a una cada pocos minutos a 150-190 NM).
        if (wanted > 0 && created < wanted * 0.8 && distance < 170 && (ctx.UtcNow - _initialFillStart).TotalMinutes < InitialFillMinutes)
            _initialFillDone = false;
    }

    /// <summary>Una llegada al aeropuerto cargado que está a <paramref name="remainingNm"/> NM del umbral por su ruta.</summary>
    private async Task<bool> TryArrivalAsync(DirectorContext ctx, WorldRoutes routes, AirportData airport, string runway,
        double remainingNm, CancellationToken ct)
    {
        var legs = routes.ArrivalsTo(airport.Icao);
        for (var attempt = 0; attempt < 12 && legs.Count > 0; attempt++)
        {
            var leg = legs[_rng.Next(legs.Count)];
            if (_active.ContainsKey(leg.Callsign) || _airports.Find(leg.Origin) is not { } origin)
                continue;
            if (GeoMath.DistanceM(origin.Position, airport.Reference) / Nm < Math.Max(60, remainingNm * 0.7))
                continue;
            // Sus procedimientos de salida se leen primero (unos segundos); el vuelo se crea en una vuelta siguiente.
            if (!AirportReady(leg.Origin))
                continue;
            if (await CreateArrivalAsync(ctx, airport, runway, leg, origin, remainingNm, ct).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Crea una llegada con puesto reservado (RF-12c): su plan completo desde su origen (con la pista de llegada en uso que da
    /// la ruta más corta) y el avión en el punto de ese plan a <paramref name="remainingNm"/> NM del umbral.
    /// </summary>
    private async Task<bool> CreateArrivalAsync(DirectorContext ctx, AirportData airport, string runway, WorldLeg leg, WorldAirport origin,
        double remainingNm, CancellationToken ct)
    {
        var match = PickModel(leg.Airline, null, leg.Callsign);
        if (match is null)
            return false;
        var category = AircraftPerformance.For(match.Model).Category;
        var stand = PickStand(ctx, airport, match.Model, leg.Airline);
        if (stand < 0)
            return false;
        var originData = OtherAirport(leg.Origin);
        PlannedFlight? plan = null;
        LandingRunway? landing = null;
        ResolvedRoute? route = null;
        foreach (var ident in ctx.LandingRunways is { Count: > 0 } ends ? ends : [runway])
        {
            if (FlightPlanner.LandingFor(airport, ctx.Graph, ident, category) is not { } l)
                continue;
            var e = new FlightEnds(leg.Origin, origin.Position, origin.ElevationFt, originData, DepartureRunwayFor(originData, ctx),
                airport.Icao, airport.Reference, airport.ElevationFt, airport, l);
            route ??= await RouteForAsync(e, ct).ConfigureAwait(false);
            var p = FlightPlanBuilder.Assemble(e, route, category);
            if (plan is null || p.Path.LengthM < plan.Path.LengthM)
            {
                plan = p;
                landing = l;
            }
        }
        if (plan is null || landing is null || plan.Path.LengthM < (remainingNm + 10) * Nm)
            return false;
        var along = plan.Path.LengthM - remainingNm * Nm;
        var (pos, heading, next, alt, climbing) = plan.At(along, origin.ElevationFt);
        if (GeoMath.DistanceM(pos, ctx.Pilot) < TrafficEngine.InjectDistanceM || TooClose(pos, alt))
            return false;
        if (!_reservedStands.TryAdd(stand, leg.Callsign))
            return false;
        // Sale de la pista por el lado de su puesto (no la cruza ni la rodea por el otro lado).
        landing = landing.TowardSide(airport.Parkings[stand].Position, FlightPlanner.MinRolloutM(category));
        _airNow.Add((pos, alt));
        var f = new AirborneAgent(leg.Callsign, leg.Airline, match.Model.IcaoType, leg.Origin, airport.Icao, match.Model, category,
            plan.Path, pos, alt, heading, SpeedFor(category, alt, plan.CruiseFt), climbing ? 1500 : alt < plan.CruiseFt - 300 ? -1200 : 0,
            plan.CruiseFt, landing)
        {
            StartTarget = next,
            StandIndex = stand,
            ControlledArrival = true,
            OriginElevationFt = origin.ElevationFt,
            // Bajo control de Approach: mantiene su nivel hasta que lo autoricen a bajar (paso 7).
            ClearedAltitudeFt = Math.Floor(alt / 1000) * 1000,
        };
        _active[leg.Callsign] = new FlightInfo(leg, match, Window: false, route, plan);
        if (plan.Issues.Count > 0)
            _log(Tr.F("{0}: plan con avisos: {1}.", leg.Callsign, string.Join("; ", plan.Issues)));
        _engine.AddFlight(f);
        _log(Tr.F("{0}: llegada desde {1} ({2}, a {3:0} NM; {4} → ", leg.Callsign, leg.Origin, match.Model.IcaoType, remainingNm, plan.RouteText) +
             Tr.F("{0} {1} [{2}], puesto {3}).", plan.Approach ?? Tr.T("final recta"), landing.Ident, Tr.T(plan.Source), airport.Parkings[stand].DisplayName));
        return true;
    }

    /// <summary>Velocidad verdadera al aparecer: la de crucero arriba; 250-280 kt IAS abajo (≈ +2 % por cada 1000 ft).</summary>
    private static double SpeedFor(PerformanceCategory category, double altFt, double cruiseFt) =>
        altFt >= cruiseFt - 300 ? AirborneProfile.For(category).CruiseTasKt : (altFt > 10_000 ? 280 : 250) * (1 + 0.02 * altFt / 1000);

    /// <summary>
    /// La ruta en ruta del par (una vez por sesión: VATSIM, aerovías o directo). Sin aerovías del simulador (tests, sin
    /// conexión), directo y sin guardarla.
    /// </summary>
    private async Task<ResolvedRoute> RouteForAsync(FlightEnds e, CancellationToken ct)
    {
        // Por par y pistas: la ruta por aerovías va de la SID de esa pista a la STAR de la pista de llegada.
        var key = $"{e.OriginIcao}/{e.DepartureRunway}-{e.DestinationIcao}/{e.Landing?.Ident}";
        if (_routeCache.TryGetValue(key, out var cached)
            && (cached.Route.Source != ResolvedRoute.Direct.Source || (DateTime.UtcNow - cached.At).TotalMinutes < RetryDirectMinutes))
            return cached.Route;
        if (Airways is null)
            return ResolvedRoute.Direct;
        var route = await FlightPlanBuilder.ResolveRouteAsync(e, Airways, Nav, Vatsim, _log, ct).ConfigureAwait(false);
        _routeCache[key] = (route, DateTime.UtcNow);
        _log(Tr.F("Ruta {0} ({1}): {2}", key, route.Source, route.Text));
        return route;
    }

    /// <summary>
    /// ¿Están leídos los procedimientos de ese aeropuerto? Si no, se pide leerlos y el vuelo espera a otra vuelta. Sin lector
    /// (tests), siempre listo (va sin procedimientos).
    /// </summary>
    private bool AirportReady(string icao)
    {
        if (LoadAirport is null || icao == _airportIcao || _otherAirports.ContainsKey(icao))
            return true;
        OtherAirport(icao);
        return false;
    }

    /// <summary>Pista de salida de un aeropuerto: la en uso si es el cargado; si no, la más alineada con ella (viento de la zona).</summary>
    private static string? DepartureRunwayFor(AirportData? a, DirectorContext ctx) =>
        a is null ? null : a.Icao == ctx.Airport?.Icao ? ctx.ActiveRunway : RunwayAlignedWith(a, ctx, ctx.ActiveRunway);

    /// <summary>
    /// La cabecera de <paramref name="a"/> más alineada con <paramref name="reference"/> del aeropuerto cargado (APROXIMADO: el
    /// viento de la zona es el de tu posición; a igual rumbo, la pista más larga); sin referencia, la más larga.
    /// </summary>
    private static string? RunwayAlignedWith(AirportData a, DirectorContext ctx, string? reference)
    {
        double? want = null;
        if (ctx.Airport is { } here && reference is not null)
        {
            foreach (var r in here.Runways)
            {
                if (r.Primary.Ident == reference)
                    want = r.Primary.HeadingTrueDeg;
                else if (r.Secondary.Ident == reference)
                    want = r.Secondary.HeadingTrueDeg;
            }
        }
        string? best = null;
        var bestScore = double.MaxValue;
        foreach (var r in a.Runways)
        {
            foreach (var end in new[] { r.Primary, r.Secondary })
            {
                var score = want is { } h ? Math.Round(Math.Abs(GeoMath.AngleDiffDeg(h, end.HeadingTrueDeg)) / 30) * 100_000 - r.LengthM : -r.LengthM;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = end.Ident;
                }
            }
        }
        return best;
    }

    /// <summary>Los extremos de un vuelo (con sus datos del simulador si están leídos) y sus pistas.</summary>
    private FlightEnds EndsFor(WorldLeg leg, WorldAirport origin, WorldAirport dest, PerformanceCategory category, DirectorContext ctx,
        string? departureRunway = null)
    {
        var o = leg.Origin == ctx.Airport?.Icao ? ctx.Airport : OtherAirport(leg.Origin);
        var d = leg.Destination == ctx.Airport?.Icao ? ctx.Airport : OtherAirport(leg.Destination);
        LandingRunway? landing = null;
        if (d is not null)
        {
            var ident = d.Icao == ctx.Airport?.Icao && ctx.LandingRunways is { Count: > 0 } ends ? LeastUsedLanding(ends)
                : RunwayAlignedWith(d, ctx, ctx.LandingRunways is { Count: > 0 } le ? le[0] : ctx.ActiveRunway);
            if (ident is not null)
                landing = FlightPlanner.LandingFor(d, d.Icao == ctx.Airport?.Icao ? ctx.Graph : null, ident, category);
        }
        return new FlightEnds(leg.Origin, origin.Position, origin.ElevationFt, o, departureRunway ?? DepartureRunwayFor(o, ctx),
            leg.Destination, dest.Position, dest.ElevationFt, d, landing);
    }

    /// <summary>Puesto libre donde quepa el avión: de su aerolínea si hay, si no cualquiera de pasajeros; lejos del piloto.</summary>
    /// <summary>
    /// ¿El puesto está libre? No basta el índice: hay puestos que se superponen en los datos (el mismo lugar como puesto
    /// mediano y pesado). Ocupado si se toca con un puesto ocupado, reservado o con un estacionado (prueba de Marco,
    /// 2026-11-01: llegadas en puestos ya ocupados).
    /// </summary>
    private bool StandFree(AirportData airport, int index, DirectorContext ctx, int ignoreReservedFor = -1)
    {
        var p = airport.Parkings[index];
        bool Touches(GeoPoint other, double radius) => GeoMath.DistanceM(p.Position, other) < p.RadiusM + radius - 2;
        foreach (var q in ctx.Parked)
        {
            if (Touches(q.Position, q.Aircraft.Parking.RadiusM))
                return false;
        }
        foreach (var b in ctx.BusyStands)
        {
            if (b == index || Touches(airport.Parkings[b].Position, airport.Parkings[b].RadiusM))
                return false;
        }
        foreach (var r in _reservedStands.Keys)
        {
            if (r != ignoreReservedFor && (r == index || Touches(airport.Parkings[r].Position, airport.Parkings[r].RadiusM)))
                return false;
        }
        return true;
    }

    private DirectorContext? _lastContext;

    private int PickStand(DirectorContext ctx, AirportData airport, AircraftModel model, string airline)
    {
        var halfSpan = _matcher.WingspanFor(model) / 2;
        var cargo = ModelMatcher.IsCargo(model);
        // Primero puertas y plataformas de aerolínea; si no queda ninguna, un puesto remoto de respaldo con más holgura
        // (ADR-089: en SCIE, 4 puertas, una llegada se quedó sin puesto y pasó de largo sobre el aeropuerto).
        for (var pass = 0; pass < 2; pass++)
        {
            var best = -1;
            var bestScore = double.MaxValue;
            for (var i = 0; i < airport.Parkings.Count; i++)
            {
                var p = airport.Parkings[i];
                var ok = pass == 0
                    ? (ParkedFleetPlanner.Gate(p) || p.Type is ParkingType.RampGaLarge or ParkingType.RampGaExtra or ParkingType.RampCargo)
                      && ParkedFleetPlanner.Fits(p, halfSpan)
                    : ParkedFleetPlanner.RemoteStand(p) && ParkedFleetPlanner.Fits(p, halfSpan, ParkedFleetPlanner.RemoteToleranceM);
                if (!ok || !StandFree(airport, i, ctx) || ctx.Graph is { } g && !g.StandReachable(i))
                    continue;
                if (GeoMath.DistanceM(p.Position, ctx.Pilot) < ParkedFleetPlanner.UserClearanceM + halfSpan + 20)
                    continue;
                // Preferir su aerolínea, su clase (carga o pasajeros) y el puesto más justo (los grandes quedan para los
                // grandes); algo de azar.
                var score = (p.Airlines.Contains(airline) ? 0 : 50) + ParkedFleetPlanner.KindPenalty(p, cargo)
                    + Math.Abs(p.RadiusM - halfSpan) + _rng.NextDouble() * 10;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best >= 0)
                return best;
        }
        return -1;
    }

    // --- Salidas del aeropuerto cargado (etapa 6) ---

    /// <summary>
    /// Horario de salidas (paso 6, prueba de Marco 2026-11-01): cada estacionado tiene su vuelo (indicativo real de su
    /// aerolínea desde este aeropuerto y destino) y su hora de salida (STD, generada), separadas según la tasa por hora.
    /// Sale a esa hora. Una llegada estacionada vuelve, si la base lo tiene, a su origen tras la escala.
    /// </summary>
    private void ScheduleDepartures(DirectorContext ctx, WorldRoutes routes, AirportData airport, double rate)
    {
        var legs = routes.DeparturesFrom(airport.Icao);
        if (legs.Count == 0 || rate <= 0)
            return;
        var spacing = TimeSpan.FromSeconds(3600 / rate);
        foreach (var p in ctx.Parked)
        {
            // La decoración (aviación general, remotos "de noche") no sale nunca (ADR-109).
            if (p.Aircraft.Decor || _schedule.ContainsKey(p.ObjectId))
                continue;
            var leg = PickDepartureLeg(legs, p.Aircraft.Airline, p.Aircraft.TailNumber, airport.Icao, returnTo: null);
            if (leg is null)
                continue;
            // El próximo hueco libre, desde unos minutos adelante (los estacionados al iniciar "embarcan").
            var std = _lastStd < ctx.UtcNow.AddMinutes(4) ? ctx.UtcNow.AddMinutes(4 + _rng.Next(0, 4)) : _lastStd + Jitter(spacing.TotalSeconds);
            _lastStd = std;
            _schedule[p.ObjectId] = new DepartureSchedule(leg.Callsign, leg.Destination, std);
            _scheduledLegs[p.ObjectId] = leg;
            OtherAirport(leg.Destination); // sus procedimientos estarán leídos a la hora de salir
        }
        RotateStands(ctx, airport, rate);
    }

    /// <summary>Cada cuánto se revisa la reserva de puestos libres (s).</summary>
    public const double RotationCheckS = 60;

    /// <summary>Puestos de aerolínea libres además de las llegadas de la próxima hora.</summary>
    public const int RotationMargin = 2;

    private DateTime _nextRotationCheck = DateTime.MinValue;

    /// <summary>
    /// Margen de puestos libres: <see cref="RotationMargin"/> en aeropuertos con 20 puestos de aerolínea o más; en los chicos,
    /// ninguno (SCIE tiene 4 puertas: con el margen quedaba vacío).
    /// </summary>
    public static int MarginFor(AirportData airport)
    {
        var stands = 0;
        foreach (var p in airport.Parkings)
        {
            if (ParkedFleetPlanner.AirlineStand(p))
                stands++;
        }
        return stands >= 20 ? RotationMargin : 0;
    }

    /// <summary>
    /// Rotación de puestos (ADR-109): si quedan menos puestos de aerolínea libres que las llegadas de la próxima hora más
    /// <see cref="RotationMargin"/>, se adelanta la próxima salida programada (a unos minutos). Así el aeropuerto nunca se
    /// llena del todo: lo que sale libera el puesto para lo que llega.
    /// </summary>
    private void RotateStands(DirectorContext ctx, AirportData airport, double rate)
    {
        if (ctx.UtcNow < _nextRotationCheck)
            return;
        _nextRotationCheck = ctx.UtcNow.AddSeconds(RotationCheckS);
        var free = 0;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var p = airport.Parkings[i];
            if ((ParkedFleetPlanner.Gate(p) || p.Type is ParkingType.RampGaLarge or ParkingType.RampGaExtra or ParkingType.RampCargo)
                && StandFree(airport, i, ctx))
                free++;
        }
        var reserve = (int)Math.Ceiling(rate) + MarginFor(airport);
        if (free >= reserve)
            return;
        uint? nextId = null;
        var nextStd = DateTime.MaxValue;
        foreach (var p in ctx.Parked)
        {
            if (!p.Aircraft.Decor && _schedule.TryGetValue(p.ObjectId, out var s) && s.Std < nextStd)
                (nextId, nextStd) = (p.ObjectId, s.Std);
        }
        if (nextId is not { } id || nextStd <= ctx.UtcNow.AddMinutes(6))
            return;
        var earlier = ctx.UtcNow.AddMinutes(3 + _rng.Next(0, 3));
        _schedule[id] = _schedule[id] with { Std = earlier };
        _log(Tr.F("Rotación de puestos: {0} libres (reserva {1}); {2} sale antes ({3:HH:mm} UTC).", free, reserve, _schedule[id].Callsign, earlier));
    }

    private DateTime _lastStd = DateTime.MinValue;

    /// <summary>
    /// Un tramo de salida libre para esa aerolínea: si <paramref name="returnTo"/> viene, de preferencia el de vuelta a ese
    /// aeropuerto. Sin ruta de su aerolínea en la base, su indicativo y un destino real del aeropuerto.
    /// </summary>
    private WorldLeg? PickDepartureLeg(IReadOnlyList<WorldLeg> legs, string airline, string fallbackCallsign, string origin, string? returnTo)
    {
        var taken = new HashSet<string>(_schedule.Values.Select(v => v.Callsign), StringComparer.Ordinal);
        var own = legs.Where(l => l.Airline == airline && !_active.ContainsKey(l.Callsign) && !taken.Contains(l.Callsign)).ToList();
        if (returnTo is not null && own.Where(l => l.Destination == returnTo).ToList() is { Count: > 0 } back)
            return back[_rng.Next(back.Count)];
        if (own.Count > 0)
            return own[_rng.Next(own.Count)];
        if (legs.Count == 0)
            return null;
        var any = legs[_rng.Next(legs.Count)];
        return new WorldLeg(fallbackCallsign, airline, origin, returnTo ?? any.Destination);
    }

    /// <summary>Lanza el estacionado cuya hora de salida ya llegó (el más atrasado primero).</summary>
    private async Task TryDepartureAsync(DirectorContext ctx, AirportData airport, CancellationToken ct)
    {
        // Máximo de aviones moviéndose en tierra (ADR-110): la salida espera a que se libere (rodando, alineados, al puesto).
        if (_engine.GroundMovingCount >= _engine.Limits.MaxGround)
            return;
        foreach (var p in ctx.Parked.OrderBy(p => _schedule.TryGetValue(p.ObjectId, out var s) ? s.Std : DateTime.MaxValue))
        {
            if (!_schedule.TryGetValue(p.ObjectId, out var sched) || sched.Std > ctx.UtcNow)
                return;
            if (GeoMath.DistanceM(p.Position, ctx.Pilot) <= 40 || !_scheduledLegs.TryGetValue(p.ObjectId, out var leg))
                continue; // no empujar al lado del piloto
            if (_airports.Find(leg.Destination) is not { } dw || ctx.ActiveRunway is null)
                continue;
            // Los procedimientos del destino se leen antes (la STAR y la aproximación son parte del plan).
            if (!AirportReady(leg.Destination))
                continue;
            // Su plan completo ya está armado desde que tuvo hora (si no, se arma ahora).
            if (!_parkedPlans.TryGetValue(p.ObjectId, out var pp) || pp.Callsign != leg.Callsign)
            {
                await PlanOneAsync(p, leg, airport, ctx, ct).ConfigureAwait(false);
                _parkedPlans.TryGetValue(p.ObjectId, out pp);
            }
            _active[leg.Callsign] = new FlightInfo(leg, p.Aircraft.Match, Window: false, pp?.Route, pp?.Plan, pp?.Runway);
            if (await LaunchDeparture!(p, leg.Callsign).ConfigureAwait(false))
            {
                _parkedPlans.TryRemove(p.ObjectId, out _);
                _schedule.TryRemove(p.ObjectId, out _);
                _scheduledLegs.TryRemove(p.ObjectId, out _);
                _parkedSince.TryRemove(p.ObjectId, out _);
                _log(Tr.F("{0}: salida a {1} desde {2} (STD {3:HH:mm} UTC).", leg.Callsign, leg.Destination, p.Aircraft.Parking.DisplayName, sched.Std));
                return;
            }
            _active.TryRemove(leg.Callsign, out _);
            // No pudo (sin ruta a la pista): se corre 5 min.
            _schedule[p.ObjectId] = sched with { Std = ctx.UtcNow.AddMinutes(5) };
        }
    }

    private readonly ConcurrentDictionary<uint, DepartureSchedule> _schedule = new();
    private readonly ConcurrentDictionary<uint, WorldLeg> _scheduledLegs = new();

    /// <summary>Fijos del simulador con sus aerovías (ADR-058); sin esto, en ruta va directo.</summary>
    public AirwayDatabase? Airways { get; set; }

    /// <summary>Todos los fijos del simulador (ADR-062), para resolver las rutas escritas; null mientras no se cargan.</summary>
    public NavIndex? Nav { get; set; }

    /// <summary>Rutas de VATSIM por par de aeropuertos (ADR-061); null = no se usan.</summary>
    public VatsimRouteStore? Vatsim { get; set; }

    /// <summary>El estacionado cambió de objeto en el simulador (llegada convertida en estacionado): su horario lo sigue.</summary>
    public void Rekey(uint oldId, uint newId)
    {
        if (_parkedPlans.TryRemove(oldId, out var plan))
            _parkedPlans[newId] = plan;
        if (_schedule.TryRemove(oldId, out var s))
            _schedule[newId] = s;
        if (_scheduledLegs.TryRemove(oldId, out var l))
            _scheduledLegs[newId] = l;
        if (_parkedSince.TryRemove(oldId, out var t))
            _parkedSince[newId] = t;
    }

    /// <summary>El tramo (origen y destino) de un vuelo activo por su indicativo, o null.</summary>
    public WorldLeg? LegFor(string callsign) => _active.TryGetValue(callsign, out var f) ? f.Leg : null;

    /// <summary>El vuelo y la hora de salida de un estacionado, o null si todavía no tiene.</summary>
    public DepartureSchedule? ScheduleFor(uint objectId) => _schedule.TryGetValue(objectId, out var s) ? s : null;

    /// <summary>El plan de vuelo completo de un estacionado con hora (para su ficha), o null si todavía no está.</summary>
    public PlannedFlight? PlanFor(uint objectId) => _parkedPlans.TryGetValue(objectId, out var p) ? p.Plan : null;

    /// <summary>El plan con que se creó un vuelo activo (por qué cada procedimiento, lo que encontró el validador), o null.</summary>
    public PlannedFlight? PlanForCallsign(string callsign) => _active.TryGetValue(callsign, out var f) ? f.Plan : null;

    /// <summary>
    /// Arma el plan completo de los estacionados que ya tienen vuelo y hora (paso a paso, unos segundos por vuelta): ruta,
    /// SID, STAR y aproximación del destino, con la pista en uso. Si la pista cambia, se rearma. Así su ficha lo muestra
    /// antes de salir y al despegar se vuela ese mismo plan.
    /// </summary>
    private async Task PlanParkedAsync(DirectorContext ctx, AirportData airport, CancellationToken ct)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var p in ctx.Parked)
        {
            if (budget.Elapsed.TotalSeconds > 15)
                return;
            if (!_scheduledLegs.TryGetValue(p.ObjectId, out var leg))
                continue;
            // Ya planeado con esa pista; si quedó en directo, se vuelve a intentar (la caché espera unos minutos entre intentos).
            if (_parkedPlans.TryGetValue(p.ObjectId, out var have) && have.Callsign == leg.Callsign && have.Runway == ctx.ActiveRunway
                && (have.Route.Source != ResolvedRoute.Direct.Source || (DateTime.UtcNow - have.At).TotalMinutes < RetryDirectMinutes))
                continue;
            if (!AirportReady(leg.Destination))
                continue;
            await PlanOneAsync(p, leg, airport, ctx, ct).ConfigureAwait(false);
        }
    }

    private async Task PlanOneAsync(InjectedParked p, WorldLeg leg, AirportData airport, DirectorContext ctx, CancellationToken ct)
    {
        if (_airports.Find(leg.Destination) is not { } dw || ctx.ActiveRunway is not { } runway)
            return;
        var category = AircraftPerformance.For(p.Aircraft.Match.Model).Category;
        var origin = new WorldAirport(airport.Icao, airport.Reference, airport.ElevationFt);
        var ends = EndsFor(leg, origin, dw, category, ctx, runway);
        var route = await RouteForAsync(ends, ct).ConfigureAwait(false);
        var plan = FlightPlanBuilder.Assemble(ends, route, category);
        _parkedPlans[p.ObjectId] = new ParkedPlan(leg.Callsign, route, plan, runway, DateTime.UtcNow);
        if (plan.Issues.Count > 0)
            _log(Tr.F("{0}: plan con avisos: {1}.", leg.Callsign, string.Join("; ", plan.Issues)));
    }

    /// <summary>
    /// Relevo del motor (en su hilo): la salida que despegó sigue su plan completo, armado ahora con la pista real por la que
    /// salió: SID, ruta, STAR y aproximación de su destino.
    /// </summary>
    public AirborneAgent? OnDepartureAirborne(DepartureAgent d, AirportData airport)
    {
        if (!_active.TryGetValue(d.Callsign, out var info))
        {
            // Lanzada a mano: un destino real del aeropuerto.
            var legs = _routes?.DeparturesFrom(airport.Icao);
            if (legs is null || legs.Count == 0)
                return null;
            var any = legs[_rng.Next(legs.Count)];
            info = new FlightInfo(new WorldLeg(d.Callsign, d.Callsign.Length >= 3 ? d.Callsign[..3] : d.Callsign, airport.Icao, any.Destination),
                null, false);
            _active[d.Callsign] = info;
        }
        if (_airports.Find(info.Leg.Destination) is not { } dest || d.RunwayIdent is not { } runway)
            return null;
        var ctx = (_lastContext ?? new DirectorContext(airport, null, runway, airport.Reference, DateTime.UtcNow, [], new HashSet<int>(), 100))
            with { Airport = airport };
        var origin = new WorldAirport(airport.Icao, airport.Reference, airport.ElevationFt);
        var ends = EndsFor(info.Leg, origin, dest, d.Category, ctx, runway);
        var plan = info.Plan is { } ready && info.PlanRunway == runway ? ready
            : FlightPlanBuilder.Assemble(ends, info.Route ?? ResolvedRoute.Direct, d.Category);
        _active[d.Callsign] = info with { Plan = plan, PlanRunway = runway };
        var model = info.Match?.Model ?? _matcher.Match(info.Leg.Airline, d.TypeDesignator, d.Callsign)?.Model;
        _log(Tr.F("{0}: en el aire hacia {1}: {2} → {3} [{4}].", d.Callsign, dest.Icao, plan.RouteText, plan.Approach ?? Tr.T("sin aproximación"), plan.Source));
        return AirborneAgent.FromDeparture(d, info.Leg.Airline, airport.Icao, dest.Icao, model, plan.Path, plan.CruiseFt, airport.ElevationFt,
            dest.ElevationFt, ends.Landing);
    }

    /// <summary>Relevo del motor (en su hilo): la llegada que dejó la pista rueda a su puesto.</summary>
    public DepartureAgent? OnArrivalLanded(AirborneAgent f, AirportData airport, TaxiGraph graph)
    {
        if (f.Landing is not { } l || f.StandIndex < 0 || f.Model is not { } model)
        {
            _log(Tr.F("{0}: aterrizó sin {1}; no rueda a un puesto.", f.Callsign, (f.Landing is null ? "pista" : f.StandIndex < 0 ? "puesto" : "modelo")));
            return null;
        }
        // El puesto se vuelve a revisar al aterrizar (alguien pudo estacionarse cerca mientras volaba).
        if (_lastContext is { } ctx && !StandFree(airport, f.StandIndex, ctx, ignoreReservedFor: f.StandIndex)
            && PickStand(ctx, airport, model, f.Airline) is >= 0 and var other)
        {
            _reservedStands.TryRemove(f.StandIndex, out _);
            _reservedStands[other] = f.Callsign;
            _log(Tr.F("{0}: su puesto quedó ocupado; va a {1}.", f.Callsign, airport.Parkings[other].DisplayName));
            f.StandIndex = other;
        }
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
        {
            _log(Tr.F("{0}: sin salida de pista conocida en la {1}; no rueda a un puesto.", f.Callsign, l.Ident));
            return null;
        }
        var perf = AircraftPerformance.For(model);
        var agent = DepartureAgent.PlanArrival(f.ObjectId, f.Callsign, airport, graph, exit, f.StandIndex, f.Position, f.HeadingDeg,
            f.SpeedKt, perf, model.EngineCount, model.NoseGearM, _matcher.WingspanFor(model), model.Wake);
        if (agent is not null)
        {
            agent.TypeDesignator = f.TypeDesignator;
            return agent;
        }
        // Sin rodaje posible: se retira y su puesto queda libre (antes quedaba reservado para siempre).
        _reservedStands.TryRemove(f.StandIndex, out _);
        _active.TryRemove(f.Callsign, out _);
        _log(Tr.F("{0}: sin rodaje desde la salida {1} hasta su puesto; se retira.", f.Callsign, exit.TaxiwayName));
        return null;
    }

    /// <summary>La llegada quedó apagada en su puesto: pasa a estacionado (podrá volver a salir tras la escala).</summary>
    public InjectedParked? OnArrivalParked(DepartureAgent g, AirportData airport, DateTime utcNow)
    {
        _reservedStands.TryRemove(g.StandIndex, out _);
        _active.TryRemove(g.Callsign, out var info);
        if (g.StandIndex < 0 || info?.Match is not { } match)
            return null;
        _parkedSince[g.ObjectId] = utcNow;
        // Escala: ~40 min un pasillo único, ~90 un fuselaje ancho (APROXIMADO); vuelve a su origen si la base lo tiene.
        var turnaround = match.Model.Wake is WakeCategory.Heavy or WakeCategory.Super ? 90 : 40;
        if (_routes?.DeparturesFrom(airport.Icao) is { Count: > 0 } legs
            && PickDepartureLeg(legs, info.Leg.Airline, g.Callsign, airport.Icao, returnTo: info.Leg.Origin) is { } back)
        {
            _schedule[g.ObjectId] = new DepartureSchedule(back.Callsign, back.Destination, utcNow.AddMinutes(turnaround + _rng.Next(0, 15)));
            _scheduledLegs[g.ObjectId] = back;
        }
        var pose = g.Pose(1);
        var parked = new ParkedAircraft(info.Leg.Airline, match.Model.IcaoType, g.Callsign, match, g.StandIndex, airport.Parkings[g.StandIndex]);
        return new InjectedParked(g.ObjectId, parked, IsParkedAtc: false, pose.Position, pose.HeadingTrueDeg);
    }

    private void OnFlightFinished(AirborneAgent f)
    {
        _released.TryRemove(f, out _);
        // Si pasó al rodaje, el puesto sigue reservado hasta que se estacione.
        if (f.Phase == FlightPhase.Landed && f.Injected)
            return;
        if (_active.TryRemove(f.Callsign, out var info) && info.Window)
            Interlocked.Decrement(ref _window);
        foreach (var (stand, cs) in _reservedStands)
        {
            if (cs == f.Callsign)
                _reservedStands.TryRemove(stand, out _);
        }
    }

    // --- Tráfico de la ventana: otros aeropuertos y de paso (etapa 6b) ---

    private async Task FillWindowAsync(DirectorContext ctx, CancellationToken ct)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var target = (int)Math.Round(Math.Clamp(ctx.Density, 0, 300) / 100.0 * 12);
        for (var tries = 0; _window < target && tries < 40 && _otherLegs.Count > 0 && budget.Elapsed.TotalSeconds < 30; tries++)
        {
            // Se sortean tres y queda el que pasa más cerca: más tráfico cerca de ti, menos en el borde de la ventana.
            var leg = _otherLegs[_rng.Next(_otherLegs.Count)];
            for (var k = 0; k < 2; k++)
            {
                var other = _otherLegs[_rng.Next(_otherLegs.Count)];
                if (other.PassNm < leg.PassNm)
                    leg = other;
            }
            var (pick, _) = leg;
            if (_active.ContainsKey(pick.Callsign) || pick.Origin == _airportIcao || pick.Destination == _airportIcao)
                continue;
            if (_airports.Find(pick.Origin) is not { } o || _airports.Find(pick.Destination) is not { } dst)
                continue;
            if (GeoMath.DistanceM(o.Position, dst.Position) < 80 * Nm)
                continue;
            // Primero se leen sus procedimientos (unos segundos); el vuelo se crea en una vuelta siguiente.
            var originReady = AirportReady(pick.Origin);
            if (!AirportReady(pick.Destination) || !originReady)
                continue;
            if (await CreatePassingAsync(ctx, pick, o, dst, ct).ConfigureAwait(false))
                Interlocked.Increment(ref _window);
        }
    }

    /// <summary>
    /// Un vuelo de la zona que no es del aeropuerto cargado: su plan completo de origen a destino y el avión en un punto de
    /// ese plan dentro de la ventana y fuera de la vista del piloto, con la altitud que le toca (ascenso, crucero o descenso).
    /// </summary>
    private async Task<bool> CreatePassingAsync(DirectorContext ctx, WorldLeg leg, WorldAirport origin, WorldAirport dest, CancellationToken ct)
    {
        var match = PickModel(leg.Airline, null, leg.Callsign);
        if (match is null)
            return false;
        var category = AircraftPerformance.For(match.Model).Category;
        var ends = EndsFor(leg, origin, dest, category, ctx);
        var route = await RouteForAsync(ends, ct).ConfigureAwait(false);
        var plan = FlightPlanBuilder.Assemble(ends, route, category);
        var length = plan.Path.LengthM;
        for (var k = 0; k < 12; k++)
        {
            var along = length * (0.04 + 0.9 * _rng.NextDouble());
            // Cerca del final (aproximación a otro aeropuerto) no: terminaría enseguida.
            if (length - along < 25 * Nm)
                continue;
            var (pos, heading, next, alt, climbing) = plan.At(along, origin.ElevationFt);
            var fromPilot = GeoMath.DistanceM(pos, ctx.Pilot);
            if (fromPilot > WindowNm * Nm || fromPilot < TrafficEngine.InjectDistanceM || TooClose(pos, alt))
                continue;
            _airNow.Add((pos, alt));
            var f = new AirborneAgent(leg.Callsign, leg.Airline, match.Model.IcaoType, leg.Origin, leg.Destination, match.Model, category,
                plan.Path, pos, alt, heading, SpeedFor(category, alt, plan.CruiseFt),
                climbing ? 1500 : alt < plan.CruiseFt - 300 ? -1500 : 0, plan.CruiseFt, ends.Landing)
            {
                StartTarget = next,
                EndElevationFt = dest.ElevationFt,
                OriginElevationFt = origin.ElevationFt,
            };
            _active[leg.Callsign] = new FlightInfo(leg, match, Window: true, route, plan);
            _engine.AddFlight(f);
            _log($"{leg.Callsign}: {leg.Origin} → {leg.Destination} ({match.Model.IcaoType}; {plan.RouteText} → {plan.Approach ?? "-"} " +
                 Tr.F("[{0}]), a {1:0} NM de su origen.", Tr.T(plan.Source), along / Nm));
            return true;
        }
        return false;
    }

    /// <summary>Lo más cerca que pasa del piloto la ortodrómica de <paramref name="a"/> a <paramref name="b"/> (NM), muestreada.</summary>
    private static double ClosestApproachNm(GeoPoint a, GeoPoint b, GeoPoint pilot)
    {
        var best = double.MaxValue;
        for (var i = 0; i <= 24; i++)
            best = Math.Min(best, GeoMath.DistanceM(GeoMath.Intermediate(a, b, i / 24.0), pilot));
        return best / Nm;
    }

    // --- Modelos ---

    /// <summary>Tipos comunes por si la aerolínea no tiene liveries instaladas (el emparejador cae a uno genérico).</summary>
    private static readonly string[] CommonTypes = ["A320", "B738", "A20N", "B38M", "A321", "E190"];

    private ModelMatch? PickModel(string airline, string? type, string key)
    {
        if (type is { Length: >= 3 })
            return _matcher.Match(airline, type, key);
        var types = _matcher.TypesFor(airline);
        var t = types.Count > 0 ? types[_rng.Next(types.Count)] : CommonTypes[_rng.Next(CommonTypes.Length)];
        return _matcher.Match(airline, t, key);
    }
}
