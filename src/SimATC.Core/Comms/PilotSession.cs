using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>En qué parte de la salida está el piloto (hito 4c).</summary>
public enum PilotPhase
{
    /// <summary>En el puesto, sin autorización.</summary>
    Parked,
    /// <summary>Delivery dio la autorización; espera la colación.</summary>
    ClearanceGiven,
    /// <summary>Autorizado (colación correcta).</summary>
    Cleared,
    /// <summary>Ground aprobó el pushback.</summary>
    PushbackApproved,
    /// <summary>Ground dio el rodaje; espera la colación.</summary>
    TaxiGiven,
    /// <summary>Rodando hacia el punto de espera.</summary>
    Taxiing,
    /// <summary>Cerca del punto de espera, Ground lo pasó a Torre; espera la colación de la frecuencia.</summary>
    TowerHandoffGiven,
    /// <summary>Con Torre, todavía sin avisar que está listo.</summary>
    WithTower,
    /// <summary>Torre: "hold short" (espera la colación o que se libere la pista; después Torre lo llama).</summary>
    HoldShortGiven,
    /// <summary>Torre: "line up and wait"; espera la colación.</summary>
    LineUpGiven,
    /// <summary>Alineado, esperando la autorización de despegue.</summary>
    LinedUp,
    /// <summary>Torre autorizó el despegue; espera la colación.</summary>
    TakeoffGiven,
    /// <summary>Autorizado a despegar (colación correcta), todavía en tierra.</summary>
    TakeoffCleared,
    /// <summary>En el aire: tiene que llamar a Approach (o Departure).</summary>
    Airborne,
    /// <summary>Approach lo identificó.</summary>
    WithApproach,
    // Bloque C (ADR-069): el resto del vuelo.
    /// <summary>En ruta (ascenso a crucero, crucero, descenso), con Salida o Centro.</summary>
    EnRoute,
    /// <summary>Con la Aproximación del destino.</summary>
    Arrival,
    /// <summary>Con la Torre del destino (final, aterrizaje).</summary>
    ArrivalTower,
    /// <summary>Aterrizado, librando la pista.</summary>
    Landed,
    /// <summary>Con Ground del destino, rodando al puesto.</summary>
    TaxiIn,
    /// <summary>En el puesto.</summary>
    Arrived,
}

/// <summary>Una transmisión del ATC: quién, en qué frecuencia y qué dijo.</summary>
public sealed record AtcTransmission(AtcStation Station, string Text);

/// <summary>Una sugerencia para el piloto (RF-30b): el texto escrito y su forma hablada (§8.2).</summary>
public sealed record PilotSuggestion(string Text, string Spoken);

/// <summary>Qué le toca hacer al piloto ahora, en español para la interfaz, y con qué dependencia (null: no tiene que hablar).</summary>
public sealed record PilotStep(string Text, AtcStation? Station);

/// <summary>Lo que la sesión necesita del resto de la app. Las funciones se leen cada vez (cambian en vivo).</summary>
public sealed record DepartureContext(
    FlightPlan Plan,
    AirportData Airport,
    AirportAtcData? Atc,
    AtcStations Stations,
    TaxiGraph Graph,
    Phraseology Phrases,
    Func<IReadOnlyList<string>> DepartureEnds,
    Func<char?> AtisLetter,
    Func<GeoPoint?> PilotPosition,
    Func<ReadbackStrictness> Strictness,
    Func<bool>? VoiceMode = null,
    Func<double>? ReadbackTimeS = null,
    Func<PilotRunwayStatus?>? RunwayStatus = null,
    Action<int, PilotTowerClearance>? SetTowerClearance = null,
    Func<bool?>? OnGround = null,
    Func<double?>? HeadingDeg = null,
    Func<double?>? AltitudeFt = null,
    Func<string?>? Wind = null,
    Func<double?>? GroundSpeedKt = null,
    // Declinación magnética donde está el piloto (grados, este positivo: magnético = verdadero − declinación), para los
    // rumbos de los vectores (ADR-123). Sin ella, 0.
    Func<double>? MagVarDeg = null,
    AirlineTelephony? Airlines = null,
    Action<GeoPoint?>? SetHoldingPoint = null,
    Action<GeoPoint?>? SetPilotPriority = null,
    AtcLanguage Language = AtcLanguage.English,
    Func<ArrivalInfo?>? Arrival = null,
    Func<GeoPoint, AtcStation?>? CenterAt = null,
    Func<int?>? QnhHpa = null,
    Action<string>? DirectTo = null,
    Func<bool>? ArrivalRunwayBusy = null,
    Func<int, bool>? StandBusy = null,
    Func<int?>? PreferredStand = null,
    Func<string, RunwayRequestAnswer>? RequestRunway = null,
    // Velocidad indicada (kt) y velocidad vertical (ft/min) del piloto: el ATC vigila la llegada (ADR-105).
    Func<double?>? IndicatedAirspeedKt = null,
    Func<double?>? VerticalSpeedFpm = null,
    // ¿Esa pista sigue sirviendo para salir? (activa o la vieja en el cambio gradual; ADR-106). Sin esto: las activas.
    Func<string, bool>? DepartureRunwayOpen = null,
    // El IA que aterriza justo antes y a cuántas NM va (ADR-107), o null.
    Func<(string Type, double GapNm)?>? LeaderAhead = null,
    // Categoría de estela del que aterriza antes que el piloto (ADR-124: "caution wake turbulence").
    Func<SimATC.Core.Fleet.WakeCategory?>? LeaderWake = null,
    // Qué tan estricta es la vigilancia de la aproximación (ADR-108, Configuración).
    Func<ApproachWatchLevel>? WatchLevel = null,
    // Algún motor encendido (el copiloto pide el rodaje solo con motores, PLAN-PULIDO bloque 1); null si no se sabe.
    Func<bool?>? EnginesRunning = null,
    // Segundos para que la pista de llegada quede libre para el piloto (ADR-120); null o infinito si no se sabe.
    Func<double?>? ArrivalRunwayClearInS = null,
    // Envergadura del avión del piloto (m), según su tipo (ADR-173); null = la supuesta.
    Func<double>? PilotWingspanM = null);

/// <summary>
/// Respuesta de la app al pedido de pista del piloto (ADR-097): aprobada (la llegada ya se rearmó con esa pista) o no, con
/// la pista en uso para decirla.
/// </summary>
public readonly record struct RunwayRequestAnswer(bool Approved, string? RunwayInUse);

/// <summary>Un avión en tierra cerca del piloto, para los avisos de tráfico en rodaje (hito 4e-2).</summary>
public readonly record struct GroundTraffic(uint Id, string Callsign, string Type, GeoPoint Position, double HeadingDeg, double SpeedKt);

/// <summary>
/// La salida IFR del piloto con Delivery, Ground, Tower y Approach (hito 4c, partes 2 y 3). Decide con reglas y habla con la fraseología de
/// <c>data/phraseology</c> (ADR-003, ADR-031). Le habla quien esté en la frecuencia de COM1: si el piloto llama en una
/// frecuencia sin nadie, no hay respuesta (y la app le avisa en qué frecuencia está la dependencia que busca).
/// Sin colación: lo llama por su indicativo y después pregunta "how do you read" (RF-33c); nunca repite la instrucción
/// completa sin que se la pidan.
/// </summary>
public sealed partial class PilotSession(DepartureContext ctx, Random? random = null, Action<string>? log = null)
{
    /// <summary>Distancia al punto de espera a la que Ground pasa al piloto a Torre (m).</summary>
    public const double TowerHandoffM = 150;

    /// <summary>Espera antes del primer llamado (RF-33c): escribir toma más tiempo que hablar.</summary>
    /// <summary>Por defecto en texto (configurable, RF-33c); en voz, la mitad (con un mínimo de 15 s).</summary>
    public const double TextRecallAfterS = 45;

    private double RecallAfterS
    {
        get
        {
            var text = ctx.ReadbackTimeS?.Invoke() ?? TextRecallAfterS;
            return ctx.VoiceMode?.Invoke() == true ? Math.Max(15, text / 2) : text;
        }
    }

    /// <summary>
    /// La última instrucción recién apareció para el piloto (la app la muestra después de una pausa): el tiempo para
    /// colacionar cuenta desde ahora, no desde que el ATC la decidió.
    /// </summary>
    public void Heard(double nowS)
    {
        if (_expected is not null && _recalls == 0)
            _instructionAtS = Math.Max(_instructionAtS, nowS);
    }

    private readonly SquawkGenerator _squawks = new(random);
    private ExpectedReadback? _expected;
    private AtcTransmission? _lastInstruction;
    private double _instructionAtS;
    private int _recalls;

    public PilotPhase Phase { get; private set; } = PilotPhase.Parked;

