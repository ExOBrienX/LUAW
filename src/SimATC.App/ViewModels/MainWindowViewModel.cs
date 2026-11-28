using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;
using SimATC.Core.Simulation;
using SimATC.Core.Traffic;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Ventana principal: conexión con el simulador, aeropuerto cargado, estado del piloto para el radar y
/// aviones estacionados (etapa 2). Los eventos del puente llegan desde su hilo y se pasan al hilo de la UI.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ISimBridge _bridge;
    private readonly AirportProvider _airports;
    /// <summary>Aeropuerto cargado o por cargar (el del piloto: ADR-030); null hasta saberlo.</summary>
    private string? _icao;
    private readonly string _dataDir;
    private readonly ParkedTraffic _parked = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ModelMatcher? _matcher;
    private RouteList? _routes;
    private TaxiGraph? _graph;
    private TrafficEngine? _engine;
    private List<(string Label, int Runway, bool Primary)> _runwayEnds = [];
    private bool _loading;
    private bool _busy;
    private bool _airportFromCache;

    /// <summary>Datos de cada salida en curso, por ObjectId (para el inspector del radar). Solo en el hilo de la UI.</summary>
    private readonly Dictionary<uint, (ParkedAircraft Aircraft, string Runway, DepartureAgent Agent)> _flights = [];

    /// <summary>Segundos entre salidas al lanzar varias seguidas.</summary>
    public const int LaunchSpacingS = 40;

    public MainWindowViewModel(ISimBridge bridge, AirportProvider airports, string dataDir)
    {
        _bridge = bridge;
        _airports = airports;
        _dataDir = dataDir;
        bridge.ConnectionChanged += c => Dispatcher.UIThread.Post(() => OnConnectionChanged(c));
        bridge.UserStateUpdated += s => Dispatcher.UIThread.Post(() =>
        {
            UserState = s;
            PilotWingspanM = UserWingspanM;
            PilotShape = AircraftShapes.Of(_userSpan.Type, _userSpan.SpanM, _userSpan.Engines);
            if (_engine is { } e)
                e.UserWingspanM = PilotWingspanM;
        });
        // El radar se refresca a 5 Hz mientras haya aviones moviéndose.
        _refresh.Tick += (_, _) =>
        {
            if (_engine is { ActiveCount: > 0 } || Traffic.Count > _parked.Snapshot().Count || SelectedId is not null)
                RefreshTraffic();
        };
        _refresh.Start();
        InitShell();
        _ = MetarLoopAsync(_metarCts.Token); // METAR de internet (ADR-100)
    }

    /// <summary>Cabeceras disponibles para despegar (la pista activa se elige a mano en la etapa 3).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> RunwayEnds { get; set; } = [];

    [ObservableProperty]
    public partial string? SelectedRunwayEnd { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Tr.T("Esperando a MSFS…");

    [ObservableProperty]
    public partial string TrafficStatus { get; set; } = "";

    [ObservableProperty]
    public partial AirportData? Airport { get; set; }

    [ObservableProperty]
    public partial UserAircraftState? UserState { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TrafficMarker> Traffic { get; set; } = [];

    // --- Inspector del radar: clic en un avión ---

    [ObservableProperty]
    public partial uint? SelectedId { get; set; }

    [ObservableProperty]
    public partial bool FollowSelected { get; set; }

    [ObservableProperty]
    public partial string InspectorTitle { get; set; } = "";

    /// <summary>Segunda línea de la ficha: aerolínea y modelo (o el título de tu avión).</summary>
    [ObservableProperty]
    public partial string InspectorSubtitle { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<InspectorRow> InspectorRows { get; set; } = [];

    // Lo importante de la ficha, en grande; y a quién espera (clic lo selecciona).
    [ObservableProperty]
    public partial string InspectorPhase { get; set; } = "";

    [ObservableProperty]
    public partial string InspectorAltitude { get; set; } = "";

    [ObservableProperty]
    public partial string InspectorSpeed { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectorWaitPlain))]
    public partial string? InspectorWait { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectorWaitPlain))]
    public partial uint? InspectorWaitId { get; set; }

    /// <summary>Espera algo que no es un avión (o uno que ya no está): sin botón.</summary>
    public bool InspectorWaitPlain => InspectorWait is not null && InspectorWaitId is null;

    /// <summary>"Más datos" de la ficha desplegado.</summary>
    [ObservableProperty]
    public partial bool InspectorMore { get; set; }

    [RelayCommand]
    private void SelectAircraft(uint? id)
    {
        if (id is { } i)
            SelectedId = i;
    }

    /// <summary>
    /// La ficha del seleccionado está abierta. Se puede seguir a un avión (o al tuyo) sin ella: "Seguirme" no la abre y
    /// cerrarla mientras se sigue solo la oculta (prueba de Marco, 2026-10-31).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector), nameof(ShowFollowChip))]
    public partial bool InspectorOpen { get; set; }

    public bool ShowInspector => HasSelection && InspectorOpen;

    /// <summary>Siguiendo con la ficha cerrada: un aviso pequeño con a quién y para dejar de seguir.</summary>
    public bool ShowFollowChip => HasSelection && FollowSelected && !InspectorOpen;

    partial void OnFollowSelectedChanged(bool value) => OnPropertyChanged(nameof(ShowFollowChip));

    [ObservableProperty]
    public partial IReadOnlyList<GeoPoint>? SelectedRoute { get; set; }

    public bool HasSelection => SelectedId is not null;

    partial void OnSelectedIdChanged(uint? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is null)
            FollowSelected = false;
        InspectorOpen = value is not null; // clic en un avión: se abre su ficha
        OnPropertyChanged(nameof(ShowInspector));
        OnPropertyChanged(nameof(ShowFollowChip));
        RefreshTraffic();
    }

    /// <summary>✕ de la ficha: si se lo está siguiendo, solo se oculta; si no, se deselecciona.</summary>
    [RelayCommand]
    private void CloseInspector()
    {
        if (FollowSelected)
            InspectorOpen = false;
        else
            SelectedId = null;
    }

    [RelayCommand]
    private void OpenInspector() => InspectorOpen = true;

    [RelayCommand]
    private void StopFollowing()
    {
        FollowSelected = false;
        if (!InspectorOpen)
            SelectedId = null;
    }

    private bool CanUseTraffic() => !_busy && _matcher is not null && _routes is not null && Airport is not null && _bridge.IsConnected;

    /// <summary>Reemplaza el tráfico estacionado por <paramref name="count"/> aviones (densidad progresiva: 1, 3, 10…).</summary>
    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private async Task PopulateAsync(string count)
    {
        if (!int.TryParse(count, out var n) || Airport is not { } airport || _matcher is null || _routes is null)
            return;
        SetBusy(true);
        try
        {
            TrafficStatus = Tr.F("Creando {0} aviones…", n);
            // Los puestos de las salidas en curso siguen ocupados (avión o remolcador): no se usan.
            var busy = _flights.Values.Select(f => f.Aircraft.ParkingIndex).ToHashSet();
            if (_director is { } d)
                busy.UnionWith(d.ReservedStands); // los de las llegadas en camino
            // Puestos sin camino a las pistas (grafo cortado en el escenario, ADR-093): no se estaciona ahí.
            if (_graph is { } g)
                busy.UnionWith(Enumerable.Range(0, airport.Parkings.Count).Where(i => !g.StandReachable(i)));
            var created = await _parked.PopulateAsync(_bridge, airport, _routes, _matcher, EffectiveLimits with { MaxGround = 200, MaxTotal = 400 }, n,
                UserState?.Position, Log, CancellationToken.None, busy, _flights.Count);
            RefreshTraffic();
            TrafficStatus = Tr.F("{0} aviones estacionados en {1}.", created, airport.Icao);
        }
        catch (Exception ex)
        {
            TrafficStatus = Tr.F("Error al poblar: {0}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>"Limpiar tráfico" (RF-44): borra todos los aviones creados sin cerrar la app ni MSFS.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private void ClearTraffic() => AskClearTraffic();

    private void ClearTrafficCore()
    {
        _engine?.Clear();
        _parked.Clear(_bridge);
        _flights.Clear();
        _director?.Reset();
        SelectedId = null;
        RefreshTraffic();
        TrafficStatus = Tr.T("Tráfico eliminado.");
    }

    /// <summary>Lanza varias salidas seguidas, separadas <see cref="LaunchSpacingS"/> s (prueba de la etapa 3).</summary>
    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private async Task LaunchSeveralAsync(string count)
    {
        if (!int.TryParse(count, out var n))
            return;
        for (var i = 0; i < n; i++)
        {
            if (i > 0)
                await Task.Delay(TimeSpan.FromSeconds(LaunchSpacingS));
            if (!await TryLaunchOneAsync())
                return;
        }
    }

    /// <summary>Toma un avión estacionado y lo hace salir por la cabecera elegida (etapa 3).</summary>
    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private async Task LaunchDepartureAsync() => await TryLaunchOneAsync();

    /// <param name="only">Si viene, solo ese avión estacionado (botón "Lanzar" de la tabla de tráfico).</param>
    /// <param name="callsign">Indicativo del vuelo (tráfico automático); null = la matrícula del estacionado.</param>
    private async Task<bool> TryLaunchOneAsync(uint? only = null, string? callsign = null)
    {
        if (_engine is null || _graph is null || Airport is not { } airport)
            return false;
        var wanted = DepartureEndForLaunch();
        var end = _runwayEnds.Find(r => r.Label == wanted);
        if (end.Label is null)
        {
            TrafficStatus = SelectedRunwayEnd == AutoRunway
                ? Tr.T("Todavía no hay pista activa (falta el clima de tu posición en el aeropuerto). Elige una cabecera a mano.")
                : Tr.T("Elige una cabecera.");
            return false;
        }
        var parked = _parked.Snapshot();
        // Puestos con avión: el pushback no puede terminar en sus ramales de entrada.
        var occupied = parked.Select(p => p.Aircraft.ParkingIndex).ToHashSet();
        foreach (var p in parked)
        {
            if (only is { } o && p.ObjectId != o || p.Aircraft.Decor)
                continue; // la decoración nunca sale (ADR-109)
            var model = p.Aircraft.Match.Model;
            var perf = AircraftPerformance.For(model);
            var flightId = callsign ?? p.Aircraft.TailNumber;
            if (DepartureAgent.Plan(p.ObjectId, flightId, airport, _graph, p.Aircraft.ParkingIndex,
                    end.Runway, end.Primary, perf, DepartureTimings.Default, model.EngineCount, occupied) is null)
                continue;
            _parked.Release(p.ObjectId);
            SetBusy(true);
            try
            {
                var objectId = p.ObjectId;
                if (p.IsParkedAtc)
                {
                    // Relevo (V-15): un avión sin ATC igual toma su lugar y la pasarela se retira sola.
                    TrafficStatus = Tr.F("{0}: cerrando puertas, se retira la pasarela…", p.Aircraft.TailNumber);
                    objectId = await _bridge.HandOverAsync(p.ObjectId, new ParkedSpawnRequest(model.Title, p.Aircraft.TailNumber,
                        p.Position, airport.ElevationFt, p.HeadingDeg, model.EngineCount), CancellationToken.None);
                }
                var agent = DepartureAgent.Plan(objectId, flightId, airport, _graph, p.Aircraft.ParkingIndex,
                    end.Runway, end.Primary, perf, DepartureTimings.Default, model.EngineCount, occupied, model.NoseGearM,
                    model.WingspanM, p.Position, p.HeadingDeg, model.Wake)!;
                agent.TypeDesignator = p.Aircraft.Type;
                _engine.Add(agent);
                _flights[objectId] = (p.Aircraft, end.Label, agent);
                if (SelectedId == p.ObjectId)
                    SelectedId = objectId; // el relevo cambia el ObjectId: el inspector sigue con el mismo avión
                TrafficStatus = Tr.F("{0} ({1} {2}) sale de {3} hacia {4}.", p.Aircraft.TailNumber, p.Aircraft.Airline, p.Aircraft.Type, p.Aircraft.Parking.DisplayName, end.Label);
            }
            catch (Exception ex)
            {
                TrafficStatus = Tr.F("{0}: no se pudo iniciar la salida ({1}).", p.Aircraft.TailNumber, ex.Message);
            }
            finally
            {
                SetBusy(false);
                RefreshTraffic();
            }
            return true;
        }
        TrafficStatus = only is null
            ? Tr.T("No hay aviones estacionados con ruta a esa cabecera. Estaciona algunos primero.")
            : Tr.T("Ese avión no tiene ruta de salida a esa cabecera.");
        return false;
    }

    private void OnConnectionChanged(bool connected)
    {
        // La barra superior se actualiza al instante (antes dependía de refrescar la tabla de tráfico).
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(IsConnected));
        if (!connected)
        {
            // En vuelo y sin haberlo pedido: aviso modal (el vuelo se puede retomar al reconectar).
            if (UserState is { OnGround: false } && Plan is not null && !_reconnecting)
                NoticeConnectionLost();
            UserState = null;
            _engine?.Clear();
            _parked.Forget();
            _matcher = null;
            RefreshTraffic();
            Status = Tr.T("Esperando a MSFS…");
            TrafficStatus = "";
            NotifyCommands();
            return;
        }
        Status = Tr.T("Conectado");
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            // El aeropuerto donde está el piloto; si no está en ninguno (en vuelo), el cargado o el último usado.
            var here = await LocateAirportAsync();
            // Nunca el último usado si está lejos del piloto (la app es para todo el mundo, no solo SCEL).
            var icao = here ?? Airport?.Icao ?? (UserState is null ? _settings.LastAirport : null);
            if (icao is null)
            {
                Status = Tr.T("Conectado · no estás en un aeropuerto: LUAW carga uno cuando estés en tierra en él.");
                return;
            }
            if (Airport is null || Airport.Icao != icao)
            {
                if (Airport is not null)
                {
                    Log(Tr.F("Cambio de aeropuerto: {0} → {1}. Se borra el tráfico del anterior.", Airport.Icao, icao));
                    ClearTrafficCore();
                    _routes = null;
                }
                _icao = icao;
                OnPropertyChanged(nameof(AirportText));
                Status = Tr.F("Conectado · cargando {0}…", icao);
                Stage(Tr.F("Cargando {0}…", icao), 10);
                _airports.Log ??= Log;
                var (airport, fromCache) = await _airports.GetFreshAsync(icao, CancellationToken.None);
                // Sin pistas (un parque o helipuerto): el aeropuerto con pistas más cercano, si hay otro en rango.
                if (airport.Runways.Count == 0 && _noRunways.Add(icao) && await LocateAirportAsync() is { } other && other != icao)
                {
                    Log(Tr.F("{0} no tiene pistas: se usa {1}.", icao, other));
                    icao = other;
                    _icao = icao;
                    OnPropertyChanged(nameof(AirportText));
                    (airport, fromCache) = await _airports.GetFreshAsync(icao, CancellationToken.None);
                }
                SetAirport(airport, fromCache);
                Change(_settings with { LastAirport = icao });
            }

            // Lista manual si existe (corrección, RF-11); si no, se arma con la base mundial al arrancar el director.
            _routes ??= RouteList.Load(RouteList.PathFor(_dataDir, icao));

            // Capas del radar y rutas de los IA: fijos del mundo y aerovías guardadas, en paralelo al catálogo (ADR-063).
            _ = EnsureNavigationAsync();
            TrafficStatus = Tr.T("Cargando catálogo de modelos…");
            Stage(Tr.T("Catálogo de modelos…"), 25);
            _matcher = await FleetCatalog.LoadAsync(_bridge, AppPaths.FleetCache, Log, CancellationToken.None, _settings.ExtraModelFolders);
            ModalLine(Tr.F("Catálogo listo: {0} modelos de FSLTL y AIG, {1} aviones en el simulador.", _matcher.ModelCount, _matcher.SimTitles.Count));

            if (_engine is null)
            {
                _engine = new TrafficEngine(_bridge, Log);
                _bridge.Frame += _engine.OnFrame; // el motor corre en el hilo de SimConnect, cuadro a cuadro
                _engine.AgentRemoved += id => Dispatcher.UIThread.Post(() => _flights.Remove(id));
            }
            ConfigureAiRadio();
            TrafficStatus = Tr.F("Catálogo listo: {0} modelos.", _matcher.ModelCount);
            await StartWorldTrafficAsync();
            Stage(_trafficStarted ? Tr.T("Tráfico listo") : "", 100);
            if (_routes is null)
                TrafficStatus = Tr.F("{0}: sin rutas de aerolínea en la base mundial (sin tráfico aquí).", icao);
            else if (!_settings.AutoTraffic)
                TrafficStatus = Tr.F("Catálogo listo: {0} modelos. Elige cuántos aviones estacionar.", _matcher.ModelCount);
        }
        catch (Exception ex)
        {
            TrafficStatus = Tr.F("Error al cargar: {0}", ex.Message);
            Stage("", 100);
        }
        finally
        {
            _loading = false;
            NotifyCommands();
        }
    }

    /// <summary>Aeropuerto cargado (de la caché o del simulador): grafo de rodaje y cabeceras.</summary>
    private void SetAirport(AirportData airport, bool fromCache)
    {
        Airport = airport;
        OnPropertyChanged(nameof(TrafficLevelSummary));
        _airportFromCache = fromCache;
        _graph = TaxiGraph.Build(airport);
        _flowFor = null; // grafo nuevo: los sentidos se calculan con la pista en uso
        RadarFlowArrows = null; // las del aeropuerto anterior (ADR-102)
        _landingInUse = null;
        _runwayEnds = [];
        for (var i = 0; i < airport.Runways.Count; i++)
        {
            _runwayEnds.Add((airport.Runways[i].Primary.Ident, i, true));
            _runwayEnds.Add((airport.Runways[i].Secondary.Ident, i, false));
        }
        RunwayEnds = [AutoRunway, .. _runwayEnds.Select(r => r.Label)];
        SelectedRunwayEnd = AutoRunway;
        InitAirportAtc(airport);
        Log(Tr.F("Rodaje {0}: {1}.", airport.Icao, _graph.Health.Summary)); // grafo revisado y reparado (ADR-093)
        Status = Tr.F("Conectado · {0} {1} ({2}) · ", airport.Icao, airport.Name, (fromCache ? Tr.T("caché") : "simulador")) +
                 Tr.F("{0} pistas, {1} puntos, {2} puestos", airport.Runways.Count, airport.TaxiPoints.Count, airport.Parkings.Count);
    }

    private void RefreshTraffic()
    {
        var parked = _parked.Snapshot();
        var markers = parked
            .Select(p => new TrafficMarker(p.Position, p.HeadingDeg, $"{p.Aircraft.Airline} {p.Aircraft.Type}", p.ObjectId,
                WingspanM: p.Aircraft.Match.Model.WingspanM,
                Shape: AircraftShapes.Of(p.Aircraft.Type, p.Aircraft.Match.Model.WingspanM, p.Aircraft.Match.Model.EngineCount)))
            .ToList();
        var moving = _engine?.Snapshot() ?? [];
        foreach (var a in moving)
        {
            var waiting = a.Hold != HoldKind.None && a.SpeedKt < 1;
            var hold = waiting ? $" · {ShortHold(a.Hold)}" : "";
            var label = a.Airborne
                ? Tr.F("{0} {1} {2:0} kt · {3} · {4}", a.Callsign, FlightLevelText(a.AltitudeFt), a.SpeedKt, PhaseText(a.Phase), a.Destination)
                : Tr.F("{0} {1:0} kt{2} · {3}{4}", a.Callsign, a.SpeedKt, (a.AglFt > 20 ? $" {a.AglFt:0} ft" : ""), PhaseText(a.Phase), hold);
            markers.Add(new TrafficMarker(a.Position, a.HeadingDeg, label, a.ObjectId, waiting, a.SpeedKt, a.WingspanM,
                AircraftShapes.Of(a.Type, a.WingspanM, a.Engines)));
        }
        Traffic = markers;
        UpdateInspector(parked, moving);
        UpdateTrafficTable(parked, moving);
        PublishEfb(parked, moving);
    }

    /// <summary>Panel del avión seleccionado, en vivo (5 Hz).</summary>
    private void UpdateInspector(IReadOnlyList<InjectedParked> parked, AgentSnapshot[] moving)
    {
        SelectedFlight = SelectedId is { } chosen && chosen != RadarView.UserId ? RadarFlightView(chosen) : null;
        if (SelectedId is not { } sel)
        {
            InspectorRows = [];
            SelectedRoute = null;
            return;
        }
        if (sel == RadarView.UserId)
        {
            UpdateUserInspector();
            return;
        }
        var snap = Array.Find(moving, m => m.ObjectId == sel);
        if (snap.Callsign is not null && snap.Airborne)
        {
            InspectorTitle = $"{snap.Callsign} · {snap.Type}";
            InspectorSubtitle = $"{(string.IsNullOrEmpty(snap.Origin) ? "?" : snap.Origin)} → {snap.Destination}";
            InspectorPhase = PhaseText(snap.Phase);
            InspectorAltitude = FlightLevelText(snap.AltitudeFt);
            InspectorSpeed = $"{snap.SpeedKt:0} kt";
            InspectorWait = null;
            InspectorWaitId = null;
            InspectorRows = Details(sel, parked).Rows;
            SelectedRoute = null;
            return;
        }
        if (snap.Callsign is not null && _flights.TryGetValue(sel, out var f))
        {
            var a = f.Aircraft;
            InspectorTitle = $"{snap.Callsign} · {a.Type}";
            InspectorSubtitle = $"{a.Airline} · {a.Match.Model.Title}";
            InspectorPhase = PhaseText(snap.Phase);
            InspectorAltitude = snap.AglFt > 20 ? $"{snap.AglFt:N0} ft" : Tr.T("en tierra");
            InspectorSpeed = $"{snap.SpeedKt:0} kt";
            InspectorWait = snap.Hold == HoldKind.None ? null : WaitWithTime(snap);
            InspectorWaitId = snap.HoldFor is { } by && Array.Find(moving, m => m.Callsign == by) is { Callsign: not null } other ? other.ObjectId : null;
            InspectorRows =
            [
                new(Tr.T("Salida"), Tr.F("{0} → pista {1}", a.Parking.DisplayName, f.Runway), Tr.T("Rodaje")),
                new(Tr.T("Calles"), f.Agent.Taxiways.Count > 0 ? string.Join(", ", f.Agent.Taxiways) : Tr.T("calles sin nombre")),
                new(Tr.T("Gases"), Tr.F("{0:0} %", snap.ThrottlePercent)),
                new(Tr.T("Modelo"), a.Match.Model.Title, Tr.T("Avión")),
            ];
            SelectedRoute = RouteAhead(f.Agent);
            return;
        }
        if (snap.Callsign is not null && _engine?.Find(sel) is DepartureAgent { Inbound: true } inbound)
        {
            InspectorTitle = $"{snap.Callsign} · {inbound.TypeDesignator}";
            InspectorSubtitle = Tr.T("llegada, rodando a su puesto");
            InspectorPhase = PhaseText(snap.Phase);
            InspectorAltitude = Tr.T("en tierra");
            InspectorSpeed = $"{snap.SpeedKt:0} kt";
            InspectorWait = snap.Hold == HoldKind.None ? null : WaitWithTime(snap);
            InspectorWaitId = null;
            InspectorRows = Details(sel, parked).Rows;
            SelectedRoute = RouteAhead(inbound);
            return;
        }
        if (parked.FirstOrDefault(p => p.ObjectId == sel) is { } pk)
        {
            var a = pk.Aircraft;
            InspectorTitle = $"{a.TailNumber} · {a.Type}";
            InspectorSubtitle = $"{a.Airline} · {a.Match.Model.Title}";
            InspectorPhase = Tr.T("estacionado");
            InspectorAltitude = Tr.T("en tierra");
            InspectorSpeed = Tr.T("0 kt");
            InspectorWait = null;
            InspectorWaitId = null;
            InspectorRows =
            [
                new(Tr.T("Puesto"), a.Parking.DisplayName, Tr.T("Avión")),
                new(Tr.T("Pasarela"), pk.IsParkedAtc ? Tr.T("sí (Parked ATC)") : Tr.T("no")),
            ];
            SelectedRoute = null;
            return;
        }
        SelectedId = null; // ya no existe
    }

    /// <summary>Lo que le queda de ruta al avión (pushback y rodaje), cada 10 m.</summary>
    private static List<GeoPoint>? RouteAhead(DepartureAgent agent)
    {
        if (!agent.OnGround || agent.Phase >= DeparturePhase.TakeoffRoll)
            return null;
        var points = new List<GeoPoint>();
        if (agent.Phase <= DeparturePhase.Pushback)
        {
            var push = agent.PushbackPath;
            for (var s = 0.0; s < push.Length; s += 10)
                points.Add(agent.Projection.ToGeo(push.PointAt(s)));
        }
        var taxi = agent.TaxiPath;
        for (var s = agent.TaxiProgress; s < taxi.Length; s += 10)
            points.Add(agent.Projection.ToGeo(taxi.PointAt(s)));
        points.Add(agent.Projection.ToGeo(taxi.End));
        return points;
    }

    private static string PhaseText(DeparturePhase phase) => phase switch
    {
        DeparturePhase.DoorsClosing => Tr.T("cerrando puertas"),
        DeparturePhase.BeaconOn => Tr.T("listo para pushback"),
        DeparturePhase.Pushback => Tr.T("pushback"),
        DeparturePhase.PushbackComplete => Tr.T("arranque y desconexión"),
        DeparturePhase.Taxi => Tr.T("rodando"),
        DeparturePhase.HoldingShort => Tr.T("en punto de espera"),
        DeparturePhase.LineUp => Tr.T("entrando a la pista"),
        DeparturePhase.LinedUp => Tr.T("alineado"),
        DeparturePhase.TakeoffRoll => Tr.T("carrera de despegue"),
        DeparturePhase.RejectedTakeoff => Tr.T("despegue abortado"),
        DeparturePhase.Rotation => Tr.T("rotación"),
        DeparturePhase.Climb => Tr.T("ascenso"),
        DeparturePhase.Cruise => Tr.T("crucero"),
        DeparturePhase.Descent => Tr.T("descenso"),
        DeparturePhase.Approach => Tr.T("aproximación"),
        DeparturePhase.Final => Tr.T("final"),
        DeparturePhase.Landing => Tr.T("aterrizando"),
        DeparturePhase.GoAround => Tr.T("motor y al aire"),
        DeparturePhase.Holding => Tr.T("en espera"),
        DeparturePhase.AtStand => Tr.T("en su puesto"),
        _ => Tr.T("terminado"),
    };

    private static string ShortHold(HoldKind hold) => hold switch
    {
        HoldKind.Intersection => Tr.T("espera cruce"),
        HoldKind.Traffic => Tr.T("sigue a otro"),
        HoldKind.UserAircraft => Tr.T("tu avión"),
        HoldKind.PushbackTraffic => Tr.T("espera tráfico"),
        HoldKind.Runway => Tr.T("espera pista"),
        HoldKind.DepartureSeparation => Tr.T("separación"),
        HoldKind.Wake => Tr.T("estela"),
        _ => "",
    };

    private static string HoldText(AgentSnapshot s)
    {
        var who = s.HoldFor;
        return s.Hold switch
        {
            HoldKind.Intersection => Tr.F("Espera el cruce{0}", (who is null ? "" : Tr.F(" (lo tiene {0})", who))),
            HoldKind.Traffic => Tr.F("Mantiene distancia con {0}", who ?? Tr.T("el de adelante")),
            HoldKind.UserAircraft => Tr.T("Tu avión está en su ruta"),
            HoldKind.PushbackTraffic => Tr.F("Pushback en espera por tráfico{0}", (who is null ? "" : $" ({who})")),
            HoldKind.Runway => s.RunwayHold switch
            {
                RunwayHold.Arrival => Tr.T("Espera la pista: llega un avión a aterrizar"),
                RunwayHold.UserOnRunway => Tr.T("Espera la pista: tu avión está en ella"),
                RunwayHold.PilotTurn => Tr.T("Espera la pista: es tu turno (llegaste antes o estás autorizado)"),
                RunwayHold.ShortRunway => Tr.T("Espera: le queda poca pista para despegar"),
                RunwayHold.Crossing => Tr.T("Espera para cruzar la pista"),
                _ => who is null ? Tr.T("Espera la pista (ocupada)") : Tr.F("Espera la pista: {0} está en ella", who),
            },
            HoldKind.DepartureSeparation => Tr.F("Separación con la salida anterior{0}", (who is null ? "" : $" ({who})")),
            HoldKind.Wake => Tr.F("Estela turbulenta detrás de {0}", who ?? Tr.T("un pesado")),
            _ => "",
        };
    }

    private (uint Id, HoldKind Hold, RunwayHold Detail, DateTime Since) _waitClock;

    /// <summary>
    /// El motivo de la espera del avión elegido con su tiempo: lo que falta si se sabe (estela, llegada) o cuánto lleva
    /// esperando.
    /// </summary>
    private string WaitWithTime(AgentSnapshot s)
    {
        var now = DateTime.UtcNow;
        if (_waitClock.Id != s.ObjectId || _waitClock.Hold != s.Hold || _waitClock.Detail != s.RunwayHold)
            _waitClock = (s.ObjectId, s.Hold, s.RunwayHold, now);
        var text = HoldText(s);
        if (!double.IsNaN(s.HoldLeftS) && !double.IsInfinity(s.HoldLeftS))
            return Tr.F("{0} · faltan {1}", text, Clock(Math.Max(0, s.HoldLeftS)));
        return Tr.F("{0} · esperando hace {1}", text, Clock((now - _waitClock.Since).TotalSeconds));
    }

    private static string Clock(double seconds)
    {
        var t = (int)Math.Round(seconds);
        return $"{t / 60}:{t % 60:00}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        PopulateCommand.NotifyCanExecuteChanged();
        ClearTrafficCommand.NotifyCanExecuteChanged();
        LaunchDepartureCommand.NotifyCanExecuteChanged();
        LaunchSeveralCommand.NotifyCanExecuteChanged();
        NotifyToolCommands();
    }

    private string? _lastUnhandled;

    /// <summary>
    /// Red de seguridad (ADR-144): un error que nadie capturó en el hilo de la interfaz o en una tarea queda en el log (una vez
    /// por error distinto) en vez de cerrar LUAW.
    /// </summary>
    public void ReportUnhandled(Exception ex)
    {
        var key = ex.GetType().Name + ex.Message;
        if (key == _lastUnhandled)
            return;
        _lastUnhandled = key;
        Log(Core.Text.Tr.F("Error sin capturar (la app sigue): {0}", ex));
    }

    /// <summary>Se puede llamar desde cualquier hilo: el panel Diagnóstico recibe la línea en el hilo de la UI.</summary>
    private void Log(string message)
    {
        Trace.WriteLine($"[tráfico] {message}");
        AddLogLine(message);
    }
}
