using System.Collections.Concurrent;
using System.Diagnostics;
using SimATC.Core.Atc;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.Core.Engine;

/// <summary>Estado de un avión en movimiento para la UI (radar).</summary>
/// <remarks>
/// Los vuelos en el aire (etapa 6) usan <see cref="ObjectId"/> = su identificador de radar, <see cref="Virtual"/> si no
/// están en el simulador, su altitud (MSL) y su destino.
/// </remarks>
public readonly record struct AgentSnapshot(uint ObjectId, string Callsign, GeoPoint Position, double HeadingDeg,
    double AglFt, double SpeedKt, DeparturePhase Phase, HoldKind Hold = HoldKind.None, string? HoldFor = null,
    double ThrottlePercent = 0, int RunwayIndex = -1, bool Airborne = false, bool Virtual = false, double AltitudeFt = 0,
    string? Destination = null, string? Origin = null, string? Type = null, double WingspanM = 0, int Engines = 2,
    RunwayHold RunwayHold = RunwayHold.None, double HoldLeftS = double.NaN);

/// <summary>
/// Tiempo de CPU del motor por cuadro del simulador (µs) y totales, para el panel Diagnóstico. El cuadro se divide en
/// lógica (ATC y aviones, en Core) y escrituras al simulador (poses, luces, motores, remolcador).
/// </summary>
public readonly record struct EngineStats(double LastFrameUs, double AvgFrameUs, double MaxFrameUs, long Frames, int Reroutes,
    double AvgLogicUs = 0, double AvgBridgeUs = 0, int SlowFrames = 0);

/// <summary>
/// Motor de tráfico (§5.2): se ejecuta en cada cuadro del simulador (<see cref="OnFrame"/>, en el hilo del puente),
/// avanza los aviones en ticks fijos y escribe sus poses interpoladas. Las órdenes de la UI entran por una cola.
/// El trabajo por cuadro no asigna memoria en Core (las escrituras a SimConnect sí, en el borde: ADR-001).
/// </summary>
public sealed class TrafficEngine
{
    /// <summary>Radio de inyección por defecto (NM): 60, pedido de Marco (ADR-089; antes 40).</summary>
    public const double DefaultInjectNm = 60;

    /// <summary>Margen (NM) entre inyectar y retirar, para que no entre y salga en el borde (§5.3).</summary>
    public const double RemoveMarginNm = 5;

    /// <summary>Un vuelo virtual a menos de esto del piloto se inyecta (§5.3); se cambia en Configuración (ADR-089).</summary>
    public static double InjectDistanceM { get; private set; } = DefaultInjectNm * UnitConversions.MetersPerNauticalMile;

    /// <summary>Más allá de esta distancia al piloto el avión se retira (§5.3, umbral de salida).</summary>
    public static double RemoveDistanceM { get; private set; } = (DefaultInjectNm + RemoveMarginNm) * UnitConversions.MetersPerNauticalMile;

    /// <summary>Radio de inyección (NM, de 20 a 100). Los límites de aviones inyectados siguen valiendo (FPS).</summary>
    public static void SetInjectRadiusNm(double nm)
    {
        var r = Math.Clamp(nm, 20, 100);
        InjectDistanceM = r * UnitConversions.MetersPerNauticalMile;
        RemoveDistanceM = (r + RemoveMarginNm) * UnitConversions.MetersPerNauticalMile;
    }

    /// <summary>Radio en ruta (ADR-089): los IA a menos de esto del piloto y sobre <see cref="EnrouteMinFt"/> hablan con su Centro.</summary>
    public const double EnrouteRadioM = 60 * UnitConversions.MetersPerNauticalMile;
    public const double EnrouteMinFt = 12_000;

    /// <summary>El Centro baja a los IA de a 10000 ft, no más abajo que esto (después los toma Aproximación). APROXIMADO.</summary>
    public const double EnrouteDescentFloorFt = 11_000;

    /// <summary>Nivel en ft como texto de la línea de radio (redondeado a 1000 ft; cadenas precalculadas, sin asignar).</summary>
    private static string LevelDetail(double ft)
    {
        var i = Math.Clamp((int)Math.Round(ft / 1000), 0, LevelTexts.Length - 1);
        return LevelTexts[i];
    }