    /// <summary>
    /// Cuántas instrucciones dio el ATC: una instrucción repetida con el mismo texto ("reduzca a 180 nudos" otra vez) es
    /// otra instrucción y se colaciona de nuevo (vuelo RJTT-RJSS de Marco: el copiloto no la colacionó).
    /// </summary>
    public int InstructionNumber { get; private set; }

    /// <summary>Cuándo dio el ATC la última instrucción al piloto (s, reloj de la sesión).</summary>
    public double LastInstructionAtS => _instructionAtS;

    /// <summary>La frecuencia de un "contact…" pendiente de colacionar, o null.</summary>
    public int? ReadbackFrequencyKhz => _expected?.FrequencyKhz;

    /// <summary>Altura del piloto sobre el aeropuerto de salida (ft), o null si no se sabe.</summary>
    public double? AglFt => ctx.AltitudeFt?.Invoke() is { } a ? a - ctx.Airport.ElevationFt : null;

    public double? GroundSpeedKt => ctx.GroundSpeedKt?.Invoke();

    public bool? EnginesRunning => ctx.EnginesRunning?.Invoke();

    /// <summary>Está en un puesto del que se sale con pushback (lo primero que se pide es el pushback).</summary>
    public bool PushbackExpected => Phase == PilotPhase.Cleared && NearestStand() is not null;

    /// <summary>La Torre ya lo pasó a Salida ("contact…" colacionado): puede llamar aunque vaya bajo.</summary>
    public bool HandedToDeparture { get; private set; }

    /// <summary>El ATC espera una colación (para el copiloto, hito 4f).</summary>
    public bool AwaitingReadback => _expected is not null;

    /// <summary>La colación esperada es de un cambio de frecuencia ("contact Tower 118.1").</summary>
    public bool ReadbackIsFrequencyChange => _expected?.FrequencyKhz is not null;

    /// <summary>A quién se le colaciona: la dependencia que dio la última instrucción.</summary>
    public AtcStation? ReadbackStation => _expected is not null ? _lastInstruction?.Station : null;

    /// <summary>La última instrucción del ATC (su texto), o null.</summary>
    public string? LastInstructionText => _lastInstruction?.Text;

    /// <summary>Cuántas veces el ATC llamó al piloto por no colacionar (RF-33c: 1 = llamado, 2 = "how do you read").</summary>
    public int Recalls => _recalls;
    public string Callsign => ctx.Plan.Callsign.ToUpperInvariant();

    /// <summary>El plan con que trabaja la sesión.</summary>
    public Plan.FlightPlan FlightPlan => ctx.Plan;

    /// <summary>
    /// Plan nuevo del mismo vuelo (ADR-137: el piloto vuelve a cargar SimBrief ya autorizado, rodando o en vuelo): la sesión
    /// sigue donde estaba (autorización, rodaje, fase) y usa la ruta, las pistas y la llegada del plan nuevo.
    /// </summary>
    public void ReplacePlan(Plan.FlightPlan plan) => ctx = ctx with { Plan = plan };

