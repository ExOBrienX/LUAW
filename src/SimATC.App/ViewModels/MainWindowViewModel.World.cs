using Avalonia.Threading;
using SimATC.Core.Simulation;
using SimATC.Core.Traffic;
using SimATC.Core.World;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Tráfico automático (etapas 6, 6b y 7): el director crea salidas, llegadas y vuelos de la zona alrededor del piloto con
/// la base mundial de rutas (standing-data, CC0); las rutas, de VATSIM o por las aerovías del simulador (ADR-062).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Cada cuánto decide el director (s).</summary>
    private const double DirectorPeriodS = 10;

    private TrafficDirector? _director;
    private WorldAirports? _world;
    private CancellationTokenSource? _directorCts;

    /// <summary>
    /// Arranca el director cuando ya hay motor, catálogo y aeropuerto. Sin lista de rutas manual, arma la del aeropuerto
    /// con la base mundial, para poder estacionar aviones en cualquier aeropuerto (RF-12d). Si está activo el tráfico
    /// automático y no hay estacionados, estaciona aviones.
    /// </summary>
    private async Task StartWorldTrafficAsync()
    {
        if (_engine is null || _matcher is null || Airport is not { } airport)
            return;
        // El tráfico arranca solo con "Iniciar tráfico" (ADR-084): al abrir LUAW no se inyecta nada.
        if (!_trafficStarted)
        {
            TrafficStatus = Tr.T("Tráfico detenido: presiona «Iniciar tráfico» cuando estés listo.");
            return;
        }
        var airportsPath = WorldAirports.PathFor(_dataDir);
        var routesPath = WorldRoutes.PathFor(_dataDir);
        if (!File.Exists(airportsPath) || !File.Exists(routesPath))
        {
            Log(Tr.F("Falta la base mundial ({0}): sin tráfico automático.", airportsPath));
            return;
        }
        _world ??= WorldAirports.Load(airportsPath);
        if (_director is null)
        {
            _director = new TrafficDirector(_world, routesPath, _matcher, _engine, Log);
            // Rutas de VATSIM (ADR-061): lo guardado de otras sesiones, y se refresca en segundo plano desde ya: el primer
            // llenado usa los tipos de avión que se ven ahí (ADR-168).
            _director.Vatsim = VatsimRouteStore.Load(VatsimRouteStore.PathFor(SimATC.Core.AppPaths.Root));
            _ = Task.Run(() => RefreshVatsimAsync(CancellationToken.None));
            Stage(Tr.T("Fijos, radioayudas y aerovías del simulador…"), 35);
            await EnsureNavigationAsync();
            _director.Airways = AirwayDb;
            _director.Nav = Nav;
            _director.LoadAirport = async (icao, ct) =>
            {
                try
                {
                    return (await _airports.GetAsync(icao, false, ct)).Airport;
                }
                catch (Exception)
                {
                    return null;
                }
            };
            _director.LaunchDeparture = (p, callsign) => Dispatcher.UIThread.InvokeAsync(() => TryLaunchOneAsync(p.ObjectId, callsign));
            var engine = _engine;
            engine.DepartureHandoff = d =>
            {
                if (Airport is not { } a || _director?.OnDepartureAirborne(d, a) is not { } flight)
                    return null;
                Dispatcher.UIThread.Post(() => _flights.Remove(d.ObjectId));
                return flight;
            };
            engine.ArrivalPreview = (f, pl) => _graph is { } pg ? SimATC.Core.Engine.DepartureAgent.ArrivalRouteFor(pg, pl, f.StandIndex) : null;
            engine.ArrivalHandoff = f => Airport is { } a && _graph is { } g ? _director?.OnArrivalLanded(f, a, g) : null;
            engine.ArrivalParked += g =>
            {
                if (Airport is not { } a || _director?.OnArrivalParked(g, a, DateTime.UtcNow) is not { } item)
                    return;
                // Se cambia por un avión estacionado del simulador, en el puesto y alineado (prueba de Marco: el que
                // llegaba quedaba "suelto" y el simulador lo movía). Primero aparece el nuevo, después se borra el que rodó.
                _ = Task.Run(async () =>
                {
                    var stand = item.Aircraft.Parking;
                    var model = item.Aircraft.Match.Model;
                    try
                    {
                        var id = await _bridge.HandOverAsync(item.ObjectId, new ParkedSpawnRequest(model.Title, item.Aircraft.TailNumber,
                            item.Aircraft.Position, a.ElevationFt, stand.HeadingTrueDeg, model.EngineCount), CancellationToken.None);
                        _director?.Rekey(item.ObjectId, id);
                        _parked.Adopt(item with { ObjectId = id, ParkedAt = null, ParkedHeadingDeg = null });
                    }
                    catch (Exception ex)
                    {
                        Log(Tr.F("{0}: no se pudo dejar estacionado ({1}); queda como llegó.", item.Aircraft.TailNumber, ex.Message));
                        _parked.Adopt(item);
                    }
                    Dispatcher.UIThread.Post(RefreshTraffic);
                });
            };
        }
        // Las rutas de la zona (para la lista del aeropuerto si no hay una manual).
        Stage(Tr.T("Vuelos y aerolíneas reales de la zona…"), 45);
        await _director.EnsureZoneAsync(UserState?.Position ?? airport.Reference);
        if (_routes is null && _director.RouteListFor(airport.Icao) is { } list)
        {
            _routes = list;
            Log(Tr.F("{0}: lista de rutas armada con la base mundial ({1} aerolíneas).", airport.Icao, list.Airlines.Count));
        }
        if (_routes is not null)
            ModalLine(Tr.F("{0}: {1} aerolíneas operan aquí.", airport.Icao, _routes.Airlines.Count));
        if (_settings.AutoTraffic && _routes is not null && _parked.Snapshot().Count == 0)
        {
            // Según el nivel de tráfico y los puestos de cada tipo (ADR-109; antes, como mucho 20): quedan libres las llegadas
            // de la próxima hora, el piloto y un margen (ADR-089).
            Stage(Tr.F("Estacionando aviones en {0}…", airport.Icao), 60);
            await FillAirportAsync(airport);
            Stage(Tr.T("Arrancando salidas y llegadas…"), 90);
        }
        // El director arranca después de estacionar (si no, reservaba puestos para llegadas mientras se estacionaban aviones).
        if (_directorCts is null)
        {
            _directorCts = new CancellationTokenSource();
            _ = DirectorLoopAsync(_directorCts.Token);
        }
        Stage(Tr.T("Programando horarios, llegadas y planes de vuelo…"), 95);
    }

    /// <summary>Todos los fijos del simulador (rutas de los IA y radar global), o null mientras se cargan.</summary>
    public NavIndex? Nav
    {
        get => _nav;
        private set
        {
            _nav = value;
            if (AirwayDb is { } db && value is not null)
            {
                db.Nav = value;
                var fixedCount = db.RepairPositions();
                if (fixedCount > 0)
                    Log(Tr.F("Aerovías: {0} fijos sin posición corregidos con el índice.", fixedCount));
            }
            OnPropertyChanged();
        }
    }

    private NavIndex? _nav;
    private bool _navLoading;

    /// <summary>Días que vale el índice guardado antes de volver a pedirlo al simulador (cambia con el AIRAC).</summary>
    private const int NavIndexMaxAgeDays = 7;

    private async Task LoadNavIndexAsync()
    {
        if (_navLoading)
            return;
        _navLoading = true;
        try
        {
            var path = NavIndex.PathFor(SimATC.Core.AppPaths.Root);
            var saved = await Task.Run(() => NavIndex.Load(path));
            if (saved is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Nav = saved);
                if (_director is { } d)
                    d.Nav = saved;
                Log(Tr.F("Índice de fijos: {0} (guardado).", saved.Count));
            }
            var fresh = File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays < NavIndexMaxAgeDays;
            if (saved is not null && fresh || !_bridge.IsConnected)
                return;
            var list = await _bridge.RequestAllNavaidsAsync(CancellationToken.None);
            if (list.Count < 1000)
            {
                Log(Tr.F("Índice de fijos: el simulador entregó solo {0}; se usa el guardado.", list.Count));
                return;
            }
            var index = await Task.Run(() =>
            {
                var i = new NavIndex(list);
                i.Save(path);
                return i;
            });
            await Dispatcher.UIThread.InvokeAsync(() => Nav = index);
            if (_director is { } d2)
                d2.Nav = index;
            Log(Tr.F("Índice de fijos: {0} waypoints, VOR y NDB del simulador.", index.Count));
        }
        catch (Exception ex)
        {
            Log(Tr.F("Índice de fijos: {0}", ex.Message));
        }
        finally
        {
            _navLoading = false;
        }
    }

    private Task? _navigationLoading;

    /// <summary>
    /// Aeropuertos del mundo, aerovías guardadas y todos los fijos (de disco; del simulador la primera vez y cada semana), lo
    /// antes posible: el radar los muestra sin esperar al tráfico (ADR-062, ADR-063).
    /// </summary>
    private Task EnsureNavigationAsync()
    {
        if (_navigationLoading is { IsCompleted: false } running)
            return running;
        return _navigationLoading = Task.Run(async () =>
        {
            if (WorldAirportsData is null && File.Exists(WorldAirports.PathFor(_dataDir)))
            {
                _world ??= WorldAirports.Load(WorldAirports.PathFor(_dataDir));
                var world = _world;
                Dispatcher.UIThread.Post(() => WorldAirportsData = world);
            }
            if (AirwayDb is null)
            {
                var db = new AirwayDatabase(_bridge.RequestWaypointAsync) { TypedFetch = _bridge.RequestWaypointAsync };
                var loaded = db.Load(AirwayDatabase.PathFor(SimATC.Core.AppPaths.Root));
                db.Nav = Nav;
                db.RepairPositions();
                Log(Tr.F("Aerovías guardadas: {0} fijos.", loaded));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AirwayDb = db;
                    _ = CrawlAirwaysAsync();
                });
            }
            await LoadNavIndexAsync();
        });
    }

    /// <summary>Fijos con sus aerovías (rutas de los IA y capa de aerovías del radar).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial AirwayDatabase? AirwayDb { get; set; }

    /// <summary>Aeropuertos del mundo (capa del radar).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial WorldAirports? WorldAirportsData { get; set; }

    // Lo que muestra el radar (sur, norte, oeste, este), para pedir las aerovías de esa zona.
    private (double S, double N, double W, double E)? _radarBox;

    /// <summary>El radar se movió o cambió de zoom.</summary>
    public void OnRadarView(double s, double n, double w, double e) => _radarBox = (s, n, w, e);

    /// <summary>Fijos pedidos por segundo para la capa de aerovías (el simulador responde ~42 por segundo; se deja aire).</summary>
    private const int AirwayCrawlPerSecond = 25;

    /// <summary>Fijos por segundo de la carga de fondo mientras alguna ruta espera los suyos.</summary>
    private const int AirwayCrawlWhileRouting = 3;

    /// <summary>
    /// Descarga de las aerovías (ADR-064), unos fijos por segundo en segundo plano: primero lo que se ve en el radar (con la
    /// capa de aerovías encendida y una zona de menos de ~600 NM), después el mundo entero empezando por tu zona. Solo los
    /// fijos que pueden tener aerovías (VOR y waypoints de 5 letras; los terminales como "BL530" no). Con todo descargado, la
    /// búsqueda de rutas por aerovías es instantánea en cualquier parte. Cada 5 min guarda la caché en disco.
    /// </summary>
    private async Task CrawlAirwaysAsync()
    {
        var buffer = new List<NavFix>();
        var lastSave = DateTime.UtcNow;
        var savedCount = AirwayDb?.Count ?? 0;
        NavFix[]? world = null;
        NavIndex? worldFor = null;
        var next = 0;
        while (true)
        {
            await Task.Delay(1000);
            if (AirwayDb is not { } db)
                continue;
            try
            {
                if ((DateTime.UtcNow - lastSave).TotalMinutes >= 5 && db.Count != savedCount)
                {
                    lastSave = DateTime.UtcNow;
                    savedCount = db.Count;
                    await Task.Run(() => db.Save(AirwayDatabase.PathFor(SimATC.Core.AppPaths.Root)));
                    Log(Tr.F("Aerovías guardadas: {0} fijos.", savedCount));
                }
                if (Nav is not { } nav || !_bridge.IsConnected)
                    continue;
                var asked = 0;
                // Las rutas del tráfico van primero (ADR-165): el simulador da ~42 fijos por segundo para todos.
                var limit = db.Waiting > 0 ? AirwayCrawlWhileRouting : AirwayCrawlPerSecond;
                // 1) Lo que se mira en el radar.
                if (ShowAirways && _radarBox is { } box && box.N - box.S <= 10)
                {
                    nav.InBox(box.S, box.N, box.W, box.E, buffer, 6000);
                    var center = new SimATC.Core.Geo.GeoPoint((box.S + box.N) / 2, (box.W + box.E) / 2);
                    buffer.Sort((a, b) => SimATC.Core.Geo.GeoMath.DistanceM(a.Position, center).CompareTo(SimATC.Core.Geo.GeoMath.DistanceM(b.Position, center)));
                    foreach (var f in buffer)
                    {
                        if (!MayHaveAirways(f) || db.Contains(f.Ident, f.Region))
                            continue;
                        _ = db.GetAsync(f.Ident, f.Region, CancellationToken.None);
                        if (++asked >= limit)
                            break;
                    }
                }
                // 2) El mundo, de lo más cercano a lo más lejano.
                if (worldFor != nav)
                {
                    var here = UserState?.Position ?? Airport?.Reference ?? default;
                    world = await Task.Run(() =>
                    {
                        var all = new List<NavFix>();
                        nav.InBox(-90, 90, -180, 180, all, int.MaxValue);
                        return all.Where(MayHaveAirways).OrderBy(f => SimATC.Core.Geo.GeoMath.DistanceM(f.Position, here)).ToArray();
                    });
                    worldFor = nav;
                    next = 0;
                }
                while (asked < limit && world is not null && next < world.Length)
                {
                    var f = world[next++];
                    if (db.Contains(f.Ident, f.Region))
                        continue;
                    _ = db.GetAsync(f.Ident, f.Region, CancellationToken.None);
                    asked++;
                }
            }
            catch (Exception ex)
            {
                Log(Tr.F("Aerovías: {0}", ex.Message));
            }
        }
    }

    /// <summary>¿Puede tener aerovías? VOR y waypoints de 5 letras (los terminales llevan números: "BL530", "12VLN").</summary>
    private static bool MayHaveAirways(NavFix f) =>
        f.Kind == NavKind.Vor || f.Kind == NavKind.Waypoint && f.Ident.Length == 5 && f.Ident.All(char.IsAsciiLetterUpper);

    /// <summary>Guarda la caché de aerovías (al cerrar).</summary>
    public void SaveAirwayCache()
    {
        try
        {
            AirwayDb?.Save(AirwayDatabase.PathFor(SimATC.Core.AppPaths.Root));
        }
        catch (Exception)
        {
            // Al cerrar no se molesta: se pierde lo de esta sesión.
        }
    }

    private DateTime _vatsimRefreshed = DateTime.MinValue;

    /// <summary>Cada 30 min se leen los planes de vuelo de VATSIM y se guardan (crece la base de rutas).</summary>
    private async Task RefreshVatsimAsync(CancellationToken ct)
    {
        if (_director?.Vatsim is not { } store || (DateTime.UtcNow - _vatsimRefreshed).TotalMinutes < 30)
            return;
        _vatsimRefreshed = DateTime.UtcNow;
        if (await store.RefreshAsync(Log, ct) > 0)
            store.Save(VatsimRouteStore.PathFor(SimATC.Core.AppPaths.Root));
    }

    private async Task DirectorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Run(() => RefreshVatsimAsync(ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"VATSIM: {ex.Message}");
            }
            try
            {
                if (_director is { } d && _engine is { } engine && _settings.AutoTraffic && _bridge.IsConnected && !_busy && Airport is not null)
                {
                    engine.Limits = EffectiveLimits;
                    var ctx = DirectorContextNow();
                    await Task.Run(() => d.UpdateAsync(ctx, ct), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(Tr.F("Tráfico automático: {0}", ex.Message));
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(DirectorPeriodS), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Lo que el director necesita, tomado en el hilo de la interfaz.</summary>
    private DirectorContext DirectorContextNow()
    {
        var parked = _parked.Snapshot();
        var busy = parked.Select(p => p.Aircraft.ParkingIndex).ToHashSet();
        foreach (var f in _flights.Values)
            busy.Add(f.Aircraft.ParkingIndex);
        var runway = SelectedRunwayEnd != AutoRunway ? SelectedRunwayEnd : DepartureEnds.FirstOrDefault();
        IReadOnlyList<string> landing = SelectedRunwayEnd != AutoRunway && SelectedRunwayEnd is { } manual ? [manual] : ArrivalEnds;
        // Llegadas paralelas según la demanda (ADR-082, AIP SCEL 2.1): llegadas al aeropuerto a menos de 60 NM.
        if (Airport is { } ap && landing.Count == 1)
        {
            var distances = new List<double>();
            foreach (var a in _engine?.Snapshot() ?? [])
            {
                var nm = Core.Geo.GeoMath.DistanceM(a.Position, ap.Reference) / Core.Units.UnitConversions.MetersPerNauticalMile;
                if (a.Airborne && a.Destination == ap.Icao && nm < Core.Flight.ParallelRunways.DemandNm)
                    distances.Add(nm);
            }
            var near = distances.Count;
            var was = _parallelArrivals;
            // También por el tiempo estimado de aterrizaje de cada una (ADR-131): dos que llegarían al umbral con menos de
            // ~2 minutos entre sí abren la paralela, aunque sean pocas.
            var etas = new List<double>();
            foreach (var f in _engine?.AirborneNow() ?? [])
            {
                if (f.ControlledArrival && f.Destination == ap.Icao && f.Landing is not null && f.RemainingNm < 90)
                    etas.Add(Core.Flight.ArrivalSequencer.EtaS(f));
            }
            landing = Core.Flight.ParallelRunways.LandingSet(ap, landing, Math.Max(near, etas.Count >= 2 ? 2 : near), ref _parallelArrivals,
                Core.Flight.ParallelRunways.Compressed(distances) || Core.Flight.ParallelRunways.CompressedEta(etas));
            // Con el reparto automático segregado, la demanda no suma como llegada una pista de salidas (ADR-091).
            if (_runwayPlan is { Segregated: true } && _atcData is null && landing.Any(e => DepartureEnds.Contains(e)))
                landing = ArrivalEnds;
            if (was != _parallelArrivals)
                Log(_parallelArrivals
                    ? Tr.F("{0}: {1} llegadas cerca: aproximaciones paralelas simultáneas a {2}.", ap.Icao, near, string.Join(" y ", landing))
                    : Tr.F("{0}: baja la demanda: llegadas solo por {1}.", ap.Icao, string.Join("/", landing)));
        }
        _landingInUse = landing; // las de llegada abiertas ahora (ATIS y avisos, ADR-102)
        return new DirectorContext(Airport, _graph, runway, UserState?.Position ?? Airport!.Reference, DateTime.UtcNow, parked, busy,
            EffectiveDensity, LandingRunways: landing);
    }

    // Llegadas paralelas en curso (ADR-082), con histéresis.
    private bool _parallelArrivals;

    // Pistas de llegada abiertas en la última vuelta del director (con las paralelas por demanda), o null sin director.
    private IReadOnlyList<string>? _landingInUse;

    /// <summary>Las pistas de llegada abiertas ahora: las del director si está corriendo; si no, las de la pista en uso.</summary>
    private IReadOnlyList<string> ArrivalsInUse() =>
        _landingInUse is { Count: > 0 } l && SelectedRunwayEnd == AutoRunway ? l : ArrivalEnds;

    /// <summary>Altitud para el radar: nivel de vuelo sobre la altitud de transición ("FL270"), si no en pies.</summary>
    private string FlightLevelText(double altitudeFt) =>
        altitudeFt > TransitionAltitudeFt ? $"FL{altitudeFt / 100:000}" : $"{altitudeFt:N0} ft";
}