    private static readonly string[] LevelTexts = [.. Enumerable.Range(0, 61).Select(k => (k * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Una llegada controlada a menos de esto (NM por su ruta) del umbral se carga en el simulador (ADR-090).</summary>
    public const double ActiveArrivalInjectNm = 20;

    /// <summary>Dentro de este radio nunca se retira un avión a la vista (§5.3).</summary>
    public const double VisualRadiusM = 15 * UnitConversions.MetersPerNauticalMile;

    /// <summary>Un vuelo virtual más lejos que esto del piloto se olvida (ventana lógica, RF-12d: 200 NM + margen).</summary>
    public double WindowRadiusM { get; set; } = 260 * UnitConversions.MetersPerNauticalMile;

    /// <summary>A esta altura sobre la pista la salida pasa a ser un vuelo en ruta (etapa 6).</summary>
    public const double AirborneHandoffAglFt = 1000;

    private readonly List<AirborneAgent> _air = [];
    private readonly ConcurrentQueue<AirborneAgent> _incomingAir = new();
    private readonly ConcurrentQueue<(AirborneAgent Agent, uint Id)> _spawned = new();
    private readonly ArrivalSequencer _sequencer = new();
    private readonly Func<int, AirborneAgent, bool> _runwayOccupied;
    private readonly Action<AirborneAgent, double> _clearArrival;
    private readonly Action<AirborneAgent, double> _assignSpeed;

    /// <summary>Approach asigna una velocidad (kt) o la libera (0); vale desde que se dice.</summary>
    private void AssignSpeed(AirborneAgent f, double kt)
    {
        f.AssignedSpeedKt = kt;
        Radio.QueueArrival(f.TrackId, f.Callsign, f.Landing?.Ident, ArrivalCall.Speed,
            kt.ToString("0", System.Globalization.CultureInfo.InvariantCulture), () => f.ClearancePending = false);
    }

    /// <summary>Radio del área terminal (Aproximación) alrededor del umbral: más lejos, los descensos son del Centro. APROXIMADO.</summary>
    public const double ApproachAreaM = 60 * UnitConversions.MetersPerNauticalMile;

    /// <summary>Approach le habla a una llegada: descenso a <paramref name="level"/> ft, o la aproximación con NaN.</summary>
    private void ClearArrival(AirborneAgent f, double level)
    {
        if (double.IsNaN(level))
        {
            f.ApproachCleared = true;
            f.ClearedAltitudeFt = double.NaN;
            Radio.QueueArrival(f.TrackId, f.Callsign, f.Landing?.Ident, ArrivalCall.ClearedApproach, f.Path.ApproachWord,
                () => f.ClearancePending = false);
            return;
        }
        // El avión empieza a bajar apenas oye la autorización (no espera a la radio atrasada: llegaba alto a la final).
        f.ClearedAltitudeFt = level;
        // Lejos del área terminal el descenso lo da el Centro, en otra frecuencia: no se oye en las del aeropuerto
        // (prueba de Marco: la Torre de SCEL hacía bajar a aviones a más de 90 NM).
        if (f.Landing is { } l && GeoMath.DistanceM(f.Position, l.Threshold) > ApproachAreaM)
        {
            f.ClearancePending = false;
            return;
        }
        Radio.QueueArrival(f.TrackId, f.Callsign, f.Landing?.Ident, ArrivalCall.Descend,
            level.ToString("0", System.Globalization.CultureInfo.InvariantCulture), () => f.ClearancePending = false);
    }
    private double _virtualTimer;
    private double _finalAltitudeTimer;

    // Elevación real de cada cabecera (índice de pista ×2 + extremo), aprendida de las salidas alineadas ahí.
    private readonly Dictionary<int, double> _thresholdFt = [];
    private double _lodTimer;
    private int _sequenceTick;
    private uint _nextTrackId = 0x8000_0000;

    /// <summary>Límites de inyectados (§5.3); los vuelos que no caben quedan virtuales.</summary>
    public Traffic.TrafficLimits Limits { get; set; } = Traffic.TrafficLimits.Default;

    /// <summary>
    /// Arma el vuelo en ruta de una salida que ya despegó (ruta, destino); null = sigue como antes y se retira lejos.
    /// Se llama en el hilo del motor.
    /// </summary>
    public Func<DepartureAgent, AirborneAgent?>? DepartureHandoff { get; set; }

    /// <summary>Arma el rodaje de una llegada que dejó la pista; null = se retira. En el hilo del motor.</summary>
    public Func<AirborneAgent, DepartureAgent?>? ArrivalHandoff { get; set; }

    /// <summary>
    /// Aviso de llegada (ADR-094): tramos del rodaje previsto de una llegada en final corta o en la pista (lo arma quien
    /// tiene el grafo, con <see cref="DepartureAgent.ArrivalRouteFor"/>). Las salidas no entran de frente a ellos.
    /// </summary>
    public Func<AirborneAgent, LandingRunway, ArrivalRoute?>? ArrivalPreview { get; set; }

    /// <summary>
    /// Desde esta distancia al umbral (m) se avisa el rodaje de la llegada (≈ 6,5 NM, unos 3 min antes de tocar): los
    /// pushbacks la miran desde ahí; a los que ruedan los frena solo el último minuto y cuarto (ADR-095).
    /// </summary>
    public const double ArrivalPreviewFromM = 12000;
    private readonly Dictionary<uint, int> _previewExit = [];

    /// <summary>Un IA que aterrizó sin rodaje se retira cuando el piloto está a más de esto (NM).</summary>
    public const double LingerRemoveNm = 15;

    // Aterrizados sin rodaje esperando que el piloto se aleje para retirarlos (pocos: lista simple).
    private readonly List<(uint Id, GeoPoint Position)> _lingering = [];

    // El piloto como llegada (ADR-076), puesto desde la UI (en caja: se lee entero desde el hilo del motor).
    private object? _pilotArrival;
    private volatile bool _pilotRunwayOccupied;
    private double _pilotRunwayClearInS;

    /// <summary>El piloto en la secuencia de llegadas del aeropuerto activo, o null (en tierra, lejos o sin llegada).</summary>
    public PilotArrival? PilotArrival
    {
        get => (PilotArrival?)Volatile.Read(ref _pilotArrival);
        set => Volatile.Write(ref _pilotArrival, value is { } v ? v : null);
    }

    // Cambios de llegada pedidos por el director (ADR-082): se aplican en el hilo del motor.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(AirborneAgent Agent, FlightPath? Path, LandingRunway? Landing, int Stand)> _reroutes = new();

    /// <summary>Pide cambiar la pista y la ruta de llegada de un vuelo en el aire (se aplica en el próximo cuadro).</summary>
    public void RequestReroute(AirborneAgent agent, FlightPath path, LandingRunway landing) => _reroutes.Enqueue((agent, path, landing, -2));

    /// <summary>ADR-085: el vuelo pasa a ser llegada controlada del aeropuerto activo (se aplica en el hilo del motor).</summary>
    public void RequestAdopt(AirborneAgent agent, FlightPath? path, LandingRunway landing, int stand) =>
        _reroutes.Enqueue((agent, path, landing, stand));

    /// <summary>ADR-085: la llegada a un aeropuerto que dejó de estar activo sigue sin ATC.</summary>
    public void RequestRelease(AirborneAgent agent) => _reroutes.Enqueue((agent, null, null, -3));

    /// <summary>Los vuelos en el aire de la última foto (referencias; leerlos desde otro hilo es aproximado, como en la UI).</summary>
    public List<AirborneAgent> AirborneNow()
    {
        var list = new List<AirborneAgent>();
        lock (_snapshotLock)
        {
            for (var i = 0; i < _snapshotCount; i++)
            {
                if (_snapshotRefs[i] is AirborneAgent f)
                    list.Add(f);
            }
        }
        return list;
    }

    /// <summary>Tasa de simulación del último cuadro (1 = tiempo real), para acelerar las voces (ADR-077).</summary>
    public double SimRate { get; private set; } = 1;

    /// <summary>IA que aterrizan antes que el piloto en su pista (última vuelta de la secuencia).</summary>
    public int PilotArrivalAhead => _sequencer.PilotAhead;

    /// <summary>
    /// El IA que aterriza justo antes que el piloto y cuánto lo lleva (NM por la trayectoria), o null (ADR-107: el ATC del
    /// piloto lo secuencia con velocidad o un viraje de 360°).
    /// </summary>
    public (string Type, double GapNm)? PilotLeaderGap =>
        _sequencer.PilotLeader is { } l && PilotArrival is { } p ? (l.TypeDesignator, p.RemainingM / Units.UnitConversions.MetersPerNauticalMile - l.RemainingNm) : null;

    /// <summary>La categoría de estela del que aterriza antes que el piloto, o null (ADR-124).</summary>
    public Fleet.WakeCategory? PilotLeaderWake => _sequencer.PilotLeader?.Wake;

    /// <summary>¿La pista del piloto está ocupada para su aterrizaje (salida alineada, llegada sin librar)?</summary>
    public bool PilotRunwayOccupied => _pilotRunwayOccupied;

    /// <summary>
    /// Segundos estimados para que la pista del piloto quede libre para él (ADR-120): quien la ocupa la deja, y las llegadas
    /// IA que van delante aterrizan y la libran. Infinito si no se sabe; 0 si está libre.
    /// </summary>
    public double PilotRunwayClearInS => Volatile.Read(ref _pilotRunwayClearInS);

    private double ComputePilotClearInS(PilotArrival p)
    {
        var worst = RunwayClearInS(p.RunwayIndex, null);
        for (var i = 0; i < _air.Count; i++)
        {
            var o = _air[i];
            if (o.ControlledArrival && o.Landing?.RunwayIndex == p.RunwayIndex && o.RemainingM < p.RemainingM
                && o.Phase is not (FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Finished or FlightPhase.GoAround
                    or FlightPhase.Holding))
                worst = Math.Max(worst, ArrivalSequencer.SecondsToThreshold(o) + ArrivalSequencer.RotS(o));
        }
        return worst;
    }

    /// <summary>Una llegada quedó apagada en su puesto (sigue en el simulador): pasa a los estacionados.</summary>
    public event Action<DepartureAgent>? ArrivalParked;

    /// <summary>Un vuelo en el aire terminó (otro destino, fuera de la ventana o pasó al rodaje) y se olvidó.</summary>
    public event Action<AirborneAgent>? FlightFinished;

    /// <summary>No se pudo tomar como llegada (en final o subiendo): el director libera el puesto y lo reintenta.</summary>
    public event Action<AirborneAgent, int>? AdoptFailed;

    /// <summary>Secuencia de llegadas (para la radio y el diagnóstico).</summary>
    public ArrivalSequencer Sequencer => _sequencer;

    /// <summary>Agrega un vuelo en el aire (virtual al principio), desde cualquier hilo.</summary>
    public void AddFlight(AirborneAgent flight) => _incomingAir.Enqueue(flight);

    /// <summary>Vuelos en el aire en el motor y cuántos de ellos están en el simulador (lectura aproximada).</summary>
    public int AirborneCount { get; private set; }
    public int InjectedAirborneCount { get; private set; }

    private readonly ISimBridge _bridge;
    private readonly Action<string> _log;
    private readonly SimClock _clock = new();
    private readonly ConcurrentQueue<DepartureAgent> _incoming = new();
    private readonly ConcurrentQueue<uint> _removeRequests = new();
    private readonly List<DepartureAgent> _agents = [];
    private readonly List<(LightFlags Lights, bool Engines)> _sent = [];
    private readonly List<double> _throttleSent = [];
    private readonly AirportControl _control = new();

    /// <summary>Cada cuánto se pide la altura real de un avión en tierra (s).</summary>
    public const double AltitudeSampleS = 1.0;

    private double _altitudeTimer;
    private readonly Lock _snapshotLock = new();
    // Foto para la UI: el motor la escribe sobre el mismo buffer (sin asignar en el tick) y la UI se lleva una copia.
    private AgentSnapshot[] _snapshot = new AgentSnapshot[16];
    // Las referencias de la misma foto (para la ficha y la ruta del avión elegido en la interfaz).
    private object?[] _snapshotRefs = new object?[16];
    private int _snapshotCount;
    private volatile bool _hasUser;
    private GeoPoint _userPosition;
    private volatile bool _userOnGround;
    private double _userHeading;

    /// <summary>Envergadura supuesta del avión del piloto si no se conoce su tipo (m). APROXIMADO.</summary>
    public const double DefaultUserWingspanM = 36;

    /// <summary>
    /// Envergadura del avión del piloto para separarse de él en tierra (m): la de su tipo en el catálogo (ADR-173: con un
    /// A330 se usaban los 36 m de un A320 y su cola entró en la cabina del IA que esperaba detrás).
    /// </summary>
    public double UserWingspanM { get; set; } = DefaultUserWingspanM;

    private double _userSpeedKt;
    private double _snapshotTimer;
    private volatile bool _clearRequested;
    private volatile bool _paused;
    private double _lastUs;
    private double _avgUs;
    private double _maxUs;
    private double _avgLogicUs;
    private double _avgBridgeUs;
    private long _logicEnd;
    private int _lastTicks;
    private int _slowFrames;

    /// <summary>Un cuadro que tarda más que esto se anota en el log con su desglose (µs).</summary>
    public const double SlowFrameUs = 10_000;
    private long _frames;
    private int _reroutesDone;
    private volatile int _reroutesTotal;

    public TrafficEngine(ISimBridge bridge, Action<string> log)
    {
        _bridge = bridge;
        _log = log;
        _control.Log = log;
        _sequencer.Log = log;
        _sequencer.Event += (f, e) =>
        {
            var call = e switch
            {
                ArrivalEvent.ClearedToLand => ArrivalCall.ClearedToLand,
                ArrivalEvent.GoAround => ArrivalCall.GoAround,
                _ => (ArrivalCall?)null,
            };
            if (call is { } c)
                Radio.QueueArrival(f.TrackId, f.Callsign, f.Landing?.Ident, c);
            // Quién ocupaba la pista (ADR-091: para saber por qué hubo motor y al aire).
            if (e == ArrivalEvent.GoAround && f.Landing is { } gl)
            {
                _occupantAgent = null;
                RunwayOccupied(gl.RunwayIndex, f);
                var why = _occupantAgent is { } oa
                    ? Tr.F("{0}: {1} a {2:0} kt, espera {3}/{4}", Tr.T(_occupantWhy), oa.Phase, oa.SpeedKt, oa.Hold, oa.HoldFor)
                    : Tr.T(_occupantWhy);
                _log(Tr.F("{0}: la pista {1} la ocupaba {2} ({3}).", f.Callsign, gl.Ident, _occupant ?? Tr.T("nadie"), why));
            }
            // Espera sobre un fijo con nombre (ADR-076): fijo, altitud y una demora estimada (10 min, APROXIMADO).
            if (e == ArrivalEvent.Holding && f.HoldFixName.Length > 0)
                Radio.QueueArrival(f.TrackId, f.Callsign, f.Landing?.Ident, ArrivalCall.Hold,
                    $"{f.HoldFixName}|{Math.Round(f.AltitudeFt / 100) * 100:0}|10");
        };
        _runwayOccupied = RunwayOccupied;
        _sequencer.ClearInS = RunwayClearInS;
        _sequencer.DeparturesWaiting = DeparturesWaiting;
        _exitBlocked = ExitBlocked;
        _clearArrival = ClearArrival;
        _assignSpeed = AssignSpeed;
        bridge.UserStateUpdated += s =>
        {
            _userPosition = s.Position;
            _userHeading = s.HeadingTrueDeg;
            _userSpeedKt = s.GroundSpeedKt;
            _userOnGround = s.OnGround;
            _hasUser = true;
        };
        // Llega en el hilo del puente, el mismo del motor.
        bridge.ObjectAltitude += (id, alt, agl) =>
        {
            for (var i = 0; i < _agents.Count; i++)
            {
                if (_agents[i].ObjectId == id)
                {
                    _agents[i].OnAltitudeSample(alt, agl);
                    return;
                }
            }
            for (var i = 0; i < _air.Count; i++)
            {
                if (_air[i].ObjectId == id)
                {
                    _air[i].OnAltitudeSample(alt, agl);
                    return;
                }
            }
        };
    }

    public SimClock Clock => _clock;

    /// <summary>Control del aeropuerto (Ground, Tower) y su capa de seguridad (ADR-021).</summary>
    public AirportControl Control => _control;

    /// <summary>Los aviones IA en la frecuencia (hito 4e-1). Sin frecuencias configuradas no hace nada.</summary>
    public AiRadio Radio { get; } = new();

    /// <summary>Seguridad en tierra (reservas, distancia, bloqueos): para tests y diagnóstico.</summary>
    public GroundCoordinator Coordinator => _control.Safety;

    public int ActiveCount => _agents.Count;

    /// <summary>
    /// Congela el tráfico IA (herramienta de diagnóstico): el reloj no avanza y los aviones quedan quietos donde están,
    /// con el simulador corriendo. El ATC tampoco decide mientras tanto.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Tiempo por cuadro y totales (lectura aproximada desde la UI: son valores sueltos, no una foto atómica).</summary>
    public EngineStats Stats => new(_lastUs, _avgUs, _maxUs, Interlocked.Read(ref _frames), _reroutesTotal, _avgLogicUs, _avgBridgeUs,
        _slowFrames);

    /// <summary>Reinicia el máximo del tiempo por cuadro.</summary>
    public void ResetMaxFrameTime() => _maxUs = 0;

    /// <summary>Retira un avión en movimiento (desde cualquier hilo; en el próximo cuadro). Herramienta de diagnóstico.</summary>
    public void RequestRemove(uint objectId) => _removeRequests.Enqueue(objectId);

    /// <summary>Se dispara cuando un avión termina (retirado del simulador).</summary>
    public event Action<uint>? AgentRemoved;

    /// <summary>Agrega una salida (desde cualquier hilo; entra en el próximo cuadro).</summary>
    public void Add(DepartureAgent agent) => _incoming.Enqueue(agent);

    /// <summary>Olvida todos los aviones en movimiento (el puente ya los borra con RemoveAll); en el próximo cuadro.</summary>
    public void Clear() => _clearRequested = true;

    /// <summary>
    /// El avión con ese identificador de radar en la última foto (<see cref="DepartureAgent"/> en tierra o
    /// <see cref="AirborneAgent"/> en el aire), para la ficha y su ruta. Leerlo desde la interfaz es aproximado (el motor
    /// lo sigue moviendo), suficiente para mostrar.
    /// </summary>
    public object? Find(uint id)
    {
        lock (_snapshotLock)
        {
            for (var i = 0; i < _snapshotCount; i++)
            {
                if (_snapshot[i].ObjectId == id)
                    return _snapshotRefs[i];
            }
        }
        return null;
    }

    /// <summary>Copia del estado para la UI (se actualiza ~5 veces por segundo).</summary>
    public AgentSnapshot[] Snapshot()
    {
        lock (_snapshotLock)
            return _snapshot.AsSpan(0, _snapshotCount).ToArray(); // la copia se asigna en el hilo de la UI
    }

    /// <summary>Un cuadro del simulador (o del Headless).</summary>
    public void OnFrame(FrameInfo frame)
    {
        var gen0 = GC.CollectionCount(0);
        var gen2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        _logicEnd = start;
        Frame(frame);
        var end = Stopwatch.GetTimestamp();
        var us = Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;
        var logic = Stopwatch.GetElapsedTime(start, _logicEnd).TotalMicroseconds;
        var bridge = us - logic;
        _lastUs = us;
        // Medias móviles (~50 cuadros).
        var first = _frames == 0;
        _avgUs = first ? us : _avgUs + (us - _avgUs) * 0.02;
        _avgLogicUs = first ? logic : _avgLogicUs + (logic - _avgLogicUs) * 0.02;
        _avgBridgeUs = first ? bridge : _avgBridgeUs + (bridge - _avgBridgeUs) * 0.02;
        if (us > _maxUs)
            _maxUs = us;
        Interlocked.Increment(ref _frames);
        if (us > SlowFrameUs)
        {
            // Solo en un cuadro lento (asigna el texto): dice si fue la lógica, SimConnect o un cuadro con muchos ticks
            // de recuperación (el simulador se trabó y el reloj se puso al día).
            _slowFrames++;
            // Si hubo recolección de memoria de .NET en este cuadro, la pausa pudo ser suya y no de SimConnect.
            var gcs = GC.CollectionCount(0) - gen0;
            var full = GC.CollectionCount(2) - gen2;
            var gc = gcs == 0 ? Tr.T("sin recolección de memoria") : full > 0 ? Tr.T("con recolección completa de memoria") : Tr.F("con {0} recolección(es) de memoria", gcs);
            _log(Tr.F("Cuadro lento: {0:0} µs (lógica {1:0} µs en {2} ticks, simulador {3:0} µs, {4} aviones, ", us, logic, _lastTicks, bridge, _agents.Count) +
                 Tr.F("{0:0} ms desde el anterior, {1}).", frame.RealSeconds * 1000, gc));
        }
    }

    private void Frame(FrameInfo frame)
    {
        if (_clearRequested)
        {
            _clearRequested = false;
            while (_incoming.TryDequeue(out _))
            {
            }
            _agents.Clear();
            _sent.Clear();
            _throttleSent.Clear();
            while (_incomingAir.TryDequeue(out _))
            {
            }
            _air.Clear();
            foreach (var l in _lingering)
                _bridge.Remove(l.Id);
            _lingering.Clear();
            _control.Clear();
            Radio.Clear();
            PublishSnapshot(0);
        }

        while (_incoming.TryDequeue(out var a))
        {
            _agents.Add(a);
            _sent.Add((LightFlags.None, false));
            _throttleSent.Add(-1);
            _log(Tr.F("{0}: salida iniciada.", a.Callsign));
        }

        while (_incomingAir.TryDequeue(out var f))
        {
            if (f.TrackId == 0)
                f.TrackId = f.ObjectId != 0 ? f.ObjectId : _nextTrackId++;
            f.ExitBlocked = _exitBlocked;
            _air.Add(f);
            _log(Tr.F("{0}: vuelo {1}→{2} en el motor ({3}, {4:F0} ft).", f.Callsign, f.Origin, f.Destination, f.Phase, f.AltitudeFt));
        }
        while (_spawned.TryDequeue(out var sp))
        {
            sp.Agent.SpawnPending = false;
            if (sp.Id == 0)
                continue;
            if (!_air.Contains(sp.Agent) || sp.Agent.Phase is FlightPhase.Finished or FlightPhase.Landed)
            {
                _bridge.Remove(sp.Id);
                continue;
            }
            sp.Agent.ObjectId = sp.Id;
            sp.Agent.SentLights = LightFlags.None;
            sp.Agent.SentEngines = false;
            sp.Agent.SentThrottle = -1;
        }

        while (_removeRequests.TryDequeue(out var removeId))
        {
            for (var i = 0; i < _agents.Count; i++)
            {
                if (_agents[i].ObjectId == removeId)
                {
                    _log(Tr.F("{0}: quitado a mano.", _agents[i].Callsign));
                    _agents[i].MarkDeparted();
                }
            }
        }

        var paused = frame.Paused || _paused;

        SimRate = frame.SimRate;
        while (_reroutes.TryDequeue(out var rr))
        {
            if (!_air.Contains(rr.Agent))
                continue;
            if (rr.Stand == -3)
            {
                rr.Agent.Release();
                _log(Tr.F("{0}: su destino ya no es el aeropuerto activo; sigue sin ATC.", rr.Agent.Callsign));
            }
            else if (rr.Landing is null)
            {
                continue;
            }
            else if (rr.Path is null)
            {
                // Cerca o en final: sigue su ruta a la misma pista (ADR-088).
                if (rr.Stand >= 0 && rr.Agent.AdoptOnFinal(rr.Landing, rr.Stand))
                    _log(Tr.F("{0}: pasa a llegada controlada en final, pista {1}.", rr.Agent.Callsign, rr.Landing.Ident));
                else if (rr.Stand >= 0)
                    AdoptFailed?.Invoke(rr.Agent, rr.Stand);
            }
            else
            {
                var oldIdent = rr.Agent.Landing?.Ident;
                var path = rr.Path!; // no es null: lo descartó la rama anterior
                if (rr.Stand == -2 ? rr.Agent.Reroute(path, rr.Landing) : rr.Agent.Adopt(path, rr.Landing, rr.Stand))
                {
                    // Cambio de pista (ADR-097): Approach se lo avisa por radio ("expect ILS approach runway 32L").
                    if (rr.Stand == -2 && rr.Agent.ControlledArrival && path.ApproachWord is { Length: > 0 } word
                        && !string.Equals(oldIdent, rr.Landing.Ident, StringComparison.OrdinalIgnoreCase))
                        Radio.QueueArrival(rr.Agent.TrackId, rr.Agent.Callsign, rr.Landing.Ident, ArrivalCall.ExpectApproach, word);
                    _log(rr.Stand == -2 ? Tr.F("{0}: nueva llegada a la pista {1}.", rr.Agent.Callsign, rr.Landing.Ident)
                        : Tr.F("{0}: pasa a llegada controlada, pista {1}.", rr.Agent.Callsign, rr.Landing.Ident));
                }
                else if (rr.Stand >= 0)
                {
                    AdoptFailed?.Invoke(rr.Agent, rr.Stand);
                }
            }
        }
        var ticks = _clock.Advance(frame.RealSeconds, frame.SimRate, paused);
        _lastTicks = ticks;
        GroundObstacle? user = _hasUser && _userOnGround ? new GroundObstacle(_userPosition, _userHeading, UserWingspanM, _userSpeedKt) : null;
        _control.Tower.PilotPosition = _hasUser ? _userPosition : null;
        for (var t = 0; t < ticks; t++)
        {
            // Las llegadas que vienen: la Torre no deja entrar a la pista con una encima. También en el traspaso a tierra
            // (Landed, unos segundos): en Sídney una salida cruzó la pista ahí, delante de la llegada que salía (ADR-094).
            _control.Tower.ClearArrivals();
            for (var i = 0; i < _air.Count; i++)
            {
                if (!_air[i].ControlledArrival || _air[i].Landing is not { } l)
                    continue;
                if (_air[i].Phase is FlightPhase.Approach or FlightPhase.Final)
                {
                    _control.Tower.SetNextArrival(l.RunwayIndex, ArrivalSequencer.SecondsToThreshold(_air[i]));
                }
                else if (_air[i].Phase is FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed)
                {
                    _control.Tower.SetArrival(l.RunwayIndex, 0);
                    _control.Tower.SetLanding(l.RunwayIndex, _air[i].Position, _air[i].TypeDesignator);
                }
            }
            var pilotArrival = PilotArrival;
            if (pilotArrival is { } pa)
            {
                // La Torre no deja entrar a la pista con el piloto encima, como con cualquier llegada.
                _control.Tower.SetArrival(pa.RunwayIndex, pa.RemainingM / Math.Max(1, pa.SpeedKt * 0.514444));
            }
            if (_lingering.Count > 0 && _sequenceTick % 50 == 0)
            {
                for (var i = _lingering.Count - 1; i >= 0; i--)
                {
                    if (!_hasUser || GeoMath.DistanceM(_lingering[i].Position, _userPosition) > LingerRemoveNm * UnitConversions.MetersPerNauticalMile)
                    {
                        _bridge.Remove(_lingering[i].Id);
                        _lingering.RemoveAt(i);
                    }
                }
            }
            if (++_sequenceTick % 5 == 0)
            {
                _sequencer.Update(_air, _runwayOccupied, pilotArrival);
                _pilotRunwayOccupied = pilotArrival is { } pr && RunwayOccupied(pr.RunwayIndex, null);
                Volatile.Write(ref _pilotRunwayClearInS, pilotArrival is { } pc ? ComputePilotClearInS(pc) : 0);
            }
            if (_sequenceTick % 20 == 0)
            {
                ArrivalSequencer.UpdateClearances(_air, _clearArrival, _assignSpeed, _clock.SimTime);
                EnrouteSpacing.Update(_air);
            }
            // Primero el control (autorizaciones y límites) y la radio (que las dice), después cada avión avanza respetándolo.
            _control.Update(_agents, user, SimClock.TickSeconds);
            Radio.Update(_agents, _clock.SimTime - (ticks - 1 - t) * SimClock.TickSeconds, user);
            for (var i = 0; i < _agents.Count; i++)
            {
                try
                {
                    _agents[i].Step(SimClock.TickSeconds);
                }
                catch (Exception ex)
                {
                    // Una excepción en un avión no detiene el motor (RNF-06): se retira ese avión.
                    _log(Tr.F("{0}: error en el tick, se retira ({1}).", _agents[i].Callsign, ex.Message));
                    _agents[i].MarkDeparted();
                }
            }
            // Los vuelos en el simulador avanzan en cada tick; los virtuales una vez por segundo (§5.3).
            for (var i = 0; i < _air.Count; i++)
            {
                if (_air[i].Injected || _air[i].SpawnPending)
                    StepFlight(_air[i], SimClock.TickSeconds);
            }
        }
        if (!paused)
        {
            _virtualTimer += ticks * SimClock.TickSeconds;
            if (_virtualTimer >= 1)
            {
                for (var i = 0; i < _air.Count; i++)
                {
                    if (!_air[i].Injected && !_air[i].SpawnPending)
                        StepFlight(_air[i], _virtualTimer);
                }
                _virtualTimer = 0;
            }
        }

        _logicEnd = Stopwatch.GetTimestamp();
        var alpha = _clock.Alpha;
        for (var i = _agents.Count - 1; i >= 0; i--)
        {
            var a = _agents[i];
            // Salida en el aire: sigue como vuelo en ruta con el mismo objeto (etapa 6).
            if (a.Phase == DeparturePhase.Climb && a.AglFt >= AirborneHandoffAglFt && DepartureHandoff?.Invoke(a) is { } flight)
            {
                a.MarkDeparted(); // la Torre ya no lo espera para separar la siguiente salida
                _control.Remove(a);
                _reroutesDone += a.Reroutes;
                flight.TrackId = a.ObjectId;
                flight.SentLights = _sent[i].Lights;
                flight.SentEngines = _sent[i].Engines;
                flight.SentThrottle = _throttleSent[i];
                _air.Add(flight);
                _agents.RemoveAt(i);
                _sent.RemoveAt(i);
                _throttleSent.RemoveAt(i);
                _log(Tr.F("{0}: en ruta a {1} ({2} puntos, crucero {3:F0} ft).", a.Callsign, flight.Destination, flight.Path.Count, flight.CruiseAltFt));
                continue;
            }
            // Llegada apagada en su puesto: queda como estacionado (el objeto sigue).
            if (a.Parked)
            {
                _control.Remove(a);
                _agents.RemoveAt(i);
                _sent.RemoveAt(i);
                _throttleSent.RemoveAt(i);
                _log(Tr.F("{0}: estacionado en su puesto.", a.Callsign));
                ArrivalParked?.Invoke(a);
                continue;
            }
            if (a.Phase == DeparturePhase.Departed || (a.Phase == DeparturePhase.Climb && FarFromUser(a)))
            {
                _bridge.Remove(a.ObjectId);
                _log(Tr.F("{0}: retirado ({1}, {2:F0} ft AGL).", a.Callsign, (a.Phase == DeparturePhase.Departed ? "terminado" : Tr.T("a más de 45 NM")), a.AglFt));
                _control.Remove(a);
                _reroutesDone += a.Reroutes;
                _agents.RemoveAt(i);
                _sent.RemoveAt(i);
                _throttleSent.RemoveAt(i);
                AgentRemoved?.Invoke(a.ObjectId);
                continue;
            }
            if (frame.Paused)
                continue;

            var pose = a.Pose(alpha);
            // Con el tráfico congelado se sigue escribiendo la pose (si no, el simulador lo movería), pero quieto.
            if (_paused)
                pose = pose with { AirspeedKt = 0, LongitudinalSpeedKt = 0, VerticalSpeedFpm = 0 };
            _bridge.SetPose(a.ObjectId, pose);
            // Remolcador (V-15): se mueve con el avión; al quedar estacionado ya no se manda. Se retira con el avión.
            if (a.TugUpdate(alpha, out var tug))
                _bridge.SetTug(a.ObjectId, tug);

            var (lights, engines) = _sent[i];
            if (lights != a.Lights)
            {
                _bridge.SetLights(a.ObjectId, a.Lights);
                lights = a.Lights;
            }
            if (engines != a.EnginesRunning)
            {
                _bridge.SetEngines(a.ObjectId, a.EnginesRunning, a.EngineCount);
                engines = a.EnginesRunning;
            }
            // Gases: solo cuando cambian al menos 1 % (la palanca ya viene suavizada).
            if (Math.Abs(_throttleSent[i] - a.ThrottlePercent) >= 1 || (a.ThrottlePercent == 0 && _throttleSent[i] != 0))
            {
                _bridge.SetThrottle(a.ObjectId, a.ThrottlePercent, a.EngineCount);
                _throttleSent[i] = a.ThrottlePercent;
            }
            _sent[i] = (lights, engines);
        }

        UpdateFlights(frame, alpha);

        // Llegadas en final: la altura real del suelo 4 veces por segundo (para la pista real y la enderezada).
        _finalAltitudeTimer += frame.RealSeconds;
        if (_finalAltitudeTimer >= 0.25 && !paused)
        {
            _finalAltitudeTimer = 0;
            for (var i = 0; i < _air.Count; i++)
            {
                if (_air[i].Injected && _air[i].Phase is FlightPhase.Final or FlightPhase.Flare && _air[i].RemainingNm < 1.5)
                    _bridge.RequestAltitude(_air[i].ObjectId);
            }
        }
        // Las salidas alineadas enseñan la elevación real de su cabecera, que es el umbral de las llegadas.
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a.Phase == DeparturePhase.LinedUp && !a.Inbound)
                _thresholdFt[a.RunwayIndex * 2 + (a.PrimaryEnd ? 0 : 1)] = a.GroundElevationFt;
        }
        for (var i = 0; i < _air.Count; i++)
        {
            if (_air[i].ControlledArrival && _air[i].Landing is { } l && double.IsNaN(_air[i].LearnedRunwayFt)
                && _thresholdFt.TryGetValue(l.RunwayIndex * 2 + (l.PrimaryEnd ? 0 : 1), out var ft) && Math.Abs(ft - l.ElevationFt) < 150)
                _air[i].LearnedRunwayFt = ft;
        }

        _altitudeTimer += frame.RealSeconds;
        if (_altitudeTimer >= AltitudeSampleS && !paused)
        {
            _altitudeTimer = 0;
            for (var i = 0; i < _agents.Count; i++)
            {
                if (_agents[i].OnGround)
                    _bridge.RequestAltitude(_agents[i].ObjectId);
            }
        }

        _snapshotTimer += frame.RealSeconds;
        if (_snapshotTimer >= 0.2)
        {
            _snapshotTimer = 0;
            PublishSnapshot(alpha);
        }
    }

    private void StepFlight(AirborneAgent f, double dt)
    {
        try
        {
            f.Step(dt);
        }
        catch (Exception ex)
        {
            _log(Tr.F("{0}: error en el tick, se retira ({1}).", f.Callsign, ex.Message));
            f.MarkFinished();
        }
    }

    /// <summary>Vuelos en el aire: relevos, terminados, inyectar o retirar por distancia (1 Hz) y escribir sus poses.</summary>
    private void UpdateFlights(FrameInfo frame, double alpha)
    {
        _lodTimer += frame.RealSeconds;
        var lod = _lodTimer >= 1;
        if (lod)
            _lodTimer = 0;
        var injectedAir = 0;
        var onGround = _agents.Count;
        GroundMovingCount = onGround;
        for (var i = 0; i < _air.Count; i++)
        {
            if (_air[i].Injected || _air[i].SpawnPending)
                injectedAir++;
        }
        // Cupo lleno: entran primero las llegadas al aeropuerto activo y los más cercanos (ADR-110).
        if (lod && _hasUser && injectedAir >= Limits.MaxAir)
            SwapForPriority(ref injectedAir);

        for (var i = _air.Count - 1; i >= 0; i--)
        {
            var f = _air[i];
            if (f.Phase == FlightPhase.Landed)
            {
                DepartureAgent? ground = null;
                try
                {
                    ground = f.Injected ? ArrivalHandoff?.Invoke(f) : null;
                }
                catch (Exception ex)
                {
                    _log(Tr.F("{0}: no se pudo armar el rodaje ({1}).", f.Callsign, ex.Message));
                }
                if (ground is not null)
                {
                    _agents.Add(ground);
                    _sent.Add((f.SentLights, f.SentEngines));
                    _throttleSent.Add(f.SentThrottle);
                    _log(Tr.F("{0}: dejó la pista, rueda a su puesto.", f.Callsign));
                }
                else if (f.Injected)
                {
                    // Sin rodaje (otro aeropuerto): cerca del piloto queda donde terminó y se retira al alejarse él
                    // (ADR-076, RNF: ningún avión desaparece a la vista).
                    if (_hasUser && GeoMath.DistanceM(f.Position, _userPosition) < LingerRemoveNm * UnitConversions.MetersPerNauticalMile)
                        _lingering.Add((f.ObjectId, f.Position));
                    else
                        _bridge.Remove(f.ObjectId);
                }
                _air.RemoveAt(i);
                _previewExit.Remove(f.ObjectId);
                FlightFinished?.Invoke(f);
                continue;
            }
            UpdateArrivalPreview(f);
            var distance = _hasUser ? GeoMath.DistanceM(f.Position, _userPosition) : double.MaxValue;
            // En ruta (ADR-089): los IA en crucero cerca del piloto hablan con su Centro, como en el ATC del simulador: primer
            // contacto al entrar al radio y el descenso cuando lo empiezan. Las llegadas controladas tienen su propia radio.
            if (Radio.EnrouteKhz > 0 && !f.ControlledArrival && distance < EnrouteRadioM && f.AltitudeFt > EnrouteMinFt)
            {
                if (f.EnrouteStage == 0 && f.Phase is FlightPhase.Cruise or FlightPhase.Climb or FlightPhase.Descent)
                {
                    f.EnrouteStage = 1;
                    Radio.QueueEnroute(f.TrackId, f.Callsign, RadioLineKind.EnrouteCheckIn, RadioLineKind.EnrouteIdentified,
                        LevelDetail(f.AltitudeFt));
                }
                else if (f.EnrouteStage == 1 && f.Phase == FlightPhase.Descent && f.VerticalSpeedFpm < -300)
                {
                    f.EnrouteStage = 2;
                    Radio.QueueEnroute(f.TrackId, f.Callsign, RadioLineKind.Descend, RadioLineKind.ReadbackDescend,
                        LevelDetail(Math.Max(EnrouteDescentFloorFt, f.AltitudeFt - 10_000)));
                }
            }
            // En final, Approach la pasa a la Torre y avisa (una vez por aproximación).
            if (f.ControlledArrival && f.Landing is { } fl && f.Phase == FlightPhase.Final && f.RadioStage == 0)
            {
                f.RadioStage = 1;
                Radio.QueueArrival(f.TrackId, f.Callsign, fl.Ident, ArrivalCall.HandoffToTower);
            }
            else if (f.Phase == FlightPhase.GoAround)
            {
                f.RadioStage = 0;
            }
            if (f.Phase == FlightPhase.Finished && !(f.Injected && distance < VisualRadiusM))
            {
                if (f.Injected)
                    _bridge.Remove(f.ObjectId);
                _log(Tr.F("{0}: vuelo terminado ({1}).", f.Callsign, f.Destination));
                _air.RemoveAt(i);
                _previewExit.Remove(f.ObjectId);
                Coordinator.ClearArrivalPreview(f.ObjectId);
                FlightFinished?.Invoke(f);
                continue;
            }

            if (lod && _hasUser)
            {
                // Fuera de la ventana lógica: se olvida (RF-12d). Nunca uno inyectado.
                if (!f.Injected && !f.SpawnPending && distance > WindowRadiusM)
                    f.MarkFinished();
                // Las llegadas al aeropuerto activo se cargan cerca de la pista aunque el piloto esté lejos (ADR-090: JAT233
                // aterrizó virtual en SCIE, no pudo rodar y desapareció al final de la pista); los estacionados ya lo están.
                var activeArrival = f.ControlledArrival && f.RemainingNm < ActiveArrivalInjectNm;
                if (f.Injected && distance > RemoveDistanceM && !activeArrival)
                {
                    // Lejos: sigue virtual (§5.3, histéresis 40/45 NM).
                    _bridge.Remove(f.ObjectId);
                    f.ObjectId = 0;
                    injectedAir--;
                    _log(Tr.F("{0}: sale de la vista, sigue virtual.", f.Callsign));
                }
                else if (!f.Injected && !f.SpawnPending && (distance <= InjectDistanceM || activeArrival) && f.Model is { } model
                         && f.Phase is not FlightPhase.Finished
                         && injectedAir < Limits.MaxAir && injectedAir + onGround < Limits.MaxTotal)
                {
                    injectedAir++;
                    Spawn(f, model.Title);
                }
            }

            if (!f.Injected || frame.Paused)
                continue;
            var pose = f.Pose(alpha);
            if (_paused)
                pose = pose with { AirspeedKt = 0, LongitudinalSpeedKt = 0, VerticalSpeedFpm = 0 };
            _bridge.SetPose(f.ObjectId, pose);
            if (f.SentLights != f.Lights)
            {
                _bridge.SetLights(f.ObjectId, f.Lights);
                f.SentLights = f.Lights;
            }
            if (!f.SentEngines)
            {
                _bridge.SetEngines(f.ObjectId, true, f.EngineCount);
                f.SentEngines = true;
            }
            if (Math.Abs(f.SentThrottle - f.ThrottlePercent) >= 1)
            {
                _bridge.SetThrottle(f.ObjectId, f.ThrottlePercent, f.EngineCount);
                f.SentThrottle = f.ThrottlePercent;
            }
        }
        AirborneCount = _air.Count;
        InjectedAirborneCount = injectedAir;
    }

    /// <summary>Aviones moviéndose en tierra (rodando a la pista, alineados o rodando al puesto); el director no lanza más salidas si llega al máximo.</summary>
    public int GroundMovingCount { get; private set; }

    /// <summary>Más cerca que esto del piloto, un avión cargado nunca se descarga para dar su cupo a otro (no desaparece a la vista).</summary>
    public const double SwapProtectM = 40 * UnitConversions.MetersPerNauticalMile;

    /// <summary>El que espera tiene que estar al menos esto más cerca que el que se descarga (evita ir y venir).</summary>
    public const double SwapMarginM = 10 * UnitConversions.MetersPerNauticalMile;

    /// <summary>
    /// Con el cupo en el aire lleno (ADR-110): si uno que espera dentro del radio es una llegada al aeropuerto activo o está
    /// bastante más cerca que el cargado más lejano, ese lejano vuelve a ser virtual (solo si está a más de
    /// <see cref="SwapProtectM"/> del piloto: nadie lo ve desaparecer) y el que espera se carga. Uno por segundo.
    /// </summary>
    private void SwapForPriority(ref int injectedAir)
    {
        AirborneAgent? best = null;
        var bestScore = double.MaxValue;
        var bestD = double.MaxValue;
        var bestActive = false;
        AirborneAgent? worst = null;
        var worstD = SwapProtectM;
        for (var i = 0; i < _air.Count; i++)
        {
            var f = _air[i];
            var d = GeoMath.DistanceM(f.Position, _userPosition);
            var activeArrival = f.ControlledArrival && f.RemainingNm < ActiveArrivalInjectNm;
            if (!f.Injected && !f.SpawnPending)
            {
                if (f.Model is null || f.Phase is FlightPhase.Finished || d > InjectDistanceM && !activeArrival)
                    continue;
                // Primero las llegadas al aeropuerto activo, después el más cercano.
                var score = (activeArrival ? 0 : 1e9) + d;
                if (score < bestScore)
                    (best, bestScore, bestD, bestActive) = (f, score, d, activeArrival);
            }
            else if (f.Injected && !activeArrival && d > worstD)
            {
                (worst, worstD) = (f, d);
            }
        }
        if (best is null || worst is null || best.Model is not { } model)
            return;
        if (!bestActive && bestD > worstD - SwapMarginM)
            return;
        _bridge.Remove(worst.ObjectId);
        worst.ObjectId = 0;
        _log(Tr.F("{0}: vuelve a virtual (a {1:0} NM) para dar su cupo a {2}", worst.Callsign, worstD / UnitConversions.MetersPerNauticalMile, best.Callsign)
             + (bestActive ? Tr.T(" (llegada al aeropuerto).") : Tr.F(" (a {0:0} NM).", bestD / UnitConversions.MetersPerNauticalMile)));
        Spawn(best, model.Title);
    }

    /// <summary>Pide crearlo en el simulador (asincrónico: el ObjectId llega en un cuadro siguiente).</summary>
    private void Spawn(AirborneAgent f, string title)
    {
        f.SpawnPending = true;
        var p = f.Pose(1);
        var request = new AirborneSpawnRequest(title, f.Callsign, p.Position, p.AltitudeFt, p.HeadingTrueDeg, f.SpeedKt, f.EngineCount);
        try
        {
            _bridge.SpawnAirborneAsync(request, CancellationToken.None).ContinueWith(t =>
            {
                _spawned.Enqueue((f, t.IsCompletedSuccessfully ? t.Result : 0));
                if (!t.IsCompletedSuccessfully)
                    _log(Tr.F("{0}: no se pudo crear en vuelo ({1}).", f.Callsign, t.Exception?.GetBaseException().Message));
            }, TaskScheduler.Default);
            _log(Tr.F("{0}: entra a la vista ({1:F0} ft, {2}).", f.Callsign, f.AltitudeFt, f.Phase));
        }
        catch (Exception ex)
        {
            f.SpawnPending = false;
            _log(Tr.F("{0}: no se pudo crear en vuelo ({1}).", f.Callsign, ex.Message));
        }
    }

    /// <summary>Aviso de llegada (ADR-094): una vez por salida de pista prevista, en final corta o en la pista.</summary>
    private void UpdateArrivalPreview(AirborneAgent f)
    {
        if (ArrivalPreview is not { } preview || !f.Injected)
            return;
        if (f.Landing is { } l && (f.Phase is FlightPhase.Flare or FlightPhase.Rollout
                || f.Phase is FlightPhase.Approach or FlightPhase.Final && f.RemainingM < ArrivalPreviewFromM))
        {
            if (_previewExit.TryGetValue(f.ObjectId, out var exit) && exit == l.ExitEntry)
                return;
            _previewExit[f.ObjectId] = l.ExitEntry;
            ArrivalRoute? keys = null;
            try
            {
                keys = preview(f, l);
            }
            catch (Exception ex)
            {
                _log(Tr.F("{0}: no se pudo prever su rodaje ({1}).", f.Callsign, ex.Message));
            }
            if (keys is { Keys.Length: > 0 })
                Coordinator.SetArrivalPreview(f.ObjectId, keys, ArrivalSequencer.EtaS(f));
            else
                Coordinator.ClearArrivalPreview(f.ObjectId);
        }
        else if (_previewExit.Remove(f.ObjectId))
        {
            Coordinator.ClearArrivalPreview(f.ObjectId);
        }
    }

    /// <summary>¿Alguien ocupa la pista para una llegada? Salidas en la pista, otra llegada rodando en ella o el piloto.</summary>
    /// <summary>
    /// ¿Hay alguien de frente o detenido en los primeros 250 m de la calle de esa salida? (ADR-092). Se consulta una vez por
    /// salida durante el frenado.
    /// </summary>
    private readonly Func<AirborneAgent, LandingRunway, RunwayExit, bool> _exitBlocked;

    private bool ExitBlocked(AirborneAgent f, LandingRunway l, RunwayExit e)
    {
        // La ruta que seguiría desde esa salida (ADR-096, Montreal): si en su comienzo alguien rueda de frente (tiene tomado
        // o reservado el tramo en contra), la llegada entraría nariz con nariz sin poder esperar fuera de la pista.
        if (ArrivalPreview is { } preview && preview(f, l.WithExit(e)) is { } route && Coordinator.ExitRouteOpposed(route))
            return true;
        var node = GeoMath.Destination(l.Threshold, l.HeadingDeg, e.NodeAlongM);
        var exitBearing = GeoMath.NormalizeDeg(l.HeadingDeg + e.Side * e.AngleDeg);
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (!a.OnGround)
                continue;
            var pos = a.Projection.ToGeo(a.Position);
            var d = GeoMath.DistanceM(node, pos);
            if (d > 260)
                continue;
            var off = GeoMath.AngleDiffDeg(exitBearing, GeoMath.InitialBearingDeg(node, pos)) * Math.PI / 180;
            var along = d * Math.Cos(off);
            if (along < 20 || along > 250 || Math.Abs(d * Math.Sin(off)) > 35)
                continue;
            // De frente, rodando o detenido (ADR-095, Sídney: una salida esperaba para cruzar la pista en G mirando a la
            // pista, la llegada salió por G y quedaron nariz con nariz).
            if (Math.Abs(GeoMath.AngleDiffDeg(a.HeadingDeg, exitBearing + 180)) < 60)
                return true;
        }
        // Gestor de rodaje (ADR-094, Headless de Gatwick): una salida que rueda hacia la pista por esta misma calle (su
        // entrada a la pista o un cruce) en los próximos 1500 m. La llegada que sale por ahí quedaría nariz con nariz con
        // ella en una calle de un carril: mejor la siguiente salida.
        var onTaxiway = GeoMath.Destination(node, exitBearing, ExitLookAlongM);
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a.Inbound || !a.OnGround || !a.OnTaxiPath || a.Phase is not (DeparturePhase.Taxi or DeparturePhase.Pushback
                    or DeparturePhase.PushbackComplete))
                continue;
            var path = a.TaxiPath;
            var p = a.Projection.ToLocal(onTaxiway);
            var s = path.ProjectWithin(p, a.TaxiProgress, Math.Min(path.Length, a.TaxiProgress + ExitLookAheadM), out var lateral);
            if (lateral < 40 && s > a.TaxiProgress
                && Math.Abs(GeoMath.AngleDiffDeg(path.HeadingAt(s), exitBearing + 180)) < 60)
                return true;
        }
        return false;
    }

    /// <summary>Punto de la calle de salida que se mira (m desde el eje) y cuánto de la ruta de cada salida (m).</summary>
    private const double ExitLookAlongM = 80;
    private const double ExitLookAheadM = 1500;

    /// <summary>
    /// Segundos estimados para que la pista quede libre para <paramref name="arrival"/> (ADR-120): lo máximo entre quienes la
    /// ocupan. Una llegada saliendo o frenando tiene tiempo estimado; una salida ya en carrera a más de 80 kt, unos segundos;
    /// alineados, detenidos, cruces y el piloto: no se sabe (infinito).
    /// </summary>
    private double RunwayClearInS(int runwayIndex, AirborneAgent? arrival)
    {
        var worst = 0.0;
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a.RunwayIndex == runwayIndex && !a.Inbound)
            {
                if (a.Phase is DeparturePhase.Rotation || a.Phase is DeparturePhase.TakeoffRoll && a.SpeedKt >= 80)
                    worst = Math.Max(worst, 10);
                else if (a.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll or DeparturePhase.RejectedTakeoff)
                    return double.PositiveInfinity;
            }
            if (a.RunwayIndex == runwayIndex && a.OnRunwayInbound)
                worst = Math.Max(worst, a.RunwayClearInS);
            if (a.CrossingRunway == runwayIndex)
                return double.PositiveInfinity;
        }
        for (var i = 0; i < _air.Count; i++)
        {
            var o = _air[i];
            if (!ReferenceEquals(o, arrival) && o.ControlledArrival && o.Landing?.RunwayIndex == runwayIndex
                && o.Phase is FlightPhase.Flare or FlightPhase.Rollout)
                worst = Math.Max(worst, o.RunwayClearInS);
        }
        if (_control.Tower.PilotHoldsRunway(runwayIndex))
            return double.PositiveInfinity;
        if (arrival is null)
            return worst; // para el piloto: él mismo no cuenta
        if (_hasUser && _userOnGround && arrival.Landing is { } l)
        {
            var d = GeoMath.DistanceM(l.Threshold, _userPosition);
            var off = GeoMath.AngleDiffDeg(l.HeadingDeg, GeoMath.InitialBearingDeg(l.Threshold, _userPosition)) * Math.PI / 180;
            if (d * Math.Cos(off) > -60 && d * Math.Cos(off) < l.LengthM + 60 && Math.Abs(d * Math.Sin(off)) < 45)
                return double.PositiveInfinity;
        }
        return worst;
    }

    // Quién ocupó la pista en la última consulta (solo referencias a cadenas existentes: no asigna en el tick).
    private string? _occupant;
    private string _occupantWhy = "";
    private DepartureAgent? _occupantAgent;

    /// <summary>¿Alguna salida IA espera en el punto de espera de esta pista? (hueco en pista mixta, ADR-093).</summary>
    private bool DeparturesWaiting(int runwayIndex)
    {
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (!a.Inbound && a.RunwayIndex == runwayIndex && a.Phase == DeparturePhase.HoldingShort)
                return true;
        }
        return false;
    }

    private bool RunwayOccupied(int runwayIndex, AirborneAgent? arrival)
    {
        _occupant = null;
        _occupantAgent = null;
        _occupantWhy = "";
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            if (a.RunwayIndex == runwayIndex && !a.Inbound && a.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp
                    or DeparturePhase.TakeoffRoll or DeparturePhase.RejectedTakeoff or DeparturePhase.Rotation)
            {
                (_occupant, _occupantWhy) = (a.Callsign, "salida en la pista");
                return true;
            }
            // Una llegada que todavía no libró la pista (auditoría 2026-11-03: no contaba).
            if (a.RunwayIndex == runwayIndex && a.OnRunwayInbound)
            {
                (_occupant, _occupantWhy) = (a.Callsign, "llegada que no libró la pista");
                _occupantAgent = a;
                return true;
            }
            if (a.CrossingRunway == runwayIndex)
            {
                (_occupant, _occupantWhy) = (a.Callsign, "cruzando la pista");
                return true;
            }
        }
        for (var i = 0; i < _air.Count; i++)
        {
            var o = _air[i];
            if (!ReferenceEquals(o, arrival) && o.ControlledArrival && o.Landing?.RunwayIndex == runwayIndex
                && o.Phase is FlightPhase.Flare or FlightPhase.Rollout)
            {
                (_occupant, _occupantWhy) = (o.Callsign, "aterrizando");
                return true;
            }
        }
        if (arrival is null)
            return false; // para el aterrizaje del piloto: él mismo no cuenta
        if (_control.Tower.PilotHoldsRunway(runwayIndex))
        {
            (_occupant, _occupantWhy) = (null, "el piloto, autorizado a alinearse o despegar");
            return true;
        }
        // El piloto en tierra dentro del rectángulo de la pista.
        if (_hasUser && _userOnGround && arrival.Landing is { } l)
        {
            var d = GeoMath.DistanceM(l.Threshold, _userPosition);
            var off = GeoMath.AngleDiffDeg(l.HeadingDeg, GeoMath.InitialBearingDeg(l.Threshold, _userPosition)) * Math.PI / 180;
            var along = d * Math.Cos(off);
            var across = Math.Abs(d * Math.Sin(off));
            if (along > -60 && along < l.LengthM + 60 && across < 45)
            {
                (_occupant, _occupantWhy) = (null, "el piloto en la pista");
                return true;
            }
        }
        return false;
    }


    private bool FarFromUser(DepartureAgent a) =>
        _hasUser && GeoMath.DistanceM(a.Pose(1).Position, _userPosition) > RemoveDistanceM;

    // ~5 veces por segundo; solo asigna si hay más aviones que nunca (crece el buffer).
    private void PublishSnapshot(double alpha)
    {
        lock (_snapshotLock)
        {
            var count = _agents.Count + _air.Count;
            if (_snapshot.Length < count)
            {
                _snapshot = new AgentSnapshot[Math.Max(count, _snapshot.Length * 2)];
                _snapshotRefs = new object?[_snapshot.Length];
            }
            FillSnapshot(alpha);
        }
        var reroutes = _reroutesDone;
        for (var i = 0; i < _agents.Count; i++)
            reroutes += _agents[i].Reroutes;
        _reroutesTotal = reroutes;
    }

    private static DeparturePhase PhaseOf(FlightPhase p) => p switch
    {
        FlightPhase.Climb => DeparturePhase.Climb,
        FlightPhase.Cruise => DeparturePhase.Cruise,
        FlightPhase.Descent => DeparturePhase.Descent,
        FlightPhase.Approach => DeparturePhase.Approach,
        FlightPhase.Final => DeparturePhase.Final,
        FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed => DeparturePhase.Landing,
        FlightPhase.GoAround => DeparturePhase.GoAround,
        FlightPhase.Holding => DeparturePhase.Holding,
        _ => DeparturePhase.Departed,
    };

    private void FillSnapshot(double alpha)
    {
        var snap = _snapshot;
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            var p = a.Pose(alpha);
            string? holdFor = null;
            for (var j = 0; j < _agents.Count && a.HoldFor != 0; j++)
            {
                if (_agents[j].ObjectId == a.HoldFor)
                    holdFor = _agents[j].Callsign;
            }
            snap[i] = new AgentSnapshot(a.ObjectId, a.Callsign, p.Position, p.HeadingTrueDeg, a.AglFt, a.SpeedKt, a.Phase,
                a.Hold, holdFor, a.ThrottlePercent, a.RunwayIndex, Type: a.TypeDesignator, WingspanM: a.WingspanM, Engines: a.EngineCount,
                RunwayHold: a.RunwayHold, HoldLeftS: a.HoldLeftS);
            _snapshotRefs[i] = a;
        }
        for (var i = 0; i < _air.Count; i++)
        {
            var f = _air[i];
            var p = f.Injected ? f.Pose(alpha) : f.Pose(1);
            snap[_agents.Count + i] = new AgentSnapshot(f.TrackId, f.Callsign, p.Position, p.HeadingTrueDeg, f.AglFt, f.SpeedKt,
                PhaseOf(f.Phase), HoldKind.None, null, f.ThrottlePercent, f.Landing?.RunwayIndex ?? -1, true, !f.Injected,
                p.AltitudeFt, f.Destination, f.Origin, f.TypeDesignator, f.Model?.WingspanM ?? 0, f.EngineCount);
            _snapshotRefs[_agents.Count + i] = f;
        }
        _snapshotCount = _agents.Count + _air.Count;
    }
}