    /// <summary>¿Es el mismo vuelo (indicativo, origen y destino)?</summary>
    public static bool SameFlight(Plan.FlightPlan a, Plan.FlightPlan b) =>
        string.Equals(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Origin.Icao, b.Origin.Icao, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Destination.Icao, b.Destination.Icao, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lo autorizado (para la interfaz y para el resto de la salida).</summary>
    public ClearanceData? Clearance { get; private set; }

    /// <summary>La ruta de rodaje dada por Ground, para pintarla en el radar.</summary>
    public PilotTaxiRoute? TaxiRoute { get; private set; }

    /// <summary>Ya hizo el pushback con el personal de tierra (RF-21c): el rodaje parte desde donde quedó, hacia adelante.</summary>
    public bool PushedBack { get; private set; }

    public void MarkPushedBack() => PushedBack = true;

    /// <summary>El último mensaje del piloto fue una colación correcta (la interfaz lo marca con ✓).</summary>
    public bool LastReadbackAccepted { get; private set; }

    /// <summary>Formas del indicativo que reconoce el parser ("lan205", "lan 205" y con la telefonía, "lan chile 205").</summary>
    public IReadOnlyList<string> CallsignForms
    {
        get
        {
            var cs = Callsign.ToLowerInvariant();
            var split = 0;
            while (split < cs.Length && char.IsLetter(cs[split]))
                split++;
            IReadOnlyList<string> plain = split > 0 && split < cs.Length ? [cs, $"{cs[..split]} {cs[split..]}"] : [cs];
            return ctx.Airlines is { } airlines ? [.. plain.Concat(airlines.Forms(Callsign)).Distinct()] : plain;
        }
    }

    /// <summary>La dependencia con la que el piloto tiene que hablar ahora.</summary>
    public AtcStation? ExpectedStation => Phase switch
    {
        PilotPhase.Parked or PilotPhase.ClearanceGiven => ctx.Stations.For(AtcRole.Delivery),
        <= PilotPhase.TowerHandoffGiven => ctx.Stations.For(AtcRole.Ground),
        <= PilotPhase.TakeoffCleared => ctx.Stations.For(AtcRole.Tower),
        PilotPhase.Airborne => DepartureStation,
        _ => FlightStation ?? DepartureStation,
    };

    /// <summary>
    /// Con quién se habla después de despegar: la de la autorización ("after departure contact…"), Departure (o Approach o
    /// Centro del aeropuerto) y, si el escenario no trae ninguna, el Centro de la zona del aeropuerto (ADR-140: EGLL no tiene
    /// Approach ni Departure en MSFS 2024 y la sesión quedaba en "sigue la SID" todo el vuelo).
    /// </summary>
    public AtcStation? DepartureStation => Clearance?.Next ?? ctx.Stations.For(AtcRole.Departure) ?? AreaCenter;

    private AtcStation? _areaCenter;

    /// <summary>Sin nadie a quien pasarlo, a esta altura sobre el aeropuerto (ft) el vuelo sigue sin ATC.</summary>
    public const double UncontrolledAfterAglFt = 1500;

    /// <summary>En vuelo y sin dependencia: nadie lo controla hasta que aparezca un Centro o la Aproximación del destino.</summary>
    public bool Uncontrolled => Phase is PilotPhase.WithApproach or PilotPhase.EnRoute && FlightStation is null;

    /// <summary>El Centro de la zona del aeropuerto de salida (FIR de VATSpy), leído una vez que existe.</summary>
    private AtcStation? AreaCenter => _areaCenter ??= ctx.CenterAt?.Invoke(ctx.Airport.Reference);

    /// <summary>Ya en el aire: la frecuencia de salida se puede sintonizar.</summary>
    public bool IsAirborne => Phase >= PilotPhase.Airborne;

    /// <summary>
    /// Un mensaje del piloto en la frecuencia <paramref name="com1Khz"/> (null: no se sabe, se toma la esperada).
    /// Devuelve lo que responde el ATC (puede no haber respuesta).
    /// </summary>
    public IReadOnlyList<AtcTransmission> OnPilotMessage(string text, int? com1Khz, double nowS)
    {
        LastReadbackAccepted = false;
        _trafficAckPending = false;
        var station = com1Khz is { } khz ? StationOn(khz) : ExpectedStation;
        if (station is null)
            return [];
        var m = MessageParser.Parse(text, CallsignForms);
        if (!m.HasCallsign && ctx.Strictness() == ReadbackStrictness.Realistic && _expected is null)
            return [Say(station, "atc.say_again_unknown", [])];

        // "Say again" (aunque traiga más palabras): repite la última instrucción de esa dependencia, colacionada o no.
        if (m.Has(PilotIntent.SayAgain) && _lastInstruction is { } again && again.Station.Role == station.Role)
            return [Repeat(again, nowS)];

        // "¿Estoy autorizado?" / "sigo esperando" (ADR-132): lo vigente, reevaluando lo que estaba en espera.
        if (m.Has(PilotIntent.Confirm))
            return ConfirmStatus(station, nowS);
        // Prueba de radio: en cualquier momento.
        if (m.Has(PilotIntent.RadioCheck) && !m.Has(PilotIntent.RequestClearance))
            return [Say(station, "atc.radio_check", [])];

        // Esperando una colación: se valida con la tabla de fallbacks (§8.7).
        if (_expected is { } expected && _lastInstruction is { } last && last.Station.Role == station.Role)
        {
            var r = ReadbackChecker.Check(m, expected, ctx.Strictness());
            switch (r.Verdict)
            {
                case ReadbackVerdict.Correct:
                    return ReadbackCorrect(station);
                case ReadbackVerdict.Missing:
                    return [Say(station, "atc.read_back", new() { ["element"] = Element(r.Element!.Value) })];
                case ReadbackVerdict.Wrong:
                    return [Say(station, "atc.negative_correction", new() { ["correction"] = Correction(r.Element!.Value) })];
                case ReadbackVerdict.PilotSaysAgain:
                    return [Repeat(last, nowS)];
                case ReadbackVerdict.NotUnderstood:
                    return [Say(station, "atc.say_again", [])];
                case ReadbackVerdict.Unable:
                case ReadbackVerdict.OtherRequest:
                    break; // se trata como una solicitud nueva
            }
        }
        // Solicitudes, cada una a su dependencia (si llama a la que no corresponde, se la manda a la correcta).
        if (m.Has(PilotIntent.RequestClearance) || Phase == PilotPhase.Parked && m.Destination is not null)
            return Route(station, AtcRole.Delivery, () => Clearance is not null && Phase >= PilotPhase.ClearanceGiven
                ? ClearanceAgain(station, nowS)
                : IssueClearance(station, nowS));
        if (m.Has(PilotIntent.RequestStartup))
            return Route(station, AtcRole.Ground, () => [Say(station, "atc.startup_approved", [])]);
        if (m.Has(PilotIntent.RequestPushback))
            return Route(station, AtcRole.Ground, () => ApprovePushback(station, nowS));
        if (m.Has(PilotIntent.RequestTaxi))
            return Route(station, AtcRole.Ground, () => IssueTaxi(station, nowS));
        if (m.Has(PilotIntent.ReadyForDeparture))
            return Route(station, AtcRole.Tower, () => ReadyForDeparture(station, nowS));
        // El resto del vuelo (bloque C): primer contacto con la dependencia nueva, solicitudes, final, frustrada, pista libre.
        if (FlightMessage(station, m, nowS) is { } flight)
            return flight;
        // Primer contacto con Approach después de despegar (AIP SCEL: indicativo, altitud y SID).
        if (Phase == PilotPhase.Airborne && m.HasCallsign && DepartureStation is { } dep && dep.Khz == station.Khz)
            return CheckIn(station, nowS);
        // Un acuse sin nada pendiente ("roger", "give way…" ya colacionado): sin respuesta.
        if (m.Has(PilotIntent.Acknowledge))
            return [];
        return m.HasCallsign || m.Intents != PilotIntent.None ? [Say(station, "atc.say_again", [])] : [];
    }

    /// <summary>
    /// Del reloj (≈1 s), RF-33c: si el piloto no colaciona, primero lo llama ("LAN310, Santiago Delivery") y, si sigue
    /// sin responder, pregunta "how do you read". La instrucción solo se repite si el piloto pide "say again".
    /// </summary>
    public IReadOnlyList<AtcTransmission> Tick(double nowS)
    {
        if (Progress(nowS) is { Count: > 0 } said)
            return said;
        if (Phase >= PilotPhase.WithApproach && FlightProgress(nowS) is { Count: > 0 } flight)
            return flight;
        if (_expected is null || _lastInstruction is not { } last)
            return [];
        var waited = nowS - _instructionAtS;
        if (_recalls == 0 && waited >= RecallAfterS)
        {
            _recalls = 1;
            return [Say(last.Station, "atc.call", [])];
        }
        if (_recalls == 1 && waited >= 2 * RecallAfterS)
        {
            _recalls = 2;
            return [Say(last.Station, "atc.how_do_you_read", [])];
        }
        return [];
    }

    /// <summary>Qué debería decir el piloto ahora (RF-30b), con su forma hablada.</summary>
    public IReadOnlyList<PilotSuggestion> Suggestions()
    {
        var list = new List<string>();
        var station = ExpectedStation?.Callsign ?? "";
        switch (Phase)
        {
            case PilotPhase.Parked:
                list.Add(ctx.AtisLetter() is { } atis
                    ? Canonical("pilot.request_clearance", new() { ["station"] = station, ["destination"] = ctx.Plan.Destination.Icao, ["atis"] = AtisBuilder.Phonetic(atis) })
                    : Canonical("pilot.request_clearance_no_atis", new() { ["station"] = station, ["destination"] = ctx.Plan.Destination.Icao }));
                break;
            case PilotPhase.ClearanceGiven when Clearance is { } c:
                list.Add(Canonical((c.Sid, c.LevelText) switch
                {
                    (not null, not null) => "pilot.readback_clearance",
                    (not null, null) => "pilot.readback_clearance_no_level",
                    (null, not null) => "pilot.readback_clearance_no_sid",
                    _ => "pilot.readback_clearance_no_sid_no_level",
                }, new()
                {
                    ["destination"] = ctx.Plan.Destination.Icao, ["sid"] = c.Sid ?? "", ["runway"] = c.Runway,
                    ["level"] = c.LevelText ?? "", ["squawk"] = c.Squawk.ToString("0000"),
                    ["after_departure_readback"] = c.Next is { } n
                        ? Canonical("pilot.readback_after_departure", new() { ["next_station"] = n.Callsign, ["frequency"] = n.FrequencyText })
                        : "",
                }));
                break;
            // El cambio de pista de salida (ADR-106): colacionar la pista nueva (y la SID).
            case PilotPhase.Cleared or PilotPhase.PushbackApproved or PilotPhase.Taxiing when _expected is { Runway: { } newRunway } rc && _lastInstruction is { Station.Role: AtcRole.Ground }
                                                                                       && TaxiRoute?.Runway != newRunway:
                list.Add(rc.Sid is { } newSid
                    ? Canonical("pilot.readback_departure_runway_change", new() { ["runway"] = newRunway, ["sid"] = newSid })
                    : Canonical("pilot.readback_departure_runway_change_no_sid", new() { ["runway"] = newRunway }));
                break;
            case PilotPhase.Cleared:
                if (NearestStand() is { } stand)
                    list.Add(Canonical("pilot.request_pushback", new() { ["stand"] = stand }));
                list.Add(Canonical("pilot.request_taxi", new() { ["station"] = station }));
                break;
            case PilotPhase.PushbackApproved:
                list.Add(Canonical("pilot.request_taxi", new() { ["station"] = station }));
                break;
            case PilotPhase.TaxiGiven when TaxiRoute is { } t:
                list.Add(t.Taxiways.Count > 0
                    ? Canonical("pilot.readback_taxi", new() { ["runway"] = t.Runway, ["route"] = t.ViaText })
                    : Canonical("pilot.readback_taxi_no_route", new() { ["runway"] = t.Runway }));
                break;
            case PilotPhase.TowerHandoffGiven when ctx.Stations.For(AtcRole.Tower) is { } tower:
                list.Add(Canonical("pilot.readback_contact", new() { ["next_station"] = tower.Callsign, ["frequency"] = tower.FrequencyText }));
                break;
            case PilotPhase.WithTower when Clearance is { } c:
                list.Add(Canonical("pilot.ready_for_departure", new() { ["station"] = ctx.Stations.For(AtcRole.Tower)?.Callsign ?? "", ["runway"] = c.Runway }));
                break;
            case PilotPhase.HoldShortGiven when _expected is not null && Clearance is { } c:
                list.Add(Canonical("pilot.readback_hold_short", new() { ["runway"] = c.Runway }));
                break;
            case PilotPhase.LineUpGiven when Clearance is { } c && _lineUpBehind is { } behind:
                list.Add(Canonical("pilot.readback_line_up_behind", new() { ["runway"] = c.Runway, ["traffic"] = behind }));
                break;
            case PilotPhase.LineUpGiven when Clearance is { } c:
                list.Add(Canonical("pilot.readback_line_up", new() { ["runway"] = c.Runway }));
                break;
            case PilotPhase.TakeoffGiven when Clearance is { } c:
                list.Add(Canonical("pilot.readback_takeoff", new() { ["runway"] = c.Runway }));
                break;
            case PilotPhase.Airborne when DepartureStation is { } dep:
                list.Add(_expected is not null
                    ? Canonical("pilot.readback_contact", new() { ["next_station"] = dep.Callsign, ["frequency"] = dep.FrequencyText })
                    : CheckInText(dep));
                break;
            case PilotPhase.Taxiing when _expected is not null && _advisoryReadback is { } advisory:
                list.Add(advisory);
                break;
            // El rodaje otra vez después de "hold position" (hito 4e-2): se colaciona como el primero.
            case PilotPhase.Taxiing when _expected is not null && TaxiRoute is { } t:
                list.Add(t.Taxiways.Count > 0
                    ? Canonical("pilot.readback_taxi", new() { ["runway"] = t.Runway, ["route"] = t.ViaText })
                    : Canonical("pilot.readback_taxi_no_route", new() { ["runway"] = t.Runway }));
                break;
            case PilotPhase.WithApproach when _expected is not null && Clearance?.LevelText is { } level:
                list.Add(Canonical("pilot.readback_climb", new() { ["level"] = level }));
                break;
        }
        if (list.Count == 0 && Phase >= PilotPhase.WithApproach)
            FlightSuggestions(list);
        if (_expected is not null)
            list.Add(Canonical("pilot.say_again", []));
        return [.. list.Select(t => new PilotSuggestion(t, SpokenForm.Of(t, ctx.Airlines, ctx.Language)))];
    }

    /// <summary>El paso actual explicado en español (RF-30b), para la caja "Qué decir ahora".</summary>
    public PilotStep NextStep()
    {
        var delivery = ctx.Stations.For(AtcRole.Delivery);
        var ground = ctx.Stations.For(AtcRole.Ground);
        var tower = ctx.Stations.For(AtcRole.Tower);
        var runway = Clearance?.Runway ?? "";
        return Phase switch
        {
            PilotPhase.Parked => new(Tr.F("Pide la autorización IFR a {0}.", delivery?.Callsign ?? Tr.T("Delivery")), delivery),
            PilotPhase.ClearanceGiven => new(Tr.T("Colaciona la autorización: destino, SID, pista, nivel y squawk."), delivery),
            PilotPhase.Cleared or PilotPhase.PushbackApproved or PilotPhase.Taxiing when _expected is { Runway: { } nr } && TaxiRoute?.Runway != nr
                => new(Tr.F("Cambio de pista: colaciona la nueva pista de salida {0}{1}.", nr, (_expected.Sid is { } ns ? Tr.F(" y la SID {0}", ns) : "")), ground),
            PilotPhase.Cleared => new(NearestStand() is not null
                ? Tr.F("Autorizado. Cuando estés listo, pide el pushback a {0}.", ground?.Callsign ?? Tr.T("Ground"))
                : Tr.F("Autorizado. Cuando estés listo, pide rodaje a {0}.", ground?.Callsign ?? Tr.T("Ground")), ground),
            PilotPhase.PushbackApproved => new(PushedBack
                ? Tr.F("Pushback terminado. Pide rodaje a {0}.", ground?.Callsign ?? Tr.T("Ground"))
                : Tr.T("Pushback aprobado: usa el automático (la línea en el radar) o planéalo a mano. Al terminar, pide rodaje."), ground),
            PilotPhase.TaxiGiven => new(Tr.T("Colaciona la ruta de rodaje."), ground),
            PilotPhase.Taxiing when _expected is not null && _advisoryReadback is not null => new(Tr.T("Colaciona el aviso de tráfico de Ground."), ground),
            PilotPhase.Taxiing when _holdingFor is not null => new(Tr.T("Mantén la posición: Ground te dirá cuándo seguir."), ground),
            PilotPhase.Taxiing => new(TaxiRoute is { } t
                ? Tr.F("Rueda por la ruta del radar hasta el punto de espera de la pista {0}. Ground te pasará a la Torre al llegar.", t.Runway)
                : Tr.T("Rueda hasta el punto de espera."), null),
            PilotPhase.TowerHandoffGiven => new(Tr.F("Colaciona el cambio a {0} y sintonízala.", tower?.Callsign ?? Tr.T("la Torre")), ground),
            PilotPhase.WithTower => new(Tr.F("En el punto de espera de la {0}: avisa a la Torre que estás listo para salir.", runway), tower),
            PilotPhase.HoldShortGiven when _expected is not null => new(Tr.F("Colaciona: esperas antes de la pista {0}.", runway), tower),
            PilotPhase.HoldShortGiven => new(HoldShortStep(runway), tower),
            PilotPhase.LineUpGiven when _lineUpBehind is { } behind => new(Tr.F("Colaciona: te alineas en la {0} detrás del {1} que despega (cuando haya pasado).", runway, behind), tower),
            PilotPhase.LineUpGiven => new(Tr.F("Colaciona y alinéate en la pista {0}.", runway), tower),
            PilotPhase.LinedUp => new(Tr.T("Alineado: espera la autorización de despegue."), tower),
            PilotPhase.TakeoffGiven => new(Tr.T("Colaciona la autorización de despegue."), tower),
            PilotPhase.TakeoffCleared => new(Tr.T("Autorizado a despegar. ¡Buen vuelo!"), null),
            PilotPhase.Airborne when _expected is not null => new(Tr.T("Colaciona el cambio de frecuencia."), tower),
            PilotPhase.Airborne => new(DepartureStation is { } dep
                ? Tr.F("En el aire: cambia a {0} {1} y haz el primer contacto (altitud y SID).", dep.Callsign, dep.FrequencyText)
                : Tr.T("En el aire: sigue la SID."), DepartureStation),
            PilotPhase.WithApproach when _expected is not null => new(Tr.T("Colaciona el nivel."), DepartureStation),
            _ => FlightStep() ?? new(Tr.F("Sigue la SID{0}.", (Clearance?.Sid is { } sid ? $" {sid}" : "")), null),
        };
    }

    // ---------- Delivery ----------

    private IReadOnlyList<AtcTransmission> IssueClearance(AtcStation station, double nowS)
    {
        var p = ctx.Plan;
        // La del plan si está activa para salidas; si no, la activa con el rodaje más corto (ADR-106). Antes: la primera activa.
        var runway = ChooseDepartureRunway(ctx.DepartureEnds(), p.Origin.PlannedRunway);
        // La SID recomendada para la pista en uso: la del plan si sirve; si no, la que la reemplaza (RF-29).
        var (sid, level) = SidFor(runway);
        if (sid is not null && level is null)
            log?.Invoke(Tr.F("Autorización de {0}: la SID {1} no tiene nivel en el archivo ATC ni restricciones en el simulador; se autoriza sin nivel.", Callsign, sid.Name));
        var next = ctx.Atc?.DepartureHandoffTo is { } role ? ctx.Stations.For(role) : ctx.Stations.For(AtcRole.Departure);
        var squawk = _squawks.Next();
        var levelText = level is { } l ? RadioFormat.Level(l.AltitudeFt, l.IsFlightLevel, ctx.Language) : null;
        Clearance = new ClearanceData(sid?.Name, runway, level?.AltitudeFt, levelText, squawk, next);

        var fields = new Dictionary<string, string>
        {
            ["destination"] = p.Destination.Icao, ["runway"] = runway, ["squawk"] = squawk.ToString("0000"),
            ["after_departure"] = next is { } n
                ? ctx.Phrases.Say("atc.clearance.after_departure", new Dictionary<string, string> { ["next_station"] = n.Callsign, ["frequency"] = n.FrequencyText })
                : "",
        };
        if (sid is not null)
            fields["sid"] = sid.Name;
        if (levelText is not null)
            fields["level"] = levelText;
        var id = (sid, levelText) switch
        {
            (not null, not null) => "atc.clearance",
            (not null, null) => "atc.clearance_no_level",
            (null, not null) => "atc.clearance_no_sid",
            _ => "atc.clearance_no_sid_no_level",
        };
        _expected = new ExpectedReadback(Callsign)
        {
            Destination = p.Destination.Icao, Sid = sid?.Name, Runway = runway, LevelFt = level?.AltitudeFt, Squawk = squawk,
        };
        Phase = PilotPhase.ClearanceGiven;
        return [Instruction(station, id, fields, nowS)];
    }

    private IReadOnlyList<AtcTransmission> ReadbackCorrect(AtcStation station)
    {
        _advisoryReadback = null;
        // La última instrucción se conserva: el piloto puede pedir "say again" aunque ya la haya colacionado.
        _expected = null;
        LastReadbackAccepted = true;
        switch (Phase)
        {
            case PilotPhase.ClearanceGiven:
                Phase = PilotPhase.Cleared;
                return [Say(station, "atc.readback_correct", [])];
            case PilotPhase.TaxiGiven:
                Phase = PilotPhase.Taxiing;
                return []; // Ground no responde a una colación de rodaje correcta
            case PilotPhase.TowerHandoffGiven:
                Phase = PilotPhase.WithTower;
                return [];
            case PilotPhase.LineUpGiven:
                Phase = PilotPhase.LinedUp;
                return [];
            case PilotPhase.TakeoffGiven:
                Phase = PilotPhase.TakeoffCleared;
                return [];
            // Colacionó el cambio de pista rodando: el rodaje nuevo desde donde está (ADR-106).
            case PilotPhase.Taxiing when _retaxiAfterReadback:
                _retaxiAfterReadback = false;
                return IssueTaxi(station, _instructionAtS);
            default:
                FlightReadbackDone();
                return []; // hold short, cambio a Departure, ascensos, cambios de frecuencia: sin respuesta
        }
    }

    // ---------- Ground ----------

    private IReadOnlyList<AtcTransmission> ApprovePushback(AtcStation station, double nowS)
    {
        // Fuera de guion (ADR-132): sin autorización de vuelo, primero esa; ya rodando, no se vuelve atrás.
        if (Phase < PilotPhase.Cleared)
            return ClearanceFirst(station, nowS);
        if (Phase >= PilotPhase.TaxiGiven)
            return AlreadyMoving(station, nowS);
        Phase = PilotPhase.PushbackApproved;
        return [Say(station, "atc.pushback_approved", [])];
    }

    private IReadOnlyList<AtcTransmission> IssueTaxi(AtcStation station, double nowS)
    {
        if (Phase < PilotPhase.Cleared || Clearance is not { } c)
            return ClearanceFirst(station, nowS);
        // Ya pasado a la Torre (o en el aire): el rodaje no se vuelve a dar (ADR-132).
        if (Phase >= PilotPhase.TowerHandoffGiven)
            return AlreadyMoving(station, nowS);
        // En una puerta (gate) se sale con pushback, como los IA: primero el pushback y después el rodaje.
        if (Phase == PilotPhase.Cleared && AtGate() && !PushedBack)
            return ApprovePushback(station, nowS);
        if (ctx.PilotPosition() is not { } here || PilotTaxiRoute.From(ctx.Graph, ctx.Airport, here, c.Runway, fromStand: !PushedBack, headingDeg: ctx.HeadingDeg?.Invoke()) is not { } route)
        {
            log?.Invoke(Tr.F("Rodaje de {0}: no hay ruta en el grafo hasta la {1}.", Callsign, c.Runway));
            return [Say(station, "atc.hold_position", [])];
        }
        TaxiRoute = route;
        ctx.SetHoldingPoint?.Invoke(route.HoldShort);
        _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
        Phase = PilotPhase.TaxiGiven;
        // La Torre empieza a seguir la pista del piloto (todavía sin autorización), para tener su estado al llegar.
        if (RunwayIndexOf(c.Runway) is { } idx)
            ctx.SetTowerClearance?.Invoke(idx, PilotTowerClearance.None);
        return route.Taxiways.Count > 0
            ? [Instruction(station, "atc.taxi", new() { ["runway"] = c.Runway, ["route"] = route.ViaText }, nowS)]
            : [Instruction(station, "atc.taxi_no_route", new() { ["runway"] = c.Runway }, nowS)];
    }

    // ---------- Tráfico en rodaje (hito 4e-2) ----------

    /// <summary>Entre dos avisos de tráfico al piloto, como mínimo (s): Ground no lo satura.</summary>
    public const double AdvisoryGapS = 20;

    /// <summary>Distancias de los avisos (m): se mira el tráfico hasta aquí, y "hold position" si uno queda a menos de esto adelante.</summary>
    public const double AdvisoryRangeM = 400;
    public const double HoldPositionM = 70;

    private readonly HashSet<uint> _advised = [];
    private double _lastAdvisoryS = double.MinValue;
    private uint? _holdingFor;
    private string? _advisoryReadback;

    /// <summary>
    /// Mientras el piloto rueda: avisos de tráfico de Ground con las frases de CAP 413 (§8). Un IA que va a cruzar la ruta
    /// adelante antes que el piloto → "give way to the A320 passing left to right"; uno adelante en la misma ruta → "follow
    /// the B737 ahead"; uno a menos de <see cref="HoldPositionM"/> adelante → "hold position" y, cuando se despeja, el rodaje
    /// otra vez. Uno por avión y como mucho uno cada <see cref="AdvisoryGapS"/>.
    /// </summary>
    public IReadOnlyList<AtcTransmission> Traffic(IReadOnlyList<GroundTraffic> traffic, double nowS)
    {
        if (Phase != PilotPhase.Taxiing || _expected is not null || TaxiRoute is not { } route || Clearance is not { } c
            || ctx.PilotPosition() is not { } here || ctx.Stations.For(AtcRole.Ground) is not { } ground)
            return [];
        var proj = ctx.Graph.Projection;
        var line = new Engine.Polyline([.. route.Points.Select(proj.ToLocal)]);
        var me = proj.ToLocal(here);
        var myS = line.Project(me);
        var myKt = ctx.GroundSpeedKt?.Invoke() ?? 0;

        // Después de "hold position": cuando el de adelante se va, el rodaje otra vez.
        if (_holdingFor is { } held)
        {
            var still = false;
            foreach (var t in traffic)
            {
                if (t.Id != held)
                    continue;
                var s = line.ProjectWithin(proj.ToLocal(t.Position), myS, line.Length, out var lat);
                still = lat < 30 && s > myS && s - myS < HoldPositionM + 40;
            }
            if (still)
                return [];
            _holdingFor = null;
            return ContinueTaxi(ground, traffic, line, myS, nowS);
        }

        // Cediendo el paso (prueba de Marco, 2026-10-31: en H con P le pasaban IA sin fin y nadie le decía que siguiera).
        if (_giveWayPoint is { } gwp)
        {
            var gs = line.Project(proj.ToLocal(gwp));
            if (myS > gs + 30 || _priorityAtS is { } since && nowS - since > PriorityMaxS)
            {
                // Ya pasó el cruce (o venció la prioridad): todo normal.
                ResetGiveWay();
            }
            else if (!_taxiAgainGiven && myKt < 2)
            {
                var crossingNow = false;
                foreach (var t in traffic)
                {
                    var p = proj.ToLocal(t.Position);
                    // El que ya rueda por mi ruta en mi sentido no está cruzando: se lo sigue.
                    var ts = line.ProjectWithin(p, myS, line.Length, out var lat);
                    var alongMine = lat < 20 && ts > myS && Math.Abs(GeoMath.AngleDiffDeg(t.HeadingDeg, line.HeadingAt(ts))) < 45;
                    if (t.SpeedKt >= 2 && !alongMine && LocalPoint.Distance(p, me) <= AdvisoryRangeM && Crossing(line, myS, p, t, 6 * 0.514444) is not null)
                        crossingNow = true;
                }
                // Lleva rato cediendo: Ground detiene a los que vienen y le da el paso.
                if (_priorityAtS is null && _giveWays >= PriorityAfterGiveWays && nowS - _lastAdvisoryS >= PrioritySettleS)
                {
                    _priorityAtS = nowS;
                    ctx.SetPilotPriority?.Invoke(gwp);
                    log?.Invoke(Tr.F("{0}: Ground le da el paso en el cruce (cedió {1} veces).", Callsign, _giveWays));
                    return [];
                }
                // Con la prioridad dada (los IA ya frenaron) o sin nadie más cruzando: el rodaje otra vez.
                if (_priorityAtS is { } p0 && nowS - p0 >= PrioritySettleS || _priorityAtS is null && !crossingNow && nowS - _lastAdvisoryS >= ClearAfterS)
                {
                    _taxiAgainGiven = true;
                    return ContinueTaxi(ground, traffic, line, myS, nowS);
                }
            }
        }
        if (nowS - _lastAdvisoryS < AdvisoryGapS)
            return [];

        foreach (var t in traffic.OrderBy(t => GeoMath.DistanceM(t.Position, here)))
        {
            var p = proj.ToLocal(t.Position);
            if (LocalPoint.Distance(p, me) > AdvisoryRangeM || _advised.Contains(t.Id))
                continue;
            var ts = line.ProjectWithin(p, myS, line.Length, out var lateral);
            var onRoute = lateral < 20 && ts > myS + 5;
            if (onRoute && ts - myS < HoldPositionM && myKt > 4)
            {
                _holdingFor = t.Id;
                return Advise(ground, t, "atc.hold_position", "pilot.readback_hold_position", null, nowS);
            }
            // Alcanzando al de adelante (mismo sentido, mucho más rápido y cerca): que ruede más lento.
            if (onRoute && ts - myS < CautionM && myKt - t.SpeedKt > CautionClosingKt
                && Math.Abs(GeoMath.AngleDiffDeg(t.HeadingDeg, line.HeadingAt(ts))) < 45 && _cautioned.Add(t.Id))
                return Advise(ground, t, "atc.caution_slower", "pilot.readback_caution", null, nowS, markAdvised: false);
            if (t.SpeedKt < 2)
                continue;
            if (onRoute && ts - myS < 300 && Math.Abs(GeoMath.AngleDiffDeg(t.HeadingDeg, line.HeadingAt(ts))) < 45)
                return Advise(ground, t, "atc.follow", "pilot.readback_follow", null, nowS);
            // Con la prioridad pedida, los que vienen se detienen: no se le dice que ceda.
            if (_priorityAtS is null && Crossing(line, myS, p, t, Math.Max(myKt, 6) * 0.514444, out var x) is { } direction)
            {
                _giveWays++;
                _giveWayPoint = proj.ToGeo(x);
                _taxiAgainGiven = false;
                return Advise(ground, t, "atc.give_way", "pilot.readback_give_way", direction, nowS);
            }
        }
        return [];
    }

    /// <summary>"Caution, taxi slower": a menos de esto (m) detrás de otro y más rápido que él por esto (kt).</summary>
    public const double CautionM = 150;
    public const double CautionClosingKt = 6;

    private readonly HashSet<uint> _cautioned = [];

    /// <summary>Después de ceder el paso tantas veces, detenido, Ground le da el paso.</summary>
    public const int PriorityAfterGiveWays = 2;

    /// <summary>Segundos para que los IA frenen antes de decirle al piloto que siga; y sin nadie cruzando, para darle el rodaje.</summary>
    public const double PrioritySettleS = 8;
    public const double ClearAfterS = 10;

    /// <summary>La prioridad del piloto vence (s), por si no avanza.</summary>
    public const double PriorityMaxS = 120;

    private int _giveWays;
    private GeoPoint? _giveWayPoint;
    private double? _priorityAtS;
    private bool _taxiAgainGiven;

    private void ResetGiveWay()
    {
        if (_priorityAtS is not null)
            ctx.SetPilotPriority?.Invoke(null);
        _giveWays = 0;
        _giveWayPoint = null;
        _priorityAtS = null;
        _taxiAgainGiven = false;
    }

    /// <summary>
    /// Que siga rodando, con colación (después de "hold position" o de ceder el paso): "continue taxi", o "continue taxi,
    /// follow the Airbus A320" si el que pasó va ahora adelante por la misma ruta. No se repite toda la ruta (prueba de
    /// Marco, 2026-11-01: se sentía pesado). CAP 413 no tiene "continue taxi": frase no oficial (§8.1).
    /// </summary>
    private IReadOnlyList<AtcTransmission> ContinueTaxi(AtcStation ground, IReadOnlyList<GroundTraffic> traffic, Engine.Polyline line,
        double myS, double nowS)
    {
        var proj = ctx.Graph.Projection;
        GroundTraffic? ahead = null;
        var best = double.MaxValue;
        foreach (var t in traffic)
        {
            if (t.SpeedKt < 2)
                continue;
            var ts = line.ProjectWithin(proj.ToLocal(t.Position), myS, line.Length, out var lateral);
            if (lateral < 20 && ts > myS + 10 && ts - myS < 300 && ts - myS < best
                && Math.Abs(GeoMath.AngleDiffDeg(t.HeadingDeg, line.HeadingAt(ts))) < 45)
            {
                best = ts - myS;
                ahead = t;
            }
        }
        _expected = new ExpectedReadback(Callsign);
        if (ahead is { } a)
        {
            _advisoryReadback = Canonical("pilot.readback_continue_taxi_follow", new() { ["traffic"] = a.Type });
            return [Instruction(ground, "atc.continue_taxi_follow", new() { ["traffic"] = a.Type }, nowS)];
        }
        _advisoryReadback = Canonical("pilot.readback_continue_taxi", []);
        return [Instruction(ground, "atc.continue_taxi", [], nowS)];
    }

    /// <summary>
    /// Si el IA (siguiendo su rumbo) cruza la ruta del piloto en los próximos 250 m y llega antes que él (o casi): de qué
    /// lado viene ("left to right" o "right to left", visto desde el piloto). Si no, null.
    /// </summary>
    private const string LeftToRight = "left-to-right";
    private const string RightToLeft = "right-to-left";

    private static string? Crossing(Engine.Polyline line, double myS, LocalPoint p, GroundTraffic t, double myMps) =>
        Crossing(line, myS, p, t, myMps, out _);

    private static string? Crossing(Engine.Polyline line, double myS, LocalPoint p, GroundTraffic t, double myMps, out LocalPoint point)
    {
        point = default;
        var aiMps = t.SpeedKt * 0.514444;
        var end = Engine.Polyline.Offset(p, t.HeadingDeg, 200);
        var pts = line.Points;
        var s0 = 0.0;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var len = LocalPoint.Distance(a, b);
            if (s0 + len >= myS && s0 <= myS + 250 && Intersect(a, b, p, end) is { } x)
            {
                var sx = s0 + LocalPoint.Distance(a, x);
                if (sx > myS + 10)
                {
                    var aiTime = LocalPoint.Distance(p, x) / Math.Max(aiMps, 0.5);
                    var myTime = (sx - myS) / myMps;
                    if (aiTime < 45 && aiTime < myTime + 10)
                    {
                        // Lado del IA respecto de la ruta: a la izquierda → pasa de izquierda a derecha.
                        var cross = (b.EastM - a.EastM) * (p.NorthM - a.NorthM) - (b.NorthM - a.NorthM) * (p.EastM - a.EastM);
                        point = x;
                        return cross > 0 ? LeftToRight : RightToLeft;
                    }
                }
            }
            s0 += len;
        }
        return null;
    }

