using System.Diagnostics;
using Avalonia.Threading;
using SimATC.Core;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Hito 4c: el ATC conversa con el piloto en el panel de comunicaciones. Lo que escribe va a la dependencia que está en
/// su COM1; la respuesta llega después de una pausa que depende del largo del mensaje, como por radio, y de a una
/// transmisión por vez. Las sugerencias, la ficha de la autorización y la ruta de rodaje siguen a la sesión.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Pausa antes de que el ATC hable (s): una base, más un poco por cada carácter, con un máximo.</summary>
    public const double AtcReplyMinS = 2.0;
    public const double AtcReplyMaxS = 5.5;
    public const double AtcReplyPerCharS = 0.02;

    private Phraseology? _phrases;

    /// <summary>Telefonía de las aerolíneas (hito 4e-3): la forma hablada de los indicativos ("LAN CHILE two zero fife").</summary>
    private AirlineTelephony _airlines = AirlineTelephony.Empty;
    private PilotSession? _session;
    private AtcStations? _stations;
    private readonly Stopwatch _atcClock = Stopwatch.StartNew();
    private readonly Queue<AtcTransmission> _onAir = new();
    private readonly Random _replyJitter = new();
    private bool _transmitting;

    /// <summary>El copiloto automático (hito 4f, RF-34b).</summary>
    private readonly Copilot _copilot = new();

    private void InitAtc()
    {
        Comms.Sent += OnPilotSent;
        InitPushback();
        InitFlights();
        Comms.TuneRequested += target =>
        {
            var radio = target.Radio ?? _settings.TuneRadio;
            _bridge.TuneCom(radio, target.Khz, _settings.TuneToStandby);
            Log(Tr.F("COM{0} {1} → {2}.", radio, (_settings.TuneToStandby ? "standby" : "activa"), RadioFormat.Frequency(target.Khz)));
        };
        _bridge.UserRadiosUpdated += r => Dispatcher.UIThread.Post(() => _radios = r);
        Comms.SquawkRequested += code =>
        {
            _bridge.SetTransponder(code);
            Log(Tr.F("Transponder → {0:0000}.", code));
        };
        LoadPhrases();
        try
        {
            _airlines = AirlineTelephony.Load(AirlineTelephony.PathFor(_dataDir));
            Log(Tr.F("Telefonía de aerolíneas: {0} aerolíneas.", _airlines.Count));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log(Tr.F("No se pudo cargar la telefonía de aerolíneas ({0}): los indicativos se dicen letra por letra.", ex.Message));
        }
        RebuildSession();
    }

    /// <summary>La fraseología del idioma del ATC (§8.6): <c>icao-en</c> o <c>icao-es</c>.</summary>
    private void LoadPhrases()
    {
        try
        {
            // FAA (ADR-099): capa sobre icao-en en EE. UU. (automático) o si se eligió a mano.
            _phrasesFaa = AtcWords.UseFaa(_settings.AtcLanguage, _settings.Phraseology, Airport?.Icao);
            _phrases = Phraseology.Load(Phraseology.PathFor(_dataDir, _settings.AtcLanguage),
                _phrasesFaa ? Phraseology.PathFor(_dataDir, "faa-en") : null);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            _phrases = null;
            Log(Tr.F("No se pudo cargar la fraseología ({0}): el ATC no responde.", ex.Message));
        }
    }

    /// <summary>Cambió el idioma del ATC: otra fraseología, otros distintivos y una sesión nueva (como al cambiar de plan).</summary>
    // ¿Las frases cargadas son las de la FAA? (para recargarlas si cambia el aeropuerto activo en automático)
    private bool _phrasesFaa;

    private void ApplyAtcLanguage()
    {
        LoadPhrases();
        RefreshVoiceChoices();
        RebuildSession();
        Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System, _settings.AtcLanguage == AtcLanguage.Spanish
            ? Tr.T("ATC en español. La conversación con el ATC empieza de nuevo.")
            : Tr.T("ATC en inglés. La conversación con el ATC empieza de nuevo.")));
    }

    /// <summary>El viento de la autorización de despegue, en el idioma del ATC ("190 degrees 11 knots").</summary>
    private string? WindText() => Weather is { } w ? AtcWords.Wind(w.WindFromMagneticDeg, w.WindKt, w.GustKt, _settings.AtcLanguage) : null;

    /// <summary>Con plan, aeropuerto y grafo: una sesión nueva si el plan sale de este aeropuerto.</summary>
    private void RebuildSession()
    {
        // Automático (ADR-099): al pasar a un aeropuerto de EE. UU. (o salir de uno) cambia la fraseología.
        if (_phrases is not null && _phrasesFaa != AtcWords.UseFaa(_settings.AtcLanguage, _settings.Phraseology, Airport?.Icao))
            LoadPhrases();
        // En pleno vuelo, el cambio de aeropuerto activo al destino (ADR-076) no rehace la sesión: es el mismo vuelo.
        if (_session is { Phase: >= PilotPhase.TakeoffCleared and < PilotPhase.Arrived } && Plan is not null)
        {
            _stations = Airport is { } ap ? new AtcStations(ap, _atcData, _settings.AtcLanguage) : null;
            ConfigureAiRadio();
            UpdateCommsView();
            return;
        }
        _session = null;
        if (_engine is { } eng)
            eng.PilotArrival = null; // sin sesión, el piloto no está en la secuencia de llegadas
        _engine?.Control.Tower.SetPilot(-1, PilotTowerClearance.None);
        _engine?.Control.Tower.SetPilotHoldingPoint(null);
        if (_engine is { } e)
            e.Coordinator.PilotPriority = null;
        _stations = Airport is { } a ? new AtcStations(a, _atcData, _settings.AtcLanguage) : null;
        ConfigureAiRadio();
        if (Plan is { } p && Airport is { } airport && _graph is { } graph && _phrases is { } phrases && _stations is { } stations
            && string.Equals(p.Origin.Icao, airport.Icao, StringComparison.OrdinalIgnoreCase))
        {
            _session = NewSession(p, airport, _atcData, stations, graph, phrases);
        }
        UserRoute = null;
        UserHoldShort = null;
        UpdateCommsView();
    }


    /// <summary>La sesión del ATC del piloto para el plan <paramref name="p"/>, con el aeropuerto de origen y su grafo.</summary>
    private PilotSession NewSession(Core.Plan.FlightPlan p, Core.Airports.AirportData airport, AirportAtcData? atcData, AtcStations stations, Core.Taxi.TaxiGraph graph,
        Phraseology phrases) =>
        new(new DepartureContext(p, airport, atcData, stations, graph, phrases,
            () => PilotDepartureEnds, () => _atis?.Current?.Letter, () => UserState?.Position,
            () => _settings.Strictness == Strictness.Permissive ? ReadbackStrictness.Permissive : ReadbackStrictness.Realistic,
            () => _settings.Mode == CommsMode.Voice,
            ReadbackTimeS: () => _settings.ReadbackTimeS,
            // La Torre del motor secuencia al piloto con los IA (RF-26); sin tráfico en marcha, la pista está libre.
            RunwayStatus: () => _engine?.Control.Tower.PilotStatus,
            SetTowerClearance: (runway, clearance) => _engine?.Control.Tower.SetPilot(runway, clearance),
            SetHoldingPoint: point => _engine?.Control.Tower.SetPilotHoldingPoint(point),
            SetPilotPriority: point =>
            {
                if (_engine is { } engine)
                    engine.Coordinator.PilotPriority = point is { } p ? new Core.Engine.PilotPriority(p) : null;
            },
            OnGround: () => UserState?.OnGround,
            HeadingDeg: () => UserState?.HeadingTrueDeg,
            GroundSpeedKt: () => UserState?.GroundSpeedKt,
            // La altitud que ve el ATC (ADR-106): con QNH bajo la altitud de transición, nivel de vuelo encima; la real
            // sobre el nivel del mar se aparta 100-200 ft con mucho frío o calor y descuadraba la vigilancia de la senda.
            AltitudeFt: () => UserState is { } u ? AtcAltitudeFt(u) : null,
            Wind: WindText,
            Airlines: _airlines,
            Language: _settings.AtcLanguage,
            // Bloque C (ADR-069/071): el destino, su QNH, el "directo a" y el Centro de cada FIR.
            Arrival: ArrivalNow,
            CenterAt: CenterAt,
            ArrivalRunwayBusy: () => _engine is { PilotArrival: not null } e && (e.PilotArrivalAhead > 0 || e.PilotRunwayOccupied),
            QnhHpa: DestinationQnh,
            DirectTo: ApplyDirectTo,
            StandBusy: PilotStandBusy,
            PreferredStand: () => _arrivalAirport?.Icao == _pilotStandIcao ? _pilotStand : null,
            RequestRunway: RequestRunway,
            // La llegada vigilada (ADR-105): velocidad indicada y vertical del avión.
            IndicatedAirspeedKt: () => UserState?.IndicatedAirspeedKt,
            VerticalSpeedFpm: () => UserState?.VerticalSpeedFpm,
            DepartureRunwayOpen: DepartureRunwayOpen,
            LeaderAhead: () => _engine?.PilotLeaderGap,
            LeaderWake: () => _engine?.PilotLeaderWake,
            WatchLevel: () => _settings.ApproachWatch,
            EnginesRunning: () => UserState?.EnginesRunning,
            MagVarDeg: () => Weather?.MagVarDeg ?? 0,
            ArrivalRunwayClearInS: () => _engine is { PilotArrival: not null } e2 ? e2.PilotRunwayClearInS : null), log: Log);

    private double AtcAltitudeFt(Core.Simulation.UserAircraftState u) =>
        u.QnhAltitudeFt is { } q && u.PressureAltitudeFt is { } pa
            ? q > Math.Max(TransitionAltitudeFt, _arrival?.TransitionAltitudeFt ?? 0) ? pa : q
            : u.AltitudeMslFt;

    /// <summary>
    /// Las radios del avión tienen energía (baterías, APU o tierra; <c>COM STATUS</c>). Sin energía el ATC no escucha ni
    /// habla y el copiloto no llama (prueba de Marco, 2026-10-31: el ATC empezaba apenas se abría la app).
    /// </summary>
    private bool RadiosPowered => _radios is { HasPower: true };

    /// <summary>El piloto escribió: lo que el copiloto iba a decir se cancela.</summary>
    private void OnPilotSent(string text)
    {
        if (_session is not null && !RadiosPowered)
        {
            Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System,
                Tr.T("Tu avión no tiene energía en las radios: nadie te escucha. Enciende las baterías (o el APU) y la radio.")));
            return;
        }
        if (_session is { } session)
            _copilot.PilotSpoke(session, _settings.Copilot);
        SendToAtc(text);
    }

    /// <param name="viaKhz">La frecuencia por la que sale (el copiloto recién la sintonizó y la radio todavía no lo informa); si no, la de la radio con la que transmite.</param>
    private void SendToAtc(string text, int? viaKhz = null)
    {
        if (_session is not { } s)
        {
            Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System, Plan is null
                ? Tr.T("Carga un plan de vuelo para hablar con el ATC.")
                : Tr.F("Tu plan sale de {0} y estás en {1}: el ATC de salida atiende en el aeropuerto del plan.", Plan.Origin.Icao, Airport?.Icao ?? "—")));
            return;
        }
        var com1 = viaKhz ?? TransmitKhz;
        var replies = s.OnPilotMessage(text, com1, _atcClock.Elapsed.TotalSeconds);
        // Lo que dice el piloto y lo que le responde el ATC (después de su pausa) ocupan la frecuencia (RF-38).
        MarkFrequencyBusy(com1, text.Length + replies.Sum(r => r.Text.Length) + (int)(AtcReplyMaxS * CharsPerSecond));
        if (s.LastReadbackAccepted)
            Comms.ConfirmLastPilotMessage();
        // Sin respuesta y sin nadie en esa frecuencia (de origen, ruta o destino); una colación aceptada no lleva respuesta.
        if (replies.Count == 0 && !s.LastReadbackAccepted && com1 is { } khz && StationOn(khz) is null && s.ExpectedStation is { } expected)
        {
            Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System,
                Tr.F("Nadie responde en {0}: {1} está en {2}. Usa \"Sintonizar\" o cámbiala en la radio con la que transmites.", RadioFormat.Frequency(khz), expected.Callsign, expected.FrequencyText)));
        }
        Deliver(replies);
        UpdateCommsView();
    }

    /// <summary>Del reloj de 1 s: llamados si el piloto no colaciona, el ATIS si lo sintonizó y el estado del panel.</summary>
    private void AtcTick()
    {
        ResumeTick();
        RefreshAtcProcedures();
        NoticeTick();
        if (!RadiosPowered)
        {
            // Avión apagado: el ATC espera (el motor sigue: los IA hablan igual, pero no se escuchan).
            ShowAiRadio();
            UpdateCommsView();
            return;
        }
        if (_session is { } s)
        {
            Deliver(s.Tick(_atcClock.Elapsed.TotalSeconds));
            // Avisos de tráfico en rodaje (hito 4e-2): no mientras el ATC está hablando.
            if (!_transmitting && s.Phase == PilotPhase.Taxiing)
                Deliver(s.Traffic(GroundTrafficNearby(), _atcClock.Elapsed.TotalSeconds));
            UpdatePilotArrival(s);
            UpdateFlightRecorder(s);
            UpdateEnrouteRadio();
            // Información de tráfico en vuelo (ADR-075): con la dependencia de ruta o de llegada.
            if (!_transmitting && s.Phase is PilotPhase.WithApproach or PilotPhase.EnRoute or PilotPhase.Arrival)
                Deliver(s.AirTrafficInfo(AirTrafficNearby(), _atcClock.Elapsed.TotalSeconds));
            CopilotTick(s);
        }
        ShowAiRadio();
        ListenAtis();
        UpdateCommsView();
    }

    /// <summary>
    /// El copiloto (hito 4f): cuando le toca, habla por la radio con la que transmite el piloto. Si esa radio no tiene la
    /// frecuencia de la dependencia, la sintoniza primero (si se lo permite) y habla un momento después; si no, avisa.
    /// </summary>
    private void CopilotTick(PilotSession s)
    {
        var settings = _settings.Copilot;
        if (!settings.AnyOn)
            return;
        var now = _atcClock.Elapsed.TotalSeconds;
        if (_copilot.Tick(s, settings, now, _transmitting || now < _quietUntilS || _onAir.Count > 0) is not { } action)
            return;
        // La frecuencia a la que manda un "contact…" que se va a colacionar: se sintoniza apenas se colaciona (vuelo
        // RJTT-RJSS: la Torre quedó sin sintonizar un minuto después del traspaso).
        var nextKhz = action.IsReadback ? s.ReadbackFrequencyKhz : null;
        void TuneTo(int khz, string why)
        {
            if (!settings.Tune || TransmitKhz is { } now2 && Math.Abs(now2 - khz) <= 2)
                return;
            var r = _radios?.Transmitting?.Index ?? 1;
            _bridge.TuneCom(r, khz, standby: false);
            Log(Tr.F("Copiloto: COM{0} → {1} ({2}).", r, RadioFormat.Frequency(khz), why));
        }
        if (action.Ask)
        {
            // Antes de preguntar deja la radio en la frecuencia de esa dependencia.
            TuneTo(action.Station.Khz, action.Station.Callsign);
            // Decisión del capitán: el copiloto pregunta (no transmite); se confirma con la sugerencia.
            Comms.Add(new CommsMessage(DateTime.UtcNow, Tr.T("Copiloto"), Speaker.System,
                Tr.F("Capitán, cuando esté listo pido: «{0}». Toque la sugerencia para enviarlo.", action.Text), "CP"));
            return;
        }
        async void Say()
        {
            await Speak(action.Station.Khz, Voice.VoiceRole.Copilot, "copilot", action.Text,
                _ => Comms.Add(new CommsMessage(DateTime.UtcNow, Tr.T("Copiloto"), Speaker.Pilot, action.Text, "CP")));
            SendToAtc(action.Text, action.Station.Khz);
            if (nextKhz is { } next)
                DispatcherTimer.RunOnce(() => TuneTo(next, Tr.T("cambio de frecuencia colacionado")), TimeSpan.FromSeconds(1));
        }
        if (TransmitKhz is { } khz && Math.Abs(khz - action.Station.Khz) <= 2)
        {
            Say();
            return;
        }
        if (!settings.Tune)
        {
            Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System,
                Tr.F("El copiloto quería llamar a {0}, pero tu radio no está en {1}. Sintonízala y llama tú.", action.Station.Callsign, action.Station.FrequencyText)));
            return;
        }
        // En la activa de la radio con la que transmite (la standby no sirve para hablar ya).
        var radio = _radios?.Transmitting?.Index ?? 1;
        _bridge.TuneCom(radio, action.Station.Khz, standby: false);
        Log(Tr.F("Copiloto: COM{0} → {1} ({2}).", radio, action.Station.FrequencyText, action.Station.Callsign));
        DispatcherTimer.RunOnce(Say, TimeSpan.FromSeconds(1.5));
    }

    private UserRadios? _radios;

    /// <summary>La frecuencia de la radio por la que transmite el piloto (si no se sabe, COM1).</summary>
    private int? TransmitKhz => _radios?.Transmitting is { } tx ? tx.ActiveKhz
        : UserState is { Com1ActiveKhz: > 0 } u ? u.Com1ActiveKhz : null;

    /// <summary>Alguna radio tiene sintonizada esa frecuencia (±2 kHz).</summary>
    private bool AnyRadioOn(int khz) => Comms.Channels.Any(c => c.Available && c.Khz > 0 && Math.Abs(c.Khz - khz) <= 2);

    /// <summary>El ATIS ya mostrado en el chat de cada radio (se repite solo si la radio salió de su frecuencia y volvió, o si cambió la letra).</summary>
    private readonly AtisMessage?[] _atisShown = new AtisMessage?[4];

    /// <summary>
    /// El ATIS se escucha sintonizándolo, como en la realidad: aparece en el chat de la radio que lo tiene al entrar a su
    /// frecuencia o cuando cambia la letra. No se repite por parpadeos de la recepción (prueba de Marco, 2026-10-31).
    /// </summary>
    private void ListenAtis()
    {
        if (_atisKhz is not { } atisKhz || _atis?.Current is not { } msg || Airport is not { } airport)
            return;
        foreach (var c in Comms.Channels)
        {
            var on = c.Available && Math.Abs(c.Khz - atisKhz) <= 2;
            if (!on)
            {
                _atisShown[c.Radio] = null;
                continue;
            }
            if (ReferenceEquals(_atisShown[c.Radio], msg))
                continue;
            _atisShown[c.Radio] = msg;
            c.Add(new CommsMessage(DateTime.UtcNow, $"ATIS {airport.Icao} {RadioFormat.Frequency(atisKhz)}", Speaker.OtherAircraft, msg.Text, "ATIS"));
        }
    }

    /// <summary>Pone las transmisiones en la cola de la frecuencia: se dicen de a una, cada una después de su pausa.</summary>
    private void Deliver(IReadOnlyList<AtcTransmission> replies)
    {
        foreach (var r in replies)
            _onAir.Enqueue(r);
        if (!_transmitting)
            TransmitNext();
    }

    /// <summary>Pausa (s) entre el mensaje del ATC y sus sugerencias: primero se lee, después se ve qué contestar.</summary>
    public const double SuggestionDelayS = 2.5;

    private double _quietUntilS;

    private void TransmitNext()
    {
        if (!_onAir.TryDequeue(out var r))
        {
            _transmitting = false;
            Comms.Transmitting = null;
            return;
        }
        _transmitting = true;
        Comms.Transmitting = Tr.F("{0} transmitiendo…", r.Station.Callsign);
        var delayS = Math.Clamp(AtcReplyMinS + r.Text.Length * AtcReplyPerCharS, AtcReplyMinS, AtcReplyMaxS) + (_replyJitter.NextDouble() - 0.5) * 0.8;
        // Con voz, generar la frase ya tarda ~1,5 s (Probe --voice): la pausa se acorta para no sonar lento.
        if (VoiceOn)
            delayS = Math.Max(0.3, delayS - 1.5);
        DispatcherTimer.RunOnce(async () =>
        {
            var voice = VoiceOn;
            // Con voz, el mensaje aparece cuando empieza a sonar y la cola sigue cuando terminó (etapa 5b).
            await Speak(r.Station.Khz, Voice.VoiceRole.Atc, r.Station.Callsign, r.Text, duration =>
            {
                // Todo lo que pasa por esta cola es para el piloto: se resalta y suena (si está activado y sin voz).
                Comms.Add(new CommsMessage(DateTime.UtcNow, $"{r.Station.Callsign} {r.Station.FrequencyText}", Speaker.Atc, r.Text, Avatar(r.Station.Role)) { ForMe = true });
                if (_settings.MessageSound && !voice)
                    Audio.MessageChime.Play();
                if (duration is { } d)
                    MarkFrequencyBusy(r.Station.Khz, (int)(d * CharsPerSecond));
                // El tiempo para colacionar cuenta desde que el piloto lo ve; las sugerencias, unos segundos después.
                _session?.Heard(_atcClock.Elapsed.TotalSeconds);
                _quietUntilS = _atcClock.Elapsed.TotalSeconds + SuggestionDelayS;
                UpdateCommsView();
            });
            TransmitNext();
            UpdateCommsView();
        }, TimeSpan.FromSeconds(delayS));
    }

    /// <summary>
    /// Qué decir ahora, sugerencias, ficha de la autorización, ruta de rodaje y encabezado. Se llama seguido, pero solo
    /// avisa a la interfaz de lo que cambió (las propiedades no notifican si el valor es el mismo).
    /// </summary>
    private void UpdateCommsView()
    {
        var com1 = TransmitKhz;
        if (_transmitting || _atcClock.Elapsed.TotalSeconds < _quietUntilS)
        {
            // Mientras el ATC habla, y unos segundos después (para leerlo), nada de lo que viene: las sugerencias, la ficha
            // y la ruta aparecen después de su mensaje.
            Comms.SetSuggestions([]);
            Comms.StepText = null;
            Comms.StepTune = null;
            Comms.AtisTune = null;
        }
        else if (_session is not null && !RadiosPowered)
        {
            Comms.SetSuggestions([]);
            Comms.StepText = Tr.T("Enciende el avión (baterías o APU) y la radio: el ATC empieza cuando la radio tiene energía.");
            Comms.StepTune = null;
            Comms.AtisTune = null;
        }
        else if (_session is { } s)
        {
            Comms.SetSuggestions([.. s.Suggestions().Select(x => new Suggestion(x.Text, x.Spoken))]);
            Comms.SetRequestMenu(s.RequestMenu());
            var step = s.NextStep();
            Comms.StepText = step.Text;
            Comms.ClearanceOnGround = s.Clearance is not null && s.Phase < PilotPhase.Airborne;
            Comms.SetOrders(s.Orders());
            Comms.StepTune = step.Station is { } st && (com1 is not { } k || Math.Abs(k - st.Khz) > 2)
                ? Target(st)
                : null;
            Comms.AtisTune = s.Phase == PilotPhase.Parked && _atisKhz is { } ak && Airport is { } ap && !AnyRadioOn(ak)
                ? new TuneTarget($"ATIS {ap.Icao}", ak, RadioFormat.Frequency(ak), AtisRadio)
                : null;
            Comms.Clearance = s.Clearance is { } c
                ? Comms.Clearance is { } old && old.Squawk == c.Squawk && old.Runway == c.Runway
                    ? old
                    : new ClearanceCard(c.Runway, c.Sid, c.LevelText, c.Squawk, c.Next is { } n ? Target(n) : null)
                : null;
            if (s.TaxiRoute is { } route)
                ShowTaxiProgress(route, s.Phase);
            // Rodaje al puesto en el destino (ADR-075): la ruta de Ground en el radar, desde el avión.
            if (s.Phase == PilotPhase.TaxiIn && s.TaxiInRoute is { } taxiIn)
            {
                if (!ReferenceEquals(taxiIn, _shownTaxiIn))
                {
                    _shownTaxiIn = taxiIn;
                    _routeDone = 0;
                    _routeFrom = null;
                    UserRoute = UserState is { } me ? [me.Position, .. taxiIn] : taxiIn;
                    UserHoldShort = null;
                }
                // Como en la salida: lo ya recorrido se borra (antes quedaba la ruta entera hasta el puesto).
                TrimRoute(taxiIn);
            }
            else if (s.Phase == PilotPhase.Arrived && _shownTaxiIn is not null)
            {
                _shownTaxiIn = null;
                UserRoute = null;
            }
            // Vectores a la final (ADR-123/146): el recorrido que esperan (rumbo, base o entrada, interceptación y final); sin
            // él, el rumbo asignado 12 NM desde el avión.
            if (s.AssignedVector is { } vec && UserState is { OnGround: false } flying)
            {
                _shownVector = true;
                UserRoute = s.VectorPath is { Count: >= 2 } path
                    ? path
                    : [flying.Position, GeoMath.Destination(flying.Position, vec.TrueDeg, 12 * UnitConversions.MetersPerNauticalMile)];
                UserHoldShort = null;
            }
            else if (_shownVector)
            {
                _shownVector = false;
                UserRoute = null;
            }
        }
        else
        {
            Comms.SetSuggestions([]);
            Comms.SetOrders([]);
            Comms.ClearanceOnGround = false;
            Comms.StepText = Plan is null ? Tr.T("Carga un plan de vuelo (Plan de vuelo → SimBrief) para hablar con el ATC.") : null;
            Comms.StepTune = null;
            Comms.AtisTune = null;
            Comms.Clearance = null;
        }
        Comms.TransponderCode = UserState?.TransponderCode is { } t and >= 0 ? t : null;
        Comms.CanPlanPushback = !CrewActive && AtStandStopped() && (_session is null || _session.Phase <= PilotPhase.PushbackApproved)
            && _session?.PushedBack != true;
        UpdateAutoPushback();
        UpdatePushbackBlocker();
        // La frecuencia de salida se ofrece recién en el aire (la autorización ya la dio, pero se cambia después de despegar).
        // La frecuencia de salida se ofrece en el aire, y solo si ninguna radio la tiene ya.
        Comms.ShowNextTune = (_session?.IsAirborne ?? UserState is { OnGround: false })
            && !(Comms.Clearance?.Next is { } nextTune && AnyRadioOn(nextTune.Khz));
        UpdateCommsHeader(com1);
    }

    private SimATC.Core.Taxi.PilotTaxiRoute? _shownRoute;
    private int _routeDone;

    /// <summary>
    /// La ruta de rodaje del piloto en el radar, borrando lo ya recorrido: desde el avión hasta el punto de espera. Se
    /// avanza por tramos (el más cercano, nunca hacia atrás) y se redibuja cuando el avión avanzó unos metros. Desde la Torre
    /// en adelante ya no se muestra.
    /// </summary>
    private IReadOnlyList<GeoPoint>? _shownTaxiIn;

    private bool _shownVector;

    private void ShowTaxiProgress(SimATC.Core.Taxi.PilotTaxiRoute route, PilotPhase phase)
    {
        if (phase > PilotPhase.WithTower)
        {
            if (_shownRoute is not null)
            {
                _shownRoute = null;
                UserRoute = null;
                UserHoldShort = null;
            }
            return;
        }
        if (!ReferenceEquals(_shownRoute, route))
        {
            _shownRoute = route;
            _routeDone = 0;
            _routeFrom = null;
            UserRoute = route.Points;
            UserHoldShort = route.HoldShort;
        }
        TrimRoute(route.Points);
    }

    /// <summary>Redibuja <paramref name="pts"/> desde el avión, sin lo ya recorrido (rodaje de salida y al puesto).</summary>
    private void TrimRoute(IReadOnlyList<GeoPoint> pts)
    {
        if (UserState is not { } u || pts.Count < 2)
            return;
        // El tramo más cercano al avión, desde el último recorrido en adelante.
        var best = _routeDone;
        var bestD = double.MaxValue;
        for (var i = _routeDone; i < pts.Count - 1; i++)
        {
            var d = DistanceToSegmentM(u.Position, pts[i], pts[i + 1]);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        if (bestD > 60)
            return;
        // Continua: desde el avión, cada vez que avanzó unos metros (no solo al cambiar de tramo: en un tramo largo la
        // línea quedaba dibujada detrás).
        if (best == _routeDone && _routeFrom is { } from && GeoMath.DistanceM(from, u.Position) < 3)
            return;
        _routeDone = best;
        _routeFrom = u.Position;
        UserRoute = [u.Position, .. pts.Skip(best + 1)];
    }

    private GeoPoint? _routeFrom;

    private static double DistanceToSegmentM(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var proj = new LocalProjection(a);
        var q = proj.ToLocal(p);
        var bb = proj.ToLocal(b);
        var len2 = bb.EastM * bb.EastM + bb.NorthM * bb.NorthM;
        var t = len2 > 0 ? Math.Clamp((q.EastM * bb.EastM + q.NorthM * bb.NorthM) / len2, 0, 1) : 0;
        var dx = q.EastM - bb.EastM * t;
        var dy = q.NorthM - bb.NorthM * t;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static TuneTarget Target(AtcStation s) => new(s.Callsign, s.Khz, s.FrequencyText);

    /// <summary>
    /// Los chats de COM1 a COM3 con lo que tiene cada radio (frecuencia, estación, TX/RX) y su menú "Sintonizar" con la
    /// frecuencia recomendada: en la de transmitir, la dependencia con la que toca hablar; en otra, el ATIS antes de la
    /// autorización y después la siguiente dependencia, para tenerla lista.
    /// </summary>
    private void UpdateChannels(int tx)
    {
        var expected = _session?.ExpectedStation;
        var next = expected?.Role switch
        {
            AtcRole.Delivery => _stations?.For(AtcRole.Ground),
            AtcRole.Ground => _stations?.For(AtcRole.Tower),
            AtcRole.Tower => _session?.DepartureStation,
            _ => null,
        };
        var parked = _session is null || _session.Phase == PilotPhase.Parked;
        foreach (var c in Comms.Channels)
        {
            var radio = _radios?.Coms.FirstOrDefault(r => r.Index == c.Radio);
            var khz = radio is { Available: true } r1 ? r1.ActiveKhz
                : c.Radio == 1 && UserState is { Com1ActiveKhz: > 0 } u ? u.Com1ActiveKhz : 0;
            c.Available = radio?.Available ?? c.Radio == 1;
            // Al cambiar de frecuencia se conserva lo anterior, con un divisor: desde ahí empieza la nueva.
            if (c.Khz > 0 && khz > 0 && Math.Abs(c.Khz - khz) > 2)
            {
                var name = StationOn(khz)?.Callsign ?? (_atisKhz is { } ak0 && Math.Abs(ak0 - khz) <= 2 ? $"ATIS {Airport?.Icao}" : "");
                c.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System,
                    Tr.F("— Cambiaste a {0}{1} —", RadioFormat.Frequency(khz), (name.Length > 0 ? $" · {name}" : "")), "i"));
            }
            c.Khz = khz;
            c.Frequency = khz > 0 ? RadioFormat.Frequency(khz) : "—";
            c.Station = khz <= 0 ? "" : StationOn(khz)?.Callsign
                ?? (_atisKhz is { } ak && Math.Abs(ak - khz) <= 2 ? $"ATIS {Airport?.Icao}" : "");
            c.Transmitting = c.Radio == tx;
            c.Receiving = radio?.Receive ?? c.Radio == 1;

            // Recomendada: transmitir → con quién hablar; otra radio → ATIS (estacionado) o la siguiente dependencia.
            var recommended = c.Transmitting ? expected?.Khz
                : c.Radio == 2 ? (parked && _atisKhz is { } atis ? atis : next?.Khz)
                : null;
            var options = TuneOptionsFor(recommended);
            if (!options.SequenceEqual(c.TuneOptions))
                c.TuneOptions = options;
        }
        Comms.UpdateShown();
    }

    /// <summary>Las frecuencias del aeropuerto para el menú "Sintonizar" (la recomendada primero).</summary>
    private List<TuneOption> TuneOptionsFor(int? recommendedKhz)
    {
        var list = new List<TuneOption>();
        // Solo lo que está al alcance desde donde está el piloto, como el COM del simulador (ADR-078): se actualiza al moverse.
        IEnumerable<AtcStation> known;
        if (UserState is { } u)
        {
            known = _session?.TunableStations(u.Position, u.AltitudeMslFt)
                    ?? (IEnumerable<AtcStation>?)_stations?.All.Where(st => Airport is { } a0 && RadioRange.InRange(st.Role,
                        GeoMath.DistanceM(u.Position, a0.Reference) / UnitConversions.MetersPerNauticalMile,
                        Math.Max(0, u.AltitudeMslFt - a0.ElevationFt))).OrderBy(st => st.Role)
                    ?? [];
        }
        else
        {
            known = _session?.KnownStations() ?? (IEnumerable<AtcStation>?)_stations?.All.OrderBy(st => st.Role) ?? [];
        }
        foreach (var st in known)
            list.Add(new TuneOption(Target(st), st.Khz == recommendedKhz));
        if (_atisKhz is { } atis && Airport is { } ap && (UserState is not { } me
                || RadioRange.InRange(RadioRange.AtisNm, GeoMath.DistanceM(me.Position, ap.Reference) / UnitConversions.MetersPerNauticalMile,
                    Math.Max(0, me.AltitudeMslFt - ap.ElevationFt))))
            list.Insert(0, new TuneOption(new TuneTarget($"ATIS {ap.Icao}", atis, RadioFormat.Frequency(atis)), atis == recommendedKhz));
        list = [.. list.OrderByDescending(o => o.Recommended)];
        // Al final, dejar la radio en una frecuencia libre: el ATC del juego deja de leer el ATIS (idea de Marco).
        if (FreeKhz() is { } free)
            list.Add(new TuneOption(new TuneTarget(Tr.T("Libre (desintonizar)"), free, RadioFormat.Frequency(free)), false));
        return list;
    }

    /// <summary>
    /// Una frecuencia que no usa nadie en el aeropuerto (a más de 50 kHz de todas las suyas), desde 118.000 hacia arriba.
    /// Las radios COM no aceptan "000.000": van de 118.000 a 136.975 MHz.
    /// </summary>
    private int? FreeKhz()
    {
        var used = new List<int>();
        if (Airport is { } airport)
            used.AddRange(airport.Frequencies.Select(f => f.Khz));
        if (_stations is { } stations)
            used.AddRange(stations.All.Select(s => s.Khz));
        if (_atisKhz is { } atis)
            used.Add(atis);
        for (var khz = 118000; khz <= 136975; khz += 25)
        {
            if (used.All(u => Math.Abs(u - khz) > 50))
                return khz;
        }
        return null;
    }

    /// <summary>La dependencia en esa frecuencia: la que conoce la sesión (origen, ruta, destino) o la del aeropuerto.</summary>
    private AtcStation? StationOn(int khz) => _session?.StationOnFrequency(khz) ?? _stations?.OnFrequency(khz);

    /// <summary>Los aviones en vuelo del motor (inyectados o virtuales) para la información de tráfico.</summary>
    private List<AirTraffic> AirTrafficNearby()
    {
        var list = new List<AirTraffic>();
        foreach (var a in _engine?.Snapshot() ?? [])
        {
            if (a.Airborne)
                list.Add(new AirTraffic(a.ObjectId, a.Position, a.HeadingDeg, a.AltitudeFt, a.SpeedKt));
        }
        return list;
    }

    /// <summary>El ATIS se escucha por COM2 si el avión la tiene (como en la cabina: COM1 queda con la dependencia).</summary>
    private int? AtisRadio => _radios?.Coms.Any(c => c.Index == 2 && c.Available) == true ? 2 : null;

    /// <summary>El ícono de cada dependencia en el chat: la abreviatura usual (DEL, GND, TWR, APP, DEP, CTR).</summary>
    private static string Avatar(AtcRole role) => role switch
    {
        AtcRole.Delivery => "DEL",
        AtcRole.Ground => "GND",
        AtcRole.Tower => "TWR",
        AtcRole.Approach => "APP",
        AtcRole.Departure => "DEP",
        _ => "CTR",
    };

    /// <summary>Encabezado del panel: la dependencia en tu COM1 (o con quién deberías hablar).</summary>
    private void UpdateCommsHeader(int? com1)
    {
        var on = com1 is { } k ? StationOn(k) : null;
        var onAtis = com1 is { } c && _atisKhz is { } a && Math.Abs(c - a) <= 2;
        var tx = _radios?.Transmitting?.Index ?? 1;
        Comms.StationName = on?.Callsign ?? (com1 is null ? $"COM{tx} —" : onAtis ? $"ATIS {Airport?.Icao}" : Tr.F("Sin ATC en COM{0}", tx));
        UpdateChannels(tx);
        Comms.Frequency = com1 is { } f
            ? RadioFormat.Frequency(f) + (on is null && !onAtis && _session?.ExpectedStation is { } e ? Tr.F(" · llama a {0} {1}", e.Callsign, e.FrequencyText) : "")
            : "—";
    }
}
