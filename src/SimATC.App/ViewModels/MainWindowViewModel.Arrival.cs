using Avalonia.Threading;
using SimATC.App.Radar;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;
using SimATC.Core.Units;
using SimATC.Core.World;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// El destino para el ATC del vuelo del piloto (bloque C, ADR-069): sus dependencias (simulador y <c>data/atc</c>), su grafo
/// de rodaje (armado en segundo plano, una vez por plan), la pista y la aproximación, el QNH cerca del destino y el
/// "directo a" en la ruta del radar.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>A esta distancia del destino (NM) la presión del avión sirve como QNH del destino (APROXIMADO).</summary>
    private const double DestinationQnhNm = 60;

    private AirportData? _arrivalAirport;
    private AtcStations? _arrivalStations;
    private AirportAtcData? _arrivalAtc;
    private TaxiGraph? _arrivalGraph;
    private ArrivalInfo? _arrival;
    private Approach? _arrivalApproach;
    private AmbientWeather? _lastAmbient;

    // Centro (ADR-071): FIR de VATSpy y frecuencias de Centro vistas en VATSIM.
    private FirMap? _firs;
    private VatsimCenterStore? _centers;
    private bool _firsLoading;
    private DateTime _centersRefreshed = DateTime.MinValue;
    private readonly List<Fir> _firsHere = [];
    private CenterData? _official;
    private Fir? _centerFir;
    private AtcStation? _centerStation;

    /// <summary>Destino del plan leído (en el hilo de la UI): grafo en segundo plano y dependencias.</summary>
    private async Task PrepareArrivalAsync(AirportData dest)
    {
        if (_arrivalAirport?.Icao == dest.Icao && _arrivalGraph is not null)
            return;
        ClearArrival();
        _ = EnsureCentersAsync();
        _arrivalAirport = dest;
        _arrivalAtc = AirportAtcData.Load(AirportAtcData.PathFor(_dataDir, dest.Icao), Log);
        _arrivalStations = new AtcStations(WithLearnedFrequencies(dest), _arrivalAtc, _settings.AtcLanguage);
        try
        {
            var graph = await Task.Run(() => TaxiGraph.Build(dest));
            if (_arrivalAirport?.Icao == dest.Icao)
                _arrivalGraph = graph;
            Log(Tr.F("Destino {0} listo para el ATC: {1} dependencias, rodaje con {2} nodos.", dest.Icao, _arrivalStations.All.Count, graph.NodeCount));
        }
        catch (Exception ex)
        {
            Log(Tr.F("No se pudo armar el rodaje de {0} ({1}): sin rodaje al puesto en el destino.", dest.Icao, ex.Message));
        }
        _arrival = null;
    }

    private void ClearArrival()
    {
        _arrivalAirport = null;
        _arrivalStations = null;
        _arrivalAtc = null;
        _arrivalGraph = null;
        _arrival = null;
        _arrivalApproach = null;
        _requestedRunway = null;
    }

    // Pista pedida por el piloto (ADR-097): reemplaza a la del plan para la llegada.
    private string? _requestedRunway;

    /// <summary>
    /// El piloto pide otra pista (ADR-097): se aprueba si existe en el destino y va en el sentido de la que se usa para
    /// aterrizar (la pista en uso si el destino es el aeropuerto activo; si no, la de su llegada). La llegada se rearma.
    /// </summary>
    private Core.Comms.RunwayRequestAnswer RequestRunway(string wanted)
    {
        if (_arrivalAirport is not { } dest)
            return new(false, null);
        var inUse = dest.Icao == Airport?.Icao && ArrivalEnds.Count > 0 ? ArrivalEnds[0] : _arrival?.Runway;
        var want = RunwayHeading(dest, wanted);
        var cur = inUse is null ? null : RunwayHeading(dest, inUse);
        if (want is null || cur is { } c && Math.Abs(GeoMath.AngleDiffDeg(want.Value, c)) > 90)
            return new(false, inUse);
        _requestedRunway = wanted.ToUpperInvariant();
        _arrival = null; // se rearma con la pista pedida
        Log(Tr.F("Llegada del piloto: pide la pista {0} (en uso {1}); aprobada.", _requestedRunway, inUse ?? "-"));
        return new(true, inUse);
    }

    /// <summary>Rumbo verdadero de una cabecera del aeropuerto por su designador ("17R"), o null si no existe.</summary>
    private static double? RunwayHeading(AirportData airport, string ident)
    {
        foreach (var r in airport.Runways)
        {
            if (string.Equals(r.Primary.Ident, ident, StringComparison.OrdinalIgnoreCase))
                return r.Primary.HeadingTrueDeg;
            if (string.Equals(r.Secondary.Ident, ident, StringComparison.OrdinalIgnoreCase))
                return r.Secondary.HeadingTrueDeg;
        }
        return null;
    }

    /// <summary>El destino para la sesión; se rearma solo si cambió la aproximación elegida o llegó el grafo.</summary>
    private ArrivalInfo? ArrivalNow()
    {
        if (_arrivalAirport is not { } dest || _arrivalStations is not { } stations || Plan is not { } p)
            return null;
        var approach = SelectedApproach?.Value?.Approach;
        // La pista: la pedida por el piloto, si no la del plan si está en uso, si no la que está en uso (METAR, ADR-100).
        // Al retomar un vuelo (ADR-105), la pista que se usaba reemplaza a la del plan hasta que haya viento del destino.
        var planned = _resumeRunway ?? p.Destination.PlannedRunway;
        var runway = _requestedRunway ?? DestinationRunwayInUse(planned) ?? planned;
        // Con la Torre del destino o ya en tierra, la pista no cambia (EBBR 2026-11-14: tras aterrizar pasó de la 01 a la 19).
        if (_arrival is { } current && _session is { Phase: >= Core.Comms.PilotPhase.ArrivalTower })
            runway = current.Runway;
        if (_arrival is null || !ReferenceEquals(approach, _arrivalApproach) || _arrival.Graph != _arrivalGraph
            || runway is not null && !string.Equals(_arrival.Runway, runway, StringComparison.OrdinalIgnoreCase))
        {
            string? changedFrom = null;
            if (_arrival is not null && runway is not null && !string.Equals(_arrival.Runway, runway, StringComparison.OrdinalIgnoreCase))
            {
                Log(Tr.F("Llegada del piloto: pista {0} → {1} (pista en uso en {2}).", _arrival.Runway, runway, dest.Icao));
                // No lo pidió el piloto: se le avisa con un modal (ADR-129), porque cambia su aproximación y su STAR.
                // Solo en vuelo: en el puesto, el cambio se ve en el plan y no hace falta interrumpir (ADR-133).
                if (_requestedRunway is null && !string.Equals(runway, p.Destination.PlannedRunway, StringComparison.OrdinalIgnoreCase)
                    && UserState is { OnGround: false })
                    changedFrom = _arrival.Runway;
            }
            _arrivalApproach = approach;
            var created = ArrivalInfo.Create(dest, stations, _arrivalGraph, _arrivalAtc, runway, approach);
            // Restricciones de la STAR del plan para el descenso ("cross ARUNI FL100 or above", ADR-098).
            _arrival = created with
            {
                StarRestrictions = ArrivalInfo.RestrictionsFor(dest, p.StarIdent, p.StarTransition, created.Runway),
                StarName = ArrivalInfo.StarNameFor(dest, p.StarIdent, created.Runway), // autorización de STAR (ADR-101)
                // La transición de la aproximación donde termina la STAR ("expect ILS … via BERRY", ADR-105).
                Transition = ArrivalInfo.TransitionFor(created.Procedure, ArrivalInfo.StarEndFix(dest, p.StarIdent, p.StarTransition, created.Runway)),
            };
            if (changedFrom is not null)
            {
                var now = _arrival;
                var via = now.Transition is { } t ? Tr.F(" vía {0}", t) : "";
                // Confirmación (ADR-131): acepta la nueva, o mantiene la suya si va en el mismo sentido y tiene aproximación.
                var old = changedFrom;
                var keepable = RunwayHeading(dest, old) is { } oh && RunwayHeading(dest, now.Runway) is { } nh
                               && Math.Abs(GeoMath.AngleDiffDeg(oh, nh)) <= 90
                               && Core.Atc.RunwayPlanner.WithApproach(dest, [old, now.Runway]).Contains(old);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Notice is not null)
                        return;
                    var text = Tr.F("{0} está usando la pista {1} para aterrizar (tenías la {2}). Tu aproximación pasa a ser ", dest.Icao, now.Runway, old)
                               + Tr.F("{0}{1}. Revisa la llegada y la aproximación en el FMS.", now.ApproachName ?? Tr.T("la de esa pista"), via);
                    Notice = keepable
                        ? new NoticeDialog(Tr.F("¿Cambiar tu pista de llegada en {0}?", dest.Icao),
                            text + Tr.F(" La {0} va en el mismo sentido: puedes mantenerla y el ATC te la dará.", old), Tr.F("Aceptar la {0}", now.Runway),
                            () => SetPlanArrivalRunway(now.Runway, Tr.T("aceptó la pista en uso")), Tr.F("Mantener la {0}", old))
                        {
                            OnSecondary = () =>
                            {
                                _requestedRunway = old.ToUpperInvariant();
                                _arrival = null;
                                Log(Tr.F("Llegada del piloto: mantiene la pista {0} (no aceptó el cambio a la {1}).", old, now.Runway));
                            },
                        }
                        : new NoticeDialog(Tr.F("Cambió tu pista de llegada en {0}", dest.Icao),
                            text + Tr.F(" La {0} ya no se usa para aterrizar.", old), Tr.T("Entendido"),
                            () => SetPlanArrivalRunway(now.Runway, Tr.T("la anterior ya no se usa")));
                });
            }
        }
        return _arrival;
    }

    /// <summary>
    /// QNH del destino: la presión a nivel del mar donde está el avión, solo cerca del destino (el simulador da el clima en
    /// la posición del avión). Lejos, null: el ATC da la altitud sin QNH.
    /// </summary>
    private int? DestinationQnh()
    {
        if (_lastAmbient is not { } amb || _arrivalAirport is not { } dest
            || GeoMath.DistanceM(amb.Position, dest.Reference) / UnitConversions.MetersPerNauticalMile > DestinationQnhNm)
            return null;
        return (int)Math.Round(amb.SeaLevelPressureHpa);
    }

    /// <summary>"Route direct (fijo)": la ruta del radar sigue desde el avión al fijo y de ahí el resto del plan.</summary>
    private void ApplyDirectTo(string fix) => Dispatcher.UIThread.Post(() =>
    {
        if (Plan is not { } p)
            return;
        var route = new List<PlanPoint>();
        if (UserState?.Position is { } here)
            route.Add(new PlanPoint(here, ""));
        var from = false;
        foreach (var f in p.Fixes)
        {
            from |= string.Equals(f.Ident, fix, StringComparison.OrdinalIgnoreCase);
            if (from && !f.IsPseudo && f.Ident != p.Destination.Icao)
                route.Add(new PlanPoint(f.Position, f.Ident));
        }
        if (!from)
            return;
        if (p.Destination.Position is { } d)
            route.Add(new PlanPoint(d, p.Destination.Icao));
        PlanRoute = route;
        Log(Tr.F("Directo a {0}: la ruta del radar sigue desde ahí.", fix));
    });

    private PublicFrequencies? _publicFrequencies;

    private VatsimCenterStore LoadCenters() =>
        VatsimCenterStore.Load(VatsimCenterStore.PathFor(SimATC.Core.AppPaths.Root), VatsimCenterStore.SeedPathFor(_dataDir));

    /// <summary>
    /// El aeropuerto con las frecuencias de Aproximación, Salidas o Centro que el simulador no trae, vistas en VATSIM o de
    /// la tabla pública (ADR-173). Solo para armar las dependencias del ATC.
    /// </summary>
    private SimATC.Core.Airports.AirportData WithLearnedFrequencies(SimATC.Core.Airports.AirportData airport)
    {
        _centers ??= LoadCenters();
        _publicFrequencies ??= PublicFrequencies.Load(PublicFrequencies.PathFor(_dataDir));
        return LearnedFrequencies.Augment(airport, _centers, _publicFrequencies, Log);
    }

    /// <summary>Las FIR (una vez, en segundo plano) y los Centros guardados; cada 30 min se leen los Centros en línea.</summary>
    private async Task EnsureCentersAsync()
    {
        if (_firs is null && !_firsLoading)
        {
            _firsLoading = true;
            try
            {
                var dat = FirMap.DatPathFor(_dataDir);
                var bounds = FirMap.BoundariesPathFor(_dataDir);
                _firs = await Task.Run(() => FirMap.Load(dat, bounds));
                _centers ??= LoadCenters();
                _official = CenterData.Load(CenterData.PathFor(_dataDir), Log);
                Log(Tr.F("FIR de VATSpy: {0} límites; {1} Centros oficiales (AIP) y {2} vistos en VATSIM.", _firs.Count, _official.Count, _centers.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or KeyNotFoundException or InvalidOperationException)
            {
                Log(Tr.F("Sin FIR de VATSpy ({0}): sin Centro en ruta.", ex.Message));
            }
            finally
            {
                _firsLoading = false;
            }
        }
        if (_centers is { } store && (DateTime.UtcNow - _centersRefreshed).TotalMinutes >= 30)
        {
            _centersRefreshed = DateTime.UtcNow;
            if (await store.RefreshAsync(Log, CancellationToken.None) > 0)
                store.Save(VatsimCenterStore.PathFor(SimATC.Core.AppPaths.Root));
            _centerFir = null; // puede haber frecuencias nuevas
        }
    }

    /// <summary>
    /// El Centro en esa posición: el oficial (AIP) si lo hay; si no, el de la frecuencia vista en VATSIM; si tampoco, null
    /// (lo sigue llevando la anterior).
    /// </summary>
    private AtcStation? CenterAt(GeoPoint here)
    {
        if (_firs is not { } firs)
            return null;
        firs.AllAt(here, _firsHere);
        if (_firsHere.Count == 0)
            return null;
        // Primero el dato oficial (AIP, ADR-072), con el sector según la posición.
        foreach (var fir in _firsHere)
        {
            if (_official?.StationFor(fir.Icao, here) is { } official)
            {
                if (_centerStation is not { } last || last.Khz != official.Khz || last.Callsign != official.Callsign)
                    _centerStation = official;
                _centerFir = null;
                return _centerStation;
            }
        }
        if (!ReferenceEquals(_firsHere[0], _centerFir))
        {
            // De la más específica (TMA, sector) a la FIR: la primera con frecuencia vista en VATSIM.
            _centerFir = _firsHere[0];
            _centerStation = null;
            foreach (var fir in _firsHere)
            {
                if (_centers?.KhzFor(fir) is { } khz)
                {
                    _centerStation = new AtcStation(AtcRole.Center, fir.Callsign(_settings.AtcLanguage), khz);
                    break;
                }
            }
            _ = EnsureCentersAsync();
        }
        return _centerStation;
    }

    private (string Key, SimATC.Core.Flight.LandingRunway? Runway) _pilotLanding;

    /// <summary>
    /// El piloto en la secuencia de llegadas del motor (ADR-076): con la llegada en curso y el destino como aeropuerto
    /// activo, su distancia al umbral, velocidad y altitud (estela media: el tipo del avión del piloto no se lee aún).
    /// </summary>
    private void UpdatePilotArrival(PilotSession s)
    {
        if (_engine is not { } engine)
            return;
        if (s.Phase is not (PilotPhase.Arrival or PilotPhase.ArrivalTower) || UserState is not { OnGround: false } u
            || ArrivalNow() is not { } arrival || Airport is not { } active || active.Icao != arrival.Airport.Icao)
        {
            engine.PilotArrival = null;
            return;
        }
        var key = active.Icao + "|" + arrival.Runway;
        if (_pilotLanding.Key != key)
            _pilotLanding = (key, SimATC.Core.Flight.FlightPlanner.LandingFor(active, _graph, arrival.Runway, SimATC.Core.Engine.PerformanceCategory.MediumJet));
        if (_pilotLanding.Runway is not { } l)
        {
            engine.PilotArrival = null;
            return;
        }
        engine.PilotArrival = new SimATC.Core.Flight.PilotArrival(l.RunwayIndex, GeoMath.DistanceM(u.Position, l.Threshold), u.GroundSpeedKt,
            u.AltitudeMslFt, SimATC.Core.Fleet.WakeCategory.Medium);
    }
}