    private static LocalPoint? Intersect(LocalPoint a, LocalPoint b, LocalPoint c, LocalPoint d)
    {
        var r = (b.EastM - a.EastM, b.NorthM - a.NorthM);
        var s = (d.EastM - c.EastM, d.NorthM - c.NorthM);
        var den = r.Item1 * s.Item2 - r.Item2 * s.Item1;
        if (Math.Abs(den) < 1e-9)
            return null;
        var t = ((c.EastM - a.EastM) * s.Item2 - (c.NorthM - a.NorthM) * s.Item1) / den;
        var u = ((c.EastM - a.EastM) * r.Item2 - (c.NorthM - a.NorthM) * r.Item1) / den;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? new LocalPoint(a.EastM + r.Item1 * t, a.NorthM + r.Item2 * t) : null;
    }

    private IReadOnlyList<AtcTransmission> Advise(AtcStation ground, GroundTraffic t, string atcId, string readbackId, string? direction, double nowS,
        bool markAdvised = true)
    {
        if (markAdvised)
            _advised.Add(t.Id);
        _lastAdvisoryS = nowS;
        var fields = new Dictionary<string, string> { ["traffic"] = t.Type };
        if (direction is not null)
            fields["direction"] = AtcWords.Direction(direction == LeftToRight, ctx.Language);
        _expected = new ExpectedReadback(Callsign);
        _advisoryReadback = Canonical(readbackId, new() { ["traffic"] = t.Type });
        log?.Invoke(Tr.F("{0}: aviso de tráfico ({1}) por {2}.", Callsign, atcId, t.Callsign));
        return [Instruction(ground, atcId, fields, nowS)];
    }

