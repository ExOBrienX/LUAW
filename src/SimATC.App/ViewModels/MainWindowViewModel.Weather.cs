using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimATC.Core.Airports;
using SimATC.Core.Atc;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Hito 4d: clima del aeropuerto donde está el piloto (V-18), pista activa por viento (RF-23) y ATIS (RF-24).
/// La pista activa la usan las salidas IA cuando la cabecera está en "Automática".
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Opción del selector de cabecera del panel Tráfico que sigue a la pista activa.</summary>
    public static readonly string AutoRunway = Tr.T("Automática");

    private AirportAtcData? _atcData;
    private ActiveRunwaySelector? _runwaySelector;
    private AtisBuilder? _atis;
    private int? _atisKhz;

    /// <summary>Altitud de transición del aeropuerto (del archivo ATC; si no hay, 18000 ft).</summary>
    public double TransitionAltitudeFt => _atcData?.TransitionAltitudeFt ?? (Airport is { TransitionAltitudeFt: > 0 } a ? a.TransitionAltitudeFt : 18000);
    private DateTime _lastWeatherUtc;
    private int _departureTurn;
    private readonly WindAverager _wind = new();
    private readonly System.Diagnostics.Stopwatch _windClock = System.Diagnostics.Stopwatch.StartNew();
    private double? _lastTl;

    [ObservableProperty]
    public partial string AtisChip { get; set; } = "ATIS —";

    [ObservableProperty]
    public partial string AtisText { get; set; } = Tr.T("Sin ATIS: LUAW solo conoce el clima donde está tu avión.");

    [ObservableProperty]
    public partial string ActiveRunwayText { get; set; } = "";

    /// <summary>Encabezado del panel del ATIS: aeropuerto, letra, frecuencia y hora.</summary>
    [ObservableProperty]
    public partial string AtisTitle { get; set; } = "ATIS";

    /// <summary>De dónde sale el ATIS (para que el piloto sepa qué tan completo es).</summary>
    public string AtisSourceNote =>
        Tr.T("Clima del simulador en tu posición, viento medio de 2 min. Sin nubes ni punto de rocío: el simulador no los entrega.");

    [ObservableProperty]
    public partial WeatherObservation? Weather { get; set; }

    /// <summary>
    /// La pista de salida del piloto (ADR-081, ADR-127): con "Usar las pistas del plan", la del OFP si está en uso (el
    /// sentido del viento o el reparto); si no, las de salida en uso.
    /// </summary>
    public IReadOnlyList<string> PilotDepartureEnds
    {
        get
        {
            // Eligió la salida de LUAW (ADR-113).
            if (ChosenAtcRunway is { } chosen)
                return [chosen];
            return PlanDepartureRunwayInUse() is { } planned ? [planned] : DepartureEnds;
        }
    }

    /// <summary>
    /// La pista de salida del plan, si el piloto puede usarla (ADR-127): con "Usar las pistas del plan", en el origen y en
    /// uso (el sentido del viento o el reparto, aunque los IA la usen para llegar). null si no: sale por las de salida.
    /// </summary>
    private string? PlanDepartureRunwayInUse()
    {
        if (!_settings.UsePlanRunways || Plan?.Origin is not { PlannedRunway: { } planned } o || Airport is not { } ap
            || !string.Equals(o.Icao, ap.Icao, StringComparison.OrdinalIgnoreCase))
            return null;
        var wind = _runwaySelector?.Current?.Ends ?? [];
        return Core.Atc.RunwayOptions.PlanRunwayInUse(ap, planned, wind, wind.Count > 0 ? ArrivalEndsNow() : [], DepartureEnds)
            ? planned.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Cabeceras para despegar ahora: las de salida del modo de pistas del AIP (SCEL: 17R); sin AIP, las del reparto
    /// automático (ADR-091); si no, todas las activas.
    /// </summary>
    public IReadOnlyList<string> DepartureEnds
    {
        get
        {
            if (_runwaySelector?.Current is not { } cur)
                return [];
            // Cambio de pista que no permite mezclar (misma pista en sentido contrario, o pistas que se cruzan): las salidas
            // siguen por la pista vieja mientras aterrizan por ella (EBBR 2026-11-14).
            if (_transitionHoldsDepartures && _transitionDepartures is { } held && InRunwayTransition())
                return held;
            var mode = _atcData?.ModeFor(cur.Ends);
            if (mode?.Departure is { Count: > 0 } dep)
                return dep;
            return RunwayPlanFor(cur)?.Departure is { Count: > 0 } pd ? pd : cur.Ends;
        }
    }

    /// <summary>
    /// ¿La pista sirve para que el piloto salga? Activa para salidas, o la vieja mientras dura el cambio de pista gradual
    /// (ADR-093/106: los que ya iban a ella salen por ella). Sin pista en uso todavía, cualquiera.
    /// </summary>
    private bool DepartureRunwayOpen(string runway)
    {
        var ends = DepartureEnds;
        return ends.Count == 0 || ends.Contains(runway, StringComparer.OrdinalIgnoreCase)
               || _transitionDepartures?.Contains(runway, StringComparer.OrdinalIgnoreCase) == true && DateTime.UtcNow < _transitionUntil;
    }

    // Cambio de pista progresivo (ADR-093): las cabeceras viejas y hasta cuándo.
    private IReadOnlyList<string>? _transitionArrivals;
    private IReadOnlyList<string>? _transitionDepartures;
    private DateTime _transitionUntil;
    private bool _transitionHoldsDepartures;

    /// <summary>Tope (min) del cambio de pista progresivo: después las llegadas pasan a la pista nueva igual.</summary>
    public const int TransitionMaxMin = 10;

    /// <summary>¿Quedan salidas en tierra hacia las cabeceras viejas? Si no (o se pasó el tope), el cambio termina.</summary>
    private bool InRunwayTransition()
    {
        if (_transitionArrivals is null || _transitionDepartures is not { } old)
            return false;
        var pending = DateTime.UtcNow < _transitionUntil
                      && _flights.Values.Any(f => old.Contains(f.Runway) && f.Agent.Phase is < Core.Engine.DeparturePhase.Climb);
        if (!pending)
        {
            Log(Tr.F("Cambio de pista terminado: llegadas por {0}.", string.Join("/", ArrivalEndsNow())));
            _transitionArrivals = null;
            _transitionDepartures = null;
            _transitionHoldsDepartures = false;
        }
        return pending;
    }

    /// <summary>Cabeceras para aterrizar ahora: las del AIP (SCEL: 17L), las del reparto automático o todas las activas.</summary>
    public IReadOnlyList<string> ArrivalEnds => InRunwayTransition() ? _transitionArrivals! : ArrivalEndsNow();

    private IReadOnlyList<string> ArrivalEndsNow()
    {
        {
            if (_runwaySelector?.Current is not { } cur)
                return [];
            var mode = _atcData?.ModeFor(cur.Ends);
            if (mode?.Arrival is { Count: > 0 } arr)
                return arr;
            return RunwayPlanFor(cur)?.Arrival is { Count: > 0 } pa ? pa : cur.Ends;
        }
    }

    private Core.Atc.RunwayPlan? _runwayPlan;
    private string? _runwayPlanFor;
    private string? _runwayDeparturesFor;
    private double _runwayDepartures;
    private bool _runwayRoutesKnown;

    /// <summary>
    /// Reparto automático de pistas (ADR-091) para la pista en uso, sin archivo del AIP: se calcula una vez por cambio de
    /// pista (usa el grafo de rodaje; no en el tick).
    /// </summary>
    private Core.Atc.RunwayPlan? RunwayPlanFor(Core.Atc.RunwayInUse cur)
    {
        if (_atcData?.ModeFor(cur.Ends) is not null || Airport is not { } ap || _graph is not { } g)
            return null;
        // La demanda por hora (llegadas, y otras tantas salidas) decide cuántas pistas por rol (ADR-092: en LEMD salía una
        // sola para despegar con mucho tráfico). Se recalcula si cambia en más de 8 por hora.
        var demand = _director?.ArrivalsPerHour(ap, EffectiveDensity) ?? 0;
        // Las salidas, del horario real de salidas (ADR-139): en un pico de salidas, una pista solo de salidas y otra mixta
        // (ADR-138). Sin horario todavía, otras tantas como llegadas. Solo cambia si se mueve en 8 o más por hora, para que
        // las pistas no vayan y vuelvan por un avión de más o de menos en el horario.
        var departures = _director?.ScheduledDeparturesPerHour(ap.Icao, DateTime.UtcNow) ?? demand;
        // Al cargarse la base de rutas la demanda pasa de una estimación por puertas a la real: se recalcula en ese momento
        // (ADR-165; en KMIA quedó 5 minutos con una sola pista mixta para 20 llegadas por hora).
        var known = _director?.RoutesKnown == true;
        if (_runwayDeparturesFor == ap.Icao && known == _runwayRoutesKnown && Math.Abs(departures - _runwayDepartures) < 8)
            departures = _runwayDepartures;
        _runwayRoutesKnown = known;
        _runwayDeparturesFor = ap.Icao;
        _runwayDepartures = departures;
        // Las pistas del plan del piloto en este aeropuerto pesan en el reparto (ADR-137).
        var (planDep, planArr) = PlanRunwaysAt(ap.Icao, asLoaded: true);
        var key = $"{ap.Icao}:{cur.Label}:{known}:{Math.Round(demand / 8)}:{departures:0}:{planDep}:{planArr}";
        if (_runwayPlanFor == key)
            return _runwayPlan;
        _runwayPlanFor = key;
        _runwayPlan = Core.Atc.RunwayPlanner.Plan(ap, g, cur.Ends, Weather?.WindFromTrueDeg ?? 0, Weather?.WindKt ?? 0,
            arrivalsPerHour: demand, departuresPerHour: departures, planDeparture: planDep, planArrival: planArr);
        Log(Tr.F("Pistas {0} ({1:0} llegadas y {2:0} salidas por hora): {3}.", ap.Icao, demand, departures, _runwayPlan.Reason));
        return _runwayPlan;
    }

    /// <summary>
    /// Las pistas del plan del piloto en ese aeropuerto (ADR-127): la de salida si es el origen y la de llegada si es el
    /// destino. Sin plan o con "Usar las pistas del plan" apagado, ninguna. Deciden el sentido con viento calmo y, desde
    /// ADR-137, pesan a favor de su rol en el reparto (sin forzarlo: en el Headless, forzarlo en LEMG causó bloqueos).
    /// </summary>
    /// <param name="asLoaded">Las del plan tal como se cargó (SimBrief), sin lo que el piloto eligió después en las pestañas
    /// (ADR-140): para el reparto, que si no sigue cada clic del piloto (vuelo EGLL-EBBR: los puntos verdes saltaban
    /// 01 → 07L → 07R con cada pista que elegía).</param>
    private (string? Departure, string? Arrival) PlanRunwaysAt(string icao, bool asLoaded = false)
    {
        if (!_settings.UsePlanRunways || (asLoaded ? _loadedPlan : Plan) is not { } p)
            return (null, null);
        var dep = string.Equals(p.Origin.Icao, icao, StringComparison.OrdinalIgnoreCase) ? p.Origin.PlannedRunway : null;
        var arr = string.Equals(p.Destination.Icao, icao, StringComparison.OrdinalIgnoreCase)
                  && !string.Equals(p.Origin.Icao, icao, StringComparison.OrdinalIgnoreCase) ? p.Destination.PlannedRunway : null;
        return (dep, arr);
    }

    /// <summary>Al cargar el aeropuerto: datos ATC opcionales (data/atc), selector de pista y ATIS.</summary>
    private void InitAirportAtc(AirportData airport)
    {
        _atcData = AirportAtcData.Load(AirportAtcData.PathFor(_dataDir, airport.Icao), Log);
        OnPropertyChanged(nameof(TransitionAltitudeFt));
        var limits = RunwaySelectionLimits.Default with { PreferredMaxTailwindKt = _atcData?.PreferredMaxTailwindKt };
        _runwaySelector = new ActiveRunwaySelector(airport, _atcData?.PreferredRunways, limits);
        var kind = _atcData?.AtisDepartureKhz is not null ? AtisKind.Departure : AtisKind.Combined;
        _atis = new AtisBuilder(AerodromeName(airport), kind);
        _wind.Clear();
        _lastTl = null;
        RebuildSession();
        Log(_atcData is null
            ? Tr.F("{0}: sin data/atc; se usan solo los datos del simulador.", airport.Icao)
            : Tr.F("{0}: datos ATC de {1}{2}.", airport.Icao, _atcData.Source.Title, (_atcData.Reviewed ? "" : Tr.T(" (sin revisar)"))));
    }

    /// <summary>
    /// Nombre del aeródromo para el ATIS: el del distintivo de Torre del archivo ATC ("Santiago Tower" → "Santiago"), o el
    /// que trae el simulador en la frecuencia de Torre; si no, el nombre del aeropuerto.
    /// </summary>
    private string AerodromeName(AirportData airport)
    {
        if (_atcData?.Position(AtcRole.Tower)?.CallsignEn is { } cs && cs.EndsWith(" Tower", StringComparison.Ordinal))
            return cs[..^" Tower".Length];
        var tower = airport.Frequencies.FirstOrDefault(f => f.Type == FrequencyType.Tower)?.Name;
        return tower is { Length: > 0 } t
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant())
            : airport.Name;
    }

    /// <summary>Llega desde el hilo del puente cada ~1 s.</summary>
    private void OnAmbientWeather(AmbientWeather ambient) => Dispatcher.UIThread.Post(() => ApplyWeather(ambient));

    private void ApplyWeather(AmbientWeather ambient)
    {
        _lastAmbient = ambient; // QNH cerca del destino (bloque C)
        if (Airport is not { } airport || _runwaySelector is null || _atis is null)
            return;
        var now = DateTime.UtcNow; // hora del PC: la del simulador por SimConnect está por verificar (V-20)
        // Solo cuentan las muestras tomadas en el aeropuerto: el viento medio de 2 min es el del aeropuerto.
        var atAirport = WeatherObservation.IsAtAirport(airport.Reference, ambient);
        var t = _windClock.Elapsed.TotalSeconds;
        if (atAirport)
            _wind.Add(t, ambient.WindFromTrueDeg, ambient.WindKt);
        var obs = atAirport && _wind.Current(t) is { } wind
            ? WeatherObservation.AtAirport(airport.Icao, airport.Reference, ambient, wind, now)
            : null;
        // Lejos del aeropuerto: su METAR de internet (ADR-100), si hay.
        obs ??= MetarObservation(airport, ambient.MagVarDeg);
        if (obs is null)
        {
            // Lejos del aeropuerto: se mantienen la última pista y el último ATIS.
            if (Weather is null)
                AtisText = Tr.F("Sin ATIS de {0}: LUAW solo conoce el clima donde está tu avión, y no estás en el aeropuerto.", airport.Icao);
            // Sin pista todavía (destino activado en vuelo): una provisional para que el tráfico y el ATC funcionen ya
            // (prueba de Marco 2026-11-06: 30 min sin pista en SCIE, sin llegadas controladas). ADR-089.
            var planned = Plan is { } pl ? (pl.Destination.Icao == airport.Icao ? pl.Destination.PlannedRunway
                : pl.Origin.Icao == airport.Icao ? pl.Origin.PlannedRunway : null) : null;
            if (_runwaySelector.SetProvisional(planned) && _runwaySelector.Current is { } prov)
            {
                Log(Tr.F("Pista en uso {0}: {1} ({2}).", airport.Icao, prov.Label, prov.Reason));
                ActiveRunwayText = Tr.F("Pista en uso {0} · {1}", prov.Label, prov.Reason);
                UpdatePlanViews();
                UpdateTaxiFlow();
            }
            return;
        }
        var elapsed = _lastWeatherUtc == default ? 0 : (now - _lastWeatherUtc).TotalSeconds;
        _lastWeatherUtc = now;
        Weather = obs;
        var prevArrivals = _runwaySelector.Current is null ? null : ArrivalEnds;
        var prevDepartures = _runwaySelector.Current is null ? null : DepartureEnds;
        var prevHeading = _runwaySelector.Current?.HeadingTrueDeg;
        // Con viento calmo, el sentido lo decide la pista del plan (ADR-127).
        var (planDep, planArr) = PlanRunwaysAt(airport.Icao);
        _runwaySelector.Planned = planDep ?? planArr;
        if (_runwaySelector.Update(obs.WindFromTrueDeg, obs.WindKt, elapsed))
        {
            // Cambio de sentido: progresivo (ADR-093). Las llegadas siguen por la pista vieja mientras queden salidas
            // rodando hacia ella (como mucho TransitionMaxMin); las salidas nuevas ya van a la nueva.
            if (prevArrivals is { Count: > 0 } && prevDepartures is { Count: > 0 } && prevHeading is { } ph
                && Math.Abs(Core.Geo.GeoMath.AngleDiffDeg(ph, _runwaySelector.Current!.HeadingTrueDeg)) > 90)
            {
                _transitionHoldsDepartures = false;
                var newDepartures = DepartureEnds;
                _transitionArrivals = prevArrivals;
                _transitionDepartures = prevDepartures;
                _transitionUntil = DateTime.UtcNow.AddMinutes(TransitionMaxMin);
                _transitionHoldsDepartures = _graph is { } g
                    && !Core.Atc.RunwayPlanner.CanMixDuringChange(airport, g, prevArrivals, newDepartures);
                if (_transitionHoldsDepartures)
                    Log(Tr.F("Cambio de pista {0}: las salidas siguen por {1} hasta que terminen las ", airport.Icao, string.Join("/", prevDepartures)) +
                        Tr.F("llegadas (no se puede despegar por {0} mientras se aterriza por {1}).", string.Join("/", newDepartures), string.Join("/", prevArrivals)));
                Log(Tr.F("Cambio de pista {0}: progresivo. Las salidas que ya ruedan siguen por {1} ", airport.Icao, string.Join("/", prevDepartures)) +
                    Tr.F("y las llegadas por {0} hasta que terminen (máx. {1} min).", string.Join("/", prevArrivals), TransitionMaxMin));
            }
            Log(Tr.F("Pista en uso {0}: {1} ({2}).", airport.Icao, _runwaySelector.Current!.Label, _runwaySelector.Current.Reason));
            if (_arrivalAirport?.Icao == airport.Icao)
                _requestedRunway = null; // la pedida por el piloto podía quedar con viento de cola (ADR-100)
            if (_atcData?.ModeFor(_runwaySelector.Current.Ends) is { Verified: false } mode)
                Log(Tr.F("Aviso: el modo de pistas {0} de data/atc/{1}.json no está verificado ({2}).", string.Join("/", mode.Ends), airport.Icao, mode.Notes));
        }
        // Aeropuerto sin pistas: no hay pista en uso ni ATIS de pistas (antes se caía aquí).
        if (_runwaySelector.Current is not { } cur)
        {
            ActiveRunwayText = Tr.T("Sin pista en uso: el aeropuerto no tiene pistas.");
            return;
        }
        // Nivel de transición según el QNH (tabla del AIP); fuera de la tabla, el extremo más cercano y se avisa.
        double? tl = null;
        if (_atcData?.TransitionLevelTable is { } table)
        {
            tl = table.LevelFt(obs.QnhHpa, out var outOfRange);
            if (tl != _lastTl)
                Log(outOfRange
                    ? Tr.F("QNH {0:0.0} fuera de la tabla de nivel de transición de {1}: se usa FL{2:000} (el extremo más cercano).", obs.QnhHpa, airport.Icao, tl / 100)
                    : Tr.F("Nivel de transición {0}: FL{1:000} con QNH {2:0.0}.", airport.Icao, tl / 100, obs.QnhHpa));
            _lastTl = tl;
        }
        // Llegadas y salidas por separado cuando difieren (ADR-102).
        var arrivalsNow = ArrivalsInUse();
        // Pistas de colores en el radar: solo si cambiaron (si no, rearmaría la geometría cada 5 s).
        if (RadarArrivalRunways is null || !RadarArrivalRunways.SequenceEqual(arrivalsNow))
            RadarArrivalRunways = [.. arrivalsNow];
        if (RadarDepartureRunways is null || !RadarDepartureRunways.SequenceEqual(DepartureEnds))
            RadarDepartureRunways = [.. DepartureEnds];
        ActiveRunwayText = (arrivalsNow.Order().SequenceEqual(DepartureEnds.Order())
                               ? Tr.F("Pista en uso {0}", cur.Label)
                               : Tr.F("Llegadas {0} · salidas {1}", string.Join("/", arrivalsNow), string.Join("/", DepartureEnds)))
                           + $" · {cur.Reason}";
        UpdatePlanViews(); // el aviso de pista del plan sigue a la pista en uso
        UpdateTaxiFlow();
        AnnounceRunwayChanges(airport);
        if (_atis.Update(obs, cur, DepartureEnds, tl, ArrivalsInUse()) && _atis.Current is { } msg)
        {
            // Lo esencial a la vista; el texto completo, con un clic en el chip.
            var windText = obs.WindKt < 1 ? "calma" : $"{(int)(Math.Round(obs.WindFromMagneticDeg / 10) * 10) % 360:000}/{obs.WindKt:0}{(obs.GustKt is { } g ? $"G{g:0}" : "")}";
            AtisChip = Tr.F("ATIS {0} · {1} · {2} · QNH {3}{4}", msg.Letter, msg.RunwayLabel, windText, msg.QnhHpa, (tl is { } level ? Tr.F(" · TL{0:000}", level / 100) : ""));
            AtisText = msg.Text;
            var khz = _atcData?.AtisDepartureKhz ?? airport.Frequencies.FirstOrDefault(f => f.Type == FrequencyType.Atis)?.Khz;
            AtisTitle = Tr.F("ATIS {0} · información {1}{2} · {3:HHmm}Z", airport.Icao, AtisBuilder.Phonetic(msg.Letter), (khz is { } f ? $" · {f / 1000.0:0.000}" : ""), msg.TimeUtc);
            // En el chat sale solo si el piloto sintoniza la frecuencia del ATIS (ListenAtis).
            _atisKhz = khz;
            Log(Tr.F("ATIS {0} información {1}.", airport.Icao, msg.Letter));
        }
    }

    private string? _flowFor;

    /// <summary>
    /// Sentidos de circulación de las calles paralelas según la pista de salida en uso (ADR-067), en cualquier aeropuerto;
    /// se recalculan solo cuando cambia la pista.
    /// </summary>
    private void UpdateTaxiFlow()
    {
        IReadOnlyList<string> idents = SelectedRunwayEnd != AutoRunway && SelectedRunwayEnd is { } manual ? [manual] : DepartureEnds;
        var arrivals = ArrivalLandingEnds();
        // Pistas de reserva que sirven de calle (ADR-160), nunca una que esté en uso (p. ej. elegida a mano).
        var standby = new List<int>();
        foreach (var r in _runwayPlan?.Standby ?? [])
        {
            if (!_runwayEnds.Any(x => x.Item2 == r && (idents.Contains(x.Item1) || arrivals.Contains(x.Item1))))
                standby.Add(r);
        }
        var key = string.Join("/", idents) + "|" + string.Join("/", arrivals) + "|" + string.Join("/", standby);
        if (_graph is not { } graph || key == _flowFor)
            return;
        _flowFor = key;
        graph.SetStandbyRunways(standby);
        var ends = new List<(int, bool)>();
        foreach (var (ident, i, p) in _runwayEnds)
        {
            if (idents.Contains(ident))
                ends.Add((i, p));
        }
        graph.SetFlows(ends);
        var arrEnds = new List<(int, bool)>();
        foreach (var (ident, i, p) in _runwayEnds)
        {
            if (arrivals.Contains(ident))
                arrEnds.Add((i, p));
        }
        graph.SetArrivalExitFlows(arrEnds);
        RadarFlowArrows = graph.FlowArrows();
        if (ends.Count > 0)
            Log(Tr.F("Rodaje: {0} tramos de calles paralelas con sentido para la salida por {1}; ", graph.FlowEdgeCount, string.Join("/", idents)) +
                Tr.F("{0} tramos de salida de las pistas de llegada {1}.", graph.ExitFlowEdgeCount, string.Join("/", arrivals)));
    }

    /// <summary>Cabeceras de llegada y de salida en uso, para pintar las pistas en el radar (ADR-102).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? RadarArrivalRunways { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string>? RadarDepartureRunways { get; set; }

    /// <summary>Flechas de sentido de las calles para el radar (ADR-102): se rearman al cambiar los sentidos.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<(Core.Geo.GeoPoint Position, double HeadingDeg)>? RadarFlowArrows { get; set; }

    // Las pistas de llegada y salida anunciadas por última vez, por aeropuerto (ADR-102).
    private string? _announcedRunwaysFor;
    private IReadOnlyList<string> _announcedArrivals = [];
    private IReadOnlyList<string> _announcedDepartures = [];

    /// <summary>
    /// Aviso en la conversación cuando se abre o se cierra una pista de llegada o de salida (ADR-102, bloque D): paralelas
    /// por demanda, cambio por viento. El primer reparto de cada aeropuerto no se anuncia (ya sale en el chip).
    /// </summary>
    private void AnnounceRunwayChanges(AirportData airport)
    {
        var arr = ArrivalsInUse();
        var dep = DepartureEnds;
        if (_announcedRunwaysFor != airport.Icao)
        {
            _announcedRunwaysFor = airport.Icao;
            _announcedArrivals = arr;
            _announcedDepartures = dep;
            return;
        }
        var lines = new List<string>();
        void Diff(IReadOnlyList<string> before, IReadOnlyList<string> now, string role)
        {
            var opened = now.Where(e => !before.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            var closed = before.Where(e => !now.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (opened.Count > 0)
                lines.Add(Tr.F("se abre la {0} para {1}", string.Join(Tr.T(" y la "), opened), role));
            if (closed.Count > 0)
                lines.Add(Tr.F("se cierra la {0} para {1}", string.Join(Tr.T(" y la "), closed), role));
        }
        Diff(_announcedArrivals, arr, Tr.T("llegadas"));
        Diff(_announcedDepartures, dep, Tr.T("salidas"));
        _announcedArrivals = arr;
        _announcedDepartures = dep;
        if (lines.Count == 0)
            return;
        var text = Tr.F("{0}: {1}. Ahora llegadas {2}, salidas {3}.", airport.Icao, string.Join("; ", lines), string.Join("/", arr), string.Join("/", dep));
        Log(text);
        Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System, char.ToUpperInvariant(text[0]) + text[1..]));
        ShowToast(char.ToUpperInvariant(text[0]) + text[1..]); // también como aviso sobre el radar (ADR-107)
    }

    /// <summary>Las de llegada en uso más sus paralelas (que la demanda puede abrir): sus salidas son para dejar la pista.</summary>
    private List<string> ArrivalLandingEnds()
    {
        var list = new List<string>(ArrivalEnds);
        if (Airport is { } ap)
        {
            foreach (var e in ArrivalEnds)
            {
                if (Core.Flight.ParallelRunways.PartnerOf(ap, e) is { } partner && !list.Contains(partner) && !DepartureEnds.Contains(partner))
                    list.Add(partner);
            }
        }
        return list;
    }

    /// <summary>La cabecera para la próxima salida IA: la elegida a mano, o en "Automática" las de salida en uso (alternando).</summary>
    private string? DepartureEndForLaunch()
    {
        if (SelectedRunwayEnd != AutoRunway)
            return SelectedRunwayEnd;
        var ends = DepartureEnds;
        return ends.Count == 0 ? null : ends[_departureTurn++ % ends.Count];
    }
}