    // ---------- Tower y Approach ----------

    /// <summary>Lo que el ATC hace solo, según dónde está el piloto: el paso a Torre, alinear o despegar y el paso a Departure.</summary>
    private IReadOnlyList<AtcTransmission> Progress(double nowS)
    {
        if (Clearance is { } early && TookOffEarly())
            return Airborne(early, nowS);
        if (_expected is not null || Clearance is not { } c)
            return [];
        // Su pista dejó de usarse para salidas: Ground le da la nueva (ADR-106).
        if (DepartureRunwayChange(nowS) is { Count: > 0 } change)
            return change;
        switch (Phase)
        {
            case PilotPhase.Taxiing when TaxiRoute is { } t && ctx.PilotPosition() is { } here && GeoMath.DistanceM(here, t.HoldShort) < TowerHandoffM:
                return HandToTower(nowS);
            case PilotPhase.HoldShortGiven when ctx.Stations.For(AtcRole.Tower) is { } tower && Runway() is { } st
                                                && (st.Free && st.ArrivalAllowsLineUp || CanLineUpBehind(st)):
                return TowerDecision(tower, c, nowS);
            case PilotPhase.LinedUp when ctx.Stations.For(AtcRole.Tower) is { } tower && Runway() is { TakeoffClear: true, ArrivalAllowsRoll: true }:
                return ClearForTakeoff(tower, c, nowS);
            // Autorizado a alinearse o a despegar, pero sigue fuera de la pista y ya viene una llegada encima: la Torre lo
            // deja donde está y la llegada aterriza (ADR-129: el piloto tardó cinco minutos en entrar y la llegada frustró).
            case PilotPhase.LineUpGiven or PilotPhase.TakeoffGiven or PilotPhase.TakeoffCleared
                when ctx.Stations.For(AtcRole.Tower) is { } tower && ctx.OnGround?.Invoke() != false && !OnDepartureRunway(c)
                     && Runway() is { ArrivalInS: < TowerControl.PilotCancelBeforeArrivalS }:
                Phase = PilotPhase.HoldShortGiven;
                SetTower(c, PilotTowerClearance.Ready);
                _lineUpBehind = null;
                _expected = new ExpectedReadback(Callsign) { Words = ["holding", "hold", "mantengo", "manteniendo", "mantener"] };
                _readbackHint = Canonical("pilot.readback_hold_position", []);
                log?.Invoke(Tr.F("{0}: autorización de pista cancelada (sigue fuera de la pista {1} y hay una llegada en final).", Callsign, c.Runway));
                return [Instruction(tower, "atc.hold_position", [], nowS)];
            case PilotPhase.TakeoffCleared when ctx.OnGround?.Invoke() == false:
                return Airborne(c, nowS);
            // Nadie a quien pasarlo (sin Departure, Approach ni Centro conocido): el vuelo sigue solo, sin ATC, y llama a
            // la primera dependencia que aparezca (ADR-173: EGLL-EDDF quedó en "sigue la SID" hasta Frankfurt).
            case PilotPhase.Airborne when DepartureStation is null && AglFt is >= UncontrolledAfterAglFt:
                Phase = PilotPhase.EnRoute;
                _cruiseCleared = true;
                _leftWithoutAtc = true;
                log?.Invoke(Tr.F("{0}: sin ATC después de despegar (no hay Salidas, Aproximación ni Centro conocido); sigue solo.", Callsign));
                return [];
            default:
                return [];
        }
    }

    /// <summary>Ya en el aire: si la autorización no dijo a quién llamar después de despegar, lo dice la Torre.</summary>
    private IReadOnlyList<AtcTransmission> Airborne(ClearanceData c, double nowS)
    {
        Phase = PilotPhase.Airborne;
        log?.Invoke(Tr.F("{0} en el aire.", Callsign));
        if (c.Next is null && DepartureStation is { } dep && ctx.Stations.For(AtcRole.Tower) is { } tw && dep.Khz != tw.Khz)
        {
            _expected = new ExpectedReadback(Callsign) { FrequencyKhz = dep.Khz };
            HandedToDeparture = true;
            return [Instruction(tw, "atc.contact", new() { ["next_station"] = dep.Callsign, ["frequency"] = dep.FrequencyText }, nowS)];
        }
        return [];
    }

    /// <summary>Cerca del punto de espera, Ground pasa al piloto a Torre (si son la misma frecuencia, no hace falta).</summary>
    private IReadOnlyList<AtcTransmission> HandToTower(double nowS)
    {
        ResetGiveWay(); // la prioridad en un cruce del rodaje ya no hace falta
        var ground = ctx.Stations.For(AtcRole.Ground);
        var tower = ctx.Stations.For(AtcRole.Tower);
        if (ground is null || tower is null || tower.Khz == ground.Khz)
        {
            Phase = PilotPhase.WithTower;
            return [];
        }
        Phase = PilotPhase.TowerHandoffGiven;
        _expected = new ExpectedReadback(Callsign) { FrequencyKhz = tower.Khz };
        return [Instruction(ground, "atc.contact", new() { ["next_station"] = tower.Callsign, ["frequency"] = tower.FrequencyText }, nowS)];
    }

    /// <summary>"Ready for departure": la Torre decide con el estado de la pista (RF-26, las mismas reglas que con los IA).</summary>
    private IReadOnlyList<AtcTransmission> ReadyForDeparture(AtcStation station, double nowS)
    {
        // Fuera de guion (ADR-132): sin rodaje autorizado todavía, que hable con Ground (o con Autorizaciones).
        if (Clearance is not { } c || Phase < PilotPhase.Cleared)
            return ClearanceFirst(station, nowS);
        if (Phase < PilotPhase.Taxiing)
        {
            return ctx.Stations.For(AtcRole.Ground) is { } ground && ground.Khz != station.Khz
                ? [Say(station, "atc.contact", new() { ["next_station"] = ground.Callsign, ["frequency"] = ground.FrequencyText })]
                : IssueTaxi(station, nowS);
        }
        if (Phase >= PilotPhase.Airborne)
            return [];
        if (Phase >= PilotPhase.HoldShortGiven)
            return ReadyAgain(station, c, nowS);
        _expected = null; // si no colacionó el cambio a Torre pero ya llamó, el cambio se da por hecho
        Phase = PilotPhase.WithTower;
        SetTower(c, PilotTowerClearance.Ready); // entra en la fila de salida (por orden de llegada al punto de espera)
        // Todavía lejos del punto de espera: que siga y espere antes de la pista (vuelve a avisar al llegar).
        if (TaxiRoute is { } t && ctx.PilotPosition() is { } here && GeoMath.DistanceM(here, t.HoldShort) > 2 * TowerHandoffM)
            return [Say(station, "atc.hold_short", new() { ["runway"] = c.Runway })];
        return TowerDecision(station, c, nowS);
    }

    /// <summary>
    /// El que ocupa la pista ya está despegando, nadie más sale antes y el piloto está en el punto de espera: se puede
    /// alinear detrás (autorización condicional, CAP 413 §4.33: "behind the departing DC9, line up runway 26 behind").
    /// </summary>
    private static bool CanLineUpBehind(PilotRunwayStatus st) =>
        st is { AheadCount: 0, AtHoldingPoint: true, OccupantDeparting: true, OccupiedBy: not null, ArrivalAllowsLineUp: true };

    /// <summary>El tráfico de la autorización condicional ("A320"), o null si la de alinear no es condicional.</summary>
    private string? _lineUpBehind;

    /// <summary>Qué hacer mientras la Torre lo tiene esperando, con la razón si se sabe.</summary>
    private string HoldShortStep(string runway) => Runway() switch
    {
        { AtHoldingPoint: false } => Tr.F("Sigue hasta el punto de espera de la {0} y detente antes de la pista. La Torre te llamará.", runway),
        { AheadCount: 1 } => Tr.T("Espera en el punto de espera: sale 1 avión antes que tú. La Torre te llamará."),
        { AheadCount: > 1 } st => Tr.F("Espera en el punto de espera: salen {0} aviones antes que tú. La Torre te llamará.", st.AheadCount),
        { OccupiedBy: { } by } => Tr.F("Espera en el punto de espera: {0} está en la pista. La Torre te llamará.", by),
        { ArrivalAllowsLineUp: false } => Tr.T("Espera en el punto de espera: hay un avión en final para tu pista. La Torre te llamará cuando aterrice."),
        _ => Tr.T("Espera en el punto de espera: la Torre te llamará cuando puedas alinearte."),
    };

    private IReadOnlyList<AtcTransmission> TowerDecision(AtcStation tower, ClearanceData c, double nowS)
    {
        var status = Runway();
        _lineUpBehind = null;
        if (status is { } behind && CanLineUpBehind(behind))
        {
            _lineUpBehind = Fleet.AircraftNames.Spoken(behind.OccupantType, ctx.Language);
            Phase = PilotPhase.LineUpGiven;
            SetTower(c, PilotTowerClearance.LineUp);
            _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
            return [Instruction(tower, "atc.line_up_behind", new() { ["runway"] = c.Runway, ["traffic"] = _lineUpBehind }, nowS)];
        }
        if (status is { Free: false } st)
        {
            if (Phase == PilotPhase.HoldShortGiven)
                return [];
            Phase = PilotPhase.HoldShortGiven;
            _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
            // Con la razón, si se sabe: cuántos salen antes (CAP 413 §4.25) o quién está en la pista.
            if (st is { AtHoldingPoint: true, AheadCount: > 0 })
                return [Instruction(tower, "atc.hold_short_queue",
                    new() { ["runway"] = c.Runway, ["aircraft"] = AtcWords.Aircraft(st.AheadCount, ctx.Language) }, nowS)];
            if (st.OccupiedBy is not null)
                return [Instruction(tower, "atc.hold_short_traffic",
                    new() { ["runway"] = c.Runway, ["traffic"] = Fleet.AircraftNames.Spoken(st.OccupantType, ctx.Language) }, nowS)];
            return [Instruction(tower, "atc.hold_short", new() { ["runway"] = c.Runway }, nowS)];
        }
        // Libre de salidas, pero con una llegada en final de esa pista: espera fuera (ADR-129).
        if (status is { ArrivalAllowsLineUp: false })
        {
            if (Phase == PilotPhase.HoldShortGiven)
                return [];
            Phase = PilotPhase.HoldShortGiven;
            _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
            return [Instruction(tower, "atc.hold_short", new() { ["runway"] = c.Runway }, nowS)];
        }
        if (status is null or { TakeoffClear: true })
            return ClearForTakeoff(tower, c, nowS);
        Phase = PilotPhase.LineUpGiven;
        SetTower(c, PilotTowerClearance.LineUp);
        _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
        return [Instruction(tower, "atc.line_up_and_wait", new() { ["runway"] = c.Runway }, nowS)];
    }

    private IReadOnlyList<AtcTransmission> ClearForTakeoff(AtcStation tower, ClearanceData c, double nowS)
    {
        Phase = PilotPhase.TakeoffGiven;
        SetTower(c, PilotTowerClearance.Takeoff);
        _expected = new ExpectedReadback(Callsign) { Runway = c.Runway };
        return ctx.Wind?.Invoke() is { } wind
            ? [Instruction(tower, "atc.cleared_for_takeoff", new() { ["runway"] = c.Runway, ["wind"] = wind }, nowS)]
            : [Instruction(tower, "atc.cleared_for_takeoff_no_wind", new() { ["runway"] = c.Runway }, nowS)];
    }

    /// <summary>Primer contacto con Approach: lo identifica y confirma el nivel autorizado (sin nivel conocido, solo "identified").</summary>
    private IReadOnlyList<AtcTransmission> CheckIn(AtcStation station, double nowS)
    {
        Phase = PilotPhase.WithApproach;
        _enroute = station;
        if (Clearance is { LevelText: { } level, LevelFt: { } ft })
        {
            _expected = new ExpectedReadback(Callsign) { LevelFt = ft };
            // Con SID: "climb via SID to" (PANS-ATM Enmienda 7-A), que obliga a cumplir sus restricciones (ADR-124).
            return [Instruction(station, Clearance.Sid is { Length: > 0 } ? "atc.identified_via_sid" : "atc.identified",
                new() { ["level"] = level }, nowS)];
        }
        _expected = null;
        return [Say(station, "atc.identified_no_level", [])];
    }

    /// <summary>"Santiago Approach, LAN310, 3500 feet, ALBA7C" con la altitud actual redondeada a 100 ft.</summary>
    private string CheckInText(AtcStation dep)
    {
        var alt = ctx.AltitudeFt?.Invoke() is { } a ? RadioFormat.Level(Math.Round(a / 100) * 100, OriginTa, ctx.Language) : "";
        return Canonical("pilot.check_in_departure", new() { ["station"] = dep.Callsign, ["altitude"] = alt, ["sid"] = Clearance?.Sid ?? "" })
            .Replace(", , ", ", ").Replace(", ,", ",").TrimEnd(',', ' ');
    }

    /// <summary>A menos de esto (m) del eje de su pista de salida, el piloto ya está entrando o dentro de ella.</summary>
    public const double OnRunwayLateralM = 55;

    /// <summary>¿El piloto ya está entrando o dentro de su pista de salida? (cerca del eje, entre los dos extremos).</summary>
    private bool OnDepartureRunway(ClearanceData c)
    {
        if (ctx.PilotPosition() is not { } here || RunwayIndexOf(c.Runway) is not { } idx || idx >= ctx.Airport.Runways.Count)
            return false;
        var r = ctx.Airport.Runways[idx];
        var along = GeoMath.DistanceM(r.Primary.Position, here);
        var bearing = GeoMath.InitialBearingDeg(r.Primary.Position, here);
        var off = GeoMath.AngleDiffDeg(r.Primary.HeadingTrueDeg, bearing) * GeoMath.DegToRad;
        var forward = along * Math.Cos(off);
        return Math.Abs(along * Math.Sin(off)) < OnRunwayLateralM && forward > -60 && forward < r.LengthM + 60;
    }

    private PilotRunwayStatus? Runway() =>
        Clearance is { } c && RunwayIndexOf(c.Runway) is { } idx && ctx.RunwayStatus?.Invoke() is { } s && s.RunwayIndex == idx ? s : null;

    private void SetTower(ClearanceData c, PilotTowerClearance clearance)
    {
        if (RunwayIndexOf(c.Runway) is { } idx)
            ctx.SetTowerClearance?.Invoke(idx, clearance);
    }

    private int? RunwayIndexOf(string runway) => PilotTaxiRoute.RunwayIndex(ctx.Airport, runway)?.Index;

    /// <summary>El piloto está en una puerta (gate): sale con pushback.</summary>
    private bool AtGate() => NearestParking() is { Type: ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy or ParkingType.GateExtra };

    // ---------- Comunes ----------

    /// <summary>Si el piloto llamó a otra dependencia, esa le indica a cuál llamar; si es la correcta, responde.</summary>
    private IReadOnlyList<AtcTransmission> Route(AtcStation station, AtcRole role, Func<IReadOnlyList<AtcTransmission>> handle)
    {
        var right = ctx.Stations.For(role);
        if (right is null || right.Khz == station.Khz)
            return handle();
        return [Say(station, "atc.contact", new() { ["next_station"] = right.Callsign, ["frequency"] = right.FrequencyText })];
    }

    private AtcTransmission Instruction(AtcStation station, string id, Dictionary<string, string> fields, double nowS)
    {
        var t = Say(station, id, fields);
        _lastInstruction = t;
        _instructionAtS = nowS;
        _recalls = 0;
        InstructionNumber++;
        return t;
    }

    private AtcTransmission Repeat(AtcTransmission last, double nowS)
    {
        _instructionAtS = nowS;
        _recalls = 0;
        return last;
    }

    private AtcTransmission Say(AtcStation station, string id, Dictionary<string, string> fields)
    {
        fields.TryAdd("callsign", Callsign);
        fields.TryAdd("station", station.Callsign);
        return new AtcTransmission(station, ctx.Phrases.Say(id, fields));
    }

    private string Canonical(string id, Dictionary<string, string> fields)
    {
        fields.TryAdd("callsign", Callsign);
        return ctx.Phrases.Canonical(id, fields);
    }

    // En vuelo, el procedimiento de la colación es la STAR ("read back arrival", no "departure"; ADR-105).
    private string Element(ReadbackElement e) => ctx.Phrases.Canonical(
        e == ReadbackElement.Sid && Phase >= PilotPhase.EnRoute ? "element.star" : $"element.{e.ToString().ToLowerInvariant()}",
        new Dictionary<string, string>());

    /// <summary>
    /// "Negative, (lo correcto)": con los valores de la instrucción pendiente. En vuelo antes decía el nivel y la frecuencia
    /// de la autorización de salida (ADR-105): ahora el nivel, la frecuencia, la STAR o la velocidad que se dieron.
    /// </summary>
    private string Correction(ReadbackElement e)
    {
        var c = Clearance;
        var inFlight = Phase >= PilotPhase.EnRoute;
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        var level = _expected?.LevelFt is { } ft && inFlight ? LevelText(ft, Phase >= PilotPhase.Arrival || _descentGiven ? DestTa ?? OriginTa : OriginTa) : c?.LevelText ?? "";
        var next = inFlight && _expected?.FrequencyKhz is { } khz && (_handoffTo ?? StationOn(khz)) is { } to ? to : c?.Next;
        var fields = new Dictionary<string, string>
        {
            ["destination"] = ctx.Plan.Destination.Icao,
            ["sid"] = c?.Sid ?? "",
            ["star"] = _expected?.Sid ?? "",
            ["runway"] = _expected?.Runway ?? c?.Runway ?? "",
            ["level"] = level,
            ["squawk"] = c?.Squawk.ToString("0000") ?? "",
            ["next_station"] = next?.Callsign ?? "",
            ["frequency"] = next?.FrequencyText ?? "",
            ["speed"] = _expected?.SpeedKt?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["heading"] = _expected?.HeadingDeg?.ToString("000", CultureInfo.InvariantCulture) ?? "",
        };
        var id = e switch
        {
            ReadbackElement.Sid when inFlight => "correction.star",
            ReadbackElement.Level when inFlight && _expected?.LevelFt is { } target && target < alt - 300 => "correction.level_descend",
            _ => $"correction.{e.ToString().ToLowerInvariant()}",
        };
        return ctx.Phrases.Canonical(id, fields);
    }

    /// <summary>El puesto donde está el piloto, como se dice por radio ("14", "C4"), o null si no está en uno.</summary>
    private string? NearestStand()
    {
        if (NearestParking() is not { } best)
            return null;
        return StandNames.Spoken(best);
    }

    /// <summary>El puesto a menos de 60 m del piloto (sin contar los de vehículos), o null.</summary>
    private Parking? NearestParking()
    {
        if (ctx.PilotPosition() is not { } here)
            return null;
        Parking? best = null;
        var bestD = 60.0;
        foreach (var p in ctx.Airport.Parkings)
        {
            var d = GeoMath.DistanceM(p.Position, here);
            if (d < bestD && p.Type != ParkingType.Vehicle)
            {
                bestD = d;
                best = p;
            }
        }
        return best;
    }
}

/// <summary>Lo autorizado por Delivery.</summary>
public sealed record ClearanceData(string? Sid, string Runway, double? LevelFt, string? LevelText, int Squawk, AtcStation? Next);
