using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.Core.Engine;

/// <summary>Fases de una salida (RF-17, RF-21b).</summary>
public enum DeparturePhase
{
    DoorsClosing,
    BeaconOn,
    Pushback,
    PushbackComplete,
    Taxi,
    HoldingShort,
    LineUp,
    LinedUp,
    TakeoffRoll,
    /// <summary>Despegue abortado (RF-18c, "cancel take-off"): frena en la pista y espera para volver a despegar.</summary>
    RejectedTakeoff,
    Rotation,
    Climb,
    Departed,
    // Llegadas (etapa 7). Las del aire son de AirborneAgent y solo se usan en la foto del radar.
    Cruise,
    Descent,
    Approach,
    Final,
    Landing,
    GoAround,
    Holding,
    /// <summary>Llegada en su puesto: apaga motores y queda estacionada.</summary>
    AtStand,
}

/// <summary>Tiempos de la parte final de la escala (RF-21b). Valores APROXIMADOS, en segundos.</summary>
public sealed record DepartureTimings(
    double DoorsClosingS = 40,
    double BeaconBeforePushS = 30,
    double AfterPushS = 20,
    double EngineWarmupS = 60,
    double HoldShortS = 15,
    double LinedUpS = 8)
{
    public static DepartureTimings Default { get; } = new();
}

/// <summary>
/// Un cruce de pista en el rodaje (ADR-068): la pista, cómo se nombra por radio, dónde espera (m de la ruta) y desde dónde la
/// pista queda libre (la cola pasó).
/// </summary>
public sealed record RunwayCrossing(int RunwayIndex, string Ident, double HoldS, double ClearS);

/// <summary>
/// Una salida: desde el puesto hasta el ascenso inicial. El estado se avanza en ticks fijos (<see cref="Step"/>,
/// sin asignaciones) y se interpola para lo visual (<see cref="Pose"/>).
/// </summary>
public sealed class DepartureAgent
{
    /// <summary>Velocidad al entrar a la pista y alinearse (kt). Aproximado.</summary>
    public const double LineUpSpeedKt = 10;

    /// <summary>Ritmo máximo de rotación (°/s) y cuánto tarda en alcanzarlo (s). Aproximado.</summary>
    public const double RotationRateDegPerS = 3;
    public const double RotationRampS = 1.0;

    /// <summary>Segundos para pasar de 0 a la velocidad vertical inicial tras despegar. Aproximado.</summary>
    public const double ClimbRampS = 6;


    private readonly AircraftPerformance _perf;
    private readonly DepartureTimings _timings;
    private readonly LocalProjection _projection;
    private readonly Polyline _pushback;
    /// <summary>Rodaje y entrada a la pista en una sola ruta continua (sin giros bruscos entre fases).</summary>
    private Polyline _taxi;
    /// <summary>Distancia sobre <see cref="_taxi"/> del punto de espera.</summary>
    private double _holdAt;
    private readonly double _runwayHeading;

    /// <summary>Una llegada que rueda de la pista a su puesto (etapa 7): sin pushback, remolque ni Torre.</summary>
    public bool Inbound { get; private set; }

    /// <summary>Puesto de destino de una llegada (índice en el aeropuerto), o -1.</summary>
    public int StandIndex { get; private set; } = -1;

    /// <summary>Segundos en el puesto tras los que la llegada queda estacionada (motores apagados).</summary>
    public const double ParkAfterS = 45;

    /// <summary>La llegada terminó: apagada en su puesto (el motor la pasa a los estacionados).</summary>
    public bool Parked => Inbound && Phase == DeparturePhase.AtStand && _phaseTime >= ParkAfterS;

    /// <summary>Recorrido del pushback (en metros locales), para pruebas y diagnóstico.
    public Polyline PushbackPath => _pushback;

    /// <summary>Ruta de rodaje hasta el eje de la pista (en metros locales), para pruebas y diagnóstico.</summary>
    public Polyline TaxiPath => _taxi;

    private PathFollower _ground;
    private double _phaseTime;
    private double _enginesOnFor = -1;
    private bool _airborne;
    private double _pitchRate;

    /// <summary>Altura (MSL) del punto de referencia del avión apoyado, según el simulador (ADR-017).</summary>
    private double _groundRefFt;

    // Estado del tick anterior y del actual, para interpolar lo visual.
    private LocalPoint _prevPos, _pos;
    private double _prevHeading, _heading, _prevAlt, _alt, _prevPitch, _pitch;

    private double _speedKt;
    private double _verticalSpeedFpm;
    private double _throttle;
    private double _noseGearM;
    private readonly double _wingspanM;
    private readonly LocalPoint _standPos;
    private double _standHeading;

    // Remolcador: estado, pose del tick anterior y actual (se interpola como el avión) y camino de vuelta al puesto.
    private enum TugState { Attached, BackingOff, Returning, Parked }
    private TugState _tugState = TugState.Attached;
    private LocalPoint _tugPrevPos, _tugPos;
    private double _tugPrevHeading, _tugHeading;
    private double _tugBackOff;
    private LocalPoint _tugDetachedAt;
    private Polyline? _tugReturn;
    private double _tugReturnS;
    private double _tugReturnStartHeading;
    private bool _tugParkedSent;
    private double _gearDown = 1;
    private double _flapsPercent;
    private double _airborneFor;

    // Tren y flaps (V-09: se animan escribiendo su posición cuadro a cuadro). Valores APROXIMADOS de operación de línea.
    public const double GearUpDelayS = 3;            // "positive rate, gear up": unos segundos después de despegar
    public const double GearCycleS = 10;             // recorrido completo del tren
    public const double FlapsRatePercentPerS = 4;    // velocidad de las superficies
    public const double FlapsRetractAglFt = 1500;    // altura de aceleración: se recogen al ganar velocidad

    // Remolcador (V-15). Valores APROXIMADOS: al soltarse retrocede unos metros, pasa por el costado del avión (lejos
    // de la punta del ala) y se estaciona en el puesto, donde estaba la rueda de nariz (lejos del edificio).
    public const double TugBackOffM = 8;
    public const double TugBackOffSpeedMs = 1.2;
    public const double TugDriveSpeedMs = 4;
    public const double TugWingClearanceM = 8;
    public const double TugHeadingBlendM = 4; // al arrancar la vuelta, el rumbo pasa de a poco al del camino

    // Potencia por fase (% de la palanca de gases). Valores APROXIMADOS de operación de línea.
    public const double TaxiBreakawayThrottle = 28;   // empujón para empezar a moverse
    public const double TaxiAccelThrottle = 18;       // acelerando en rodaje
    public const double TaxiCruiseThrottle = 8;       // velocidad constante en rodaje
    public const double TakeoffStabilizeThrottle = 40; // se estabilizan los motores antes de dar potencia
    public const double TakeoffStabilizeS = 3;
    public const double TakeoffThrottle = 92;
    public const double ClimbThrottle = 85;
    public const double ThrustReductionAglFt = 1500;   // reducción a potencia de ascenso
    public const double ThrottleUpPerS = 10;           // la palanca nunca salta: sube y baja de a poco
    public const double ThrottleDownPerS = 15;

    public DepartureAgent(uint objectId, string callsign, AircraftPerformance perf, DepartureTimings timings,
        LocalProjection projection, Polyline pushback, Polyline taxi, double holdAt,
        double runwayHeadingDeg, double runwayElevationFt, int engineCount, double noseGearM = 0, double wingspanM = 0)
    {
        _wingspanM = wingspanM > 0 ? wingspanM : perf.Category switch
        {
            PerformanceCategory.HeavyJet => 60,
            PerformanceCategory.Turboprop => 27,
            _ => 36,
        };
        // Rueda de nariz por delante del punto de referencia (del flight_model.cfg); si no se pudo leer, típico por categoría.
        _noseGearM = noseGearM > 0 ? noseGearM : perf.Category switch
        {
            PerformanceCategory.HeavyJet => 22,
            PerformanceCategory.Turboprop => 9,
            _ => 12,
        };
        ObjectId = objectId;
        Callsign = callsign;
        EngineCount = engineCount;
        _perf = perf;
        _timings = timings;
        _projection = projection;
        _pushback = pushback;
        _taxi = taxi;
        _holdAt = Math.Clamp(holdAt, 0, taxi.Length);
        _runwayHeading = runwayHeadingDeg;
        _groundRefFt = runwayElevationFt + perf.ReferenceAglFt; // hasta que llegue la primera muestra del simulador

        _ground = PathFollower.StartOn(pushback, reverse: true);
        _pos = _prevPos = pushback.Start;
        _heading = _prevHeading = _ground.HeadingDeg;
        _alt = _prevAlt = _groundRefFt;
        Phase = DeparturePhase.DoorsClosing;
        _standPos = pushback.Start;
        _standHeading = _heading;
        // El remolcador ya está enganchado cuando empieza la salida (puertas cerrándose), mirando al avión.
        _tugPos = _tugPrevPos = Polyline.Offset(_pos, _heading, _noseGearM);
        _tugHeading = _tugPrevHeading = GeoMath.NormalizeDeg(_heading + 180);
    }

    public uint ObjectId { get; }
    public string Callsign { get; }

    // --- Coordinación en tierra (RF-18, hito 3b) ---

    /// <summary>Pista de salida (índice en el aeropuerto) y sus datos, para el turno de pista.</summary>
    public int RunwayIndex { get; init; }
    public Runway? Runway { get; init; }
    public WakeCategory Wake { get; init; } = WakeCategory.Medium;

    /// <summary>Extremo de la pista por el que despega (true = el primario), para saber cuánta pista le queda.</summary>
    public bool PrimaryEnd { get; init; } = true;

    /// <summary>Hasta esta velocidad se aborta un despegue ante una incursión; por encima sigue (como pasado V1). APROXIMADO.</summary>
    public const double RejectMaxSpeedKt = 100;
    /// <summary>Frenado al abortar (kt/s), APROXIMADO (~0,3 g).</summary>
    public const double RejectDecelKtPerS = 5;

    /// <summary>
    /// Autorización condicional (ADR-095): el tipo de la llegada que aterriza detrás de la cual se alinea ("behind the
    /// landing A320, line up behind"), o null si la de alinear no es condicional. La fija la Torre.
    /// </summary>
    public string? LineUpBehind { get; internal set; }

    /// <summary>Pista que queda por delante (m) hasta el otro extremo; infinito si no se conoce la pista.</summary>
    public double RunwayRemainingM
    {
        get
        {
            if (Runway is not { } r)
                return double.PositiveInfinity;
            var far = _projection.ToLocal(PrimaryEnd ? r.Secondary.Position : r.Primary.Position);
            return LocalPoint.Distance(_pos, far);
        }
    }

    /// <summary>
    /// "Cancel take-off" (RF-18c): si va en carrera de despegue por debajo de <see cref="RejectMaxSpeedKt"/>, aborta y
    /// frena en la pista. true si abortó.
    /// </summary>
    internal bool CancelTakeoff()
    {
        if (Phase != DeparturePhase.TakeoffRoll || _speedKt >= RejectMaxSpeedKt)
            return false;
        Next(DeparturePhase.RejectedTakeoff);
        return true;
    }

    /// <summary>Calles del rodaje en orden (para el inspector y, en la etapa 4, la autorización de rodaje).</summary>
    public IReadOnlyList<string> Taxiways { get; internal set; } = [];

    /// <summary>Llegada: calle por la que libra la pista ("B3"), o vacía si no tiene nombre.</summary>
    public string ExitTaxiway { get; internal set; } = "";

    /// <summary>Llegada: el puesto como se dice por radio ("16", "C4").</summary>
    public string StandSpoken { get; internal set; } = "";

    /// <summary>
    /// Llegada: distancia por la ruta (m) a la que la cola ya pasó el punto de espera de la salida (pista libre). Ahí espera
    /// la autorización de rodaje de Ground (ADR-065).
    /// </summary>
    public double VacatedAt { get; internal set; } = double.PositiveInfinity;

    /// <summary>Llegada que todavía no libró la pista (para la ocupación de la pista y la Torre).</summary>
    public bool OnRunwayInbound => Inbound && Phase == DeparturePhase.Taxi && _ground.Progress < VacatedAt - 10 && !TailClearOfRunway;

    /// <summary>Distancia del eje (m) desde la que la cola ya está fuera de la pista: el punto de espera típico (75-90 m). APROXIMADO.</summary>
    public const double RunwayClearLateralM = 80;

    /// <summary>
    /// La cola ya está a más de <see cref="RunwayClearLateralM"/> del eje de su pista aunque no haya llegado al punto donde
    /// pide el rodaje: detenida entre dos paralelas cercanas esperando cruzar la otra (RKPK 36L, PLAN-PULIDO bloque 3: seguía
    /// contando como "no libró la pista" y la de atrás hacía motor y al aire con la pista vacía).
    /// </summary>
    private bool TailClearOfRunway
    {
        get
        {
            if (Runway is not { } r)
                return false;
            var c = Projection.ToLocal(r.Center);
            var h = r.HeadingTrueDeg * GeoMath.DegToRad;
            var across = Math.Abs((_pos.EastM - c.EastM) * Math.Cos(h) - (_pos.NorthM - c.NorthM) * Math.Sin(h));
            return across - Math.Max(35, _wingspanM) / 2 >= RunwayClearLateralM;
        }
    }

    /// <summary>Segundos estimados para que una llegada que rueda deje libre su pista; infinito si está detenida en ella.</summary>
    public double RunwayClearInS => !OnRunwayInbound ? 0
        : _speedKt < 2 ? double.PositiveInfinity
        : Math.Max(0, VacatedAt - 10 - _ground.Progress) / (_speedKt * 0.514444);

    // ---------- Cruces de pista (ADR-068) ----------

    private List<RunwayCrossing> _crossings = [];
    private Polyline? _crossingsFor;

    /// <summary>Pistas que cruza su rodaje, en orden.</summary>
    public IReadOnlyList<RunwayCrossing> Crossings
    {
        get
        {
            UpdateCrossings();
            return _crossings;
        }
    }

    /// <summary>Cruces autorizados por la Torre (cuenta desde el primero).</summary>
    public int CrossingsCleared { get; internal set; }

    /// <summary>Cruces que oyó por radio (sin radio, todos: <see cref="int.MaxValue"/>).</summary>
    public int CrossingsHeard { get; internal set; } = int.MaxValue;

    /// <summary>Cruces ya hechos (la cola pasó el otro lado).</summary>
    public int CrossingsDone { get; private set; }

    /// <summary>El próximo cruce, o null.</summary>
    public RunwayCrossing? PendingCrossing
    {
        get
        {
            UpdateCrossings();
            return CrossingsDone < _crossings.Count ? _crossings[CrossingsDone] : null;
        }
    }

    /// <summary>¿Puede hacer el próximo cruce (autorizado y oído)?</summary>
    public bool CrossingAllowed => CrossingsDone < Math.Min(CrossingsCleared, CrossingsHeard);

    /// <summary>Pista que está cruzando (o a punto de cruzar con autorización), o −1: la ocupa.</summary>
    public int CrossingRunway =>
        Phase == DeparturePhase.Taxi && PendingCrossing is { } c && CrossingAllowed && _ground.Progress >= c.HoldS - 5 ? c.RunwayIndex : -1;

    /// <summary>Recalcula los cruces si cambió la ruta (desvío): los ya pasados cuentan como hechos.</summary>
    private void UpdateCrossings()
    {
        if (ReferenceEquals(_crossingsFor, _taxi) || Graph is not { } graph)
            return;
        _crossingsFor = _taxi;
        _crossings = ComputeCrossings(graph, _taxi, WingspanM);
        // Llegada: librar la pista donde aterrizó no es un cruce (EYVI, ADR-093: esperaba autorización para salir de su pista
        // mientras la salida alineada la esperaba a ella).
        if (Inbound)
            _crossings.RemoveAll(c => c.RunwayIndex == RunwayIndex && c.HoldS < VacatedAt);
        // Sin LINQ: una lambda que captura una variable local asigna al entrar al método (en cada tick, aunque salga antes).
        var progress = Phase == DeparturePhase.Taxi ? _ground.Progress : 0;
        var done = 0;
        foreach (var c in _crossings)
        {
            if (c.ClearS < progress)
                done++;
        }
        CrossingsDone = done;
        CrossingsCleared = CrossingsDone;
        if (CrossingsHeard != int.MaxValue)
            CrossingsHeard = CrossingsDone;
    }

    /// <summary>
    /// Hasta dónde puede rodar por los cruces: el punto de espera del próximo cruce sin autorización. Mientras espera ahí
    /// queda "esperando pista" (no cuenta como bloqueo con otro avión). Avanza los cruces hechos.
    /// </summary>
    private double CrossingLimit()
    {
        UpdateCrossings();
        while (CrossingsDone < _crossings.Count && _ground.Progress > _crossings[CrossingsDone].ClearS)
            CrossingsDone++;
        if (CrossingsDone >= _crossings.Count || CrossingAllowed)
            return double.PositiveInfinity;
        var c = _crossings[CrossingsDone];
        if (_ground.Progress >= c.HoldS - 60 && Hold == HoldKind.None)
        {
            Hold = HoldKind.Runway;
            RunwayHold = RunwayHold.Crossing;
        }
        return c.HoldS;
    }

    /// <summary>
    /// Dónde cruza pistas una ruta: tramos sobre una pista (con sus márgenes); el punto de espera es el marcado de esa
    /// entrada si está en la ruta (si no, 50 m antes de pisar la pista) y la pista queda libre con la cola pasada
    /// (envergadura + 15 m después de salir).
    /// </summary>
    internal static List<RunwayCrossing> ComputeCrossings(TaxiGraph graph, Polyline taxi, double wingspanM)
    {
        var list = new List<RunwayCrossing>();
        var current = -1;
        var start = 0.0;
        for (var s = 0.0; s <= taxi.Length; s += 4)
        {
            var r = graph.RunwayAtPoint(taxi.PointAt(s));
            if (r == current)
                continue;
            // Lo que empieza sobre una pista (la llegada que la está dejando) no es un cruce.
            if (current >= 0 && start > 0)
                list.Add(Crossing(graph, taxi, current, start, s, wingspanM));
            current = r;
            start = s;
        }
        // La que termina sobre una pista (una salida que se alinea) no es un cruce.
        return list;
    }

    private static RunwayCrossing Crossing(TaxiGraph graph, Polyline taxi, int runway, double enterS, double exitS, double wingspanM)
    {
        var holdS = Math.Max(0, enterS - 50);
        foreach (var e in graph.RunwayEntries)
        {
            if (e.RunwayIndex != runway || !e.HoldIsMarked)
                continue;
            var p = graph.Position(e.HoldNode);
            var s = taxi.ProjectWithin(p, Math.Max(0, enterS - 200), enterS, out var lateral);
            if (lateral < 15 && s < enterS)
                holdS = Math.Max(holdS, s);
        }
        var r = graph.Runways[runway];
        return new RunwayCrossing(runway, r.Primary.Ident, holdS, exitS + Math.Max(35, wingspanM) + 15);
    }

    /// <summary>Cruces del rodaje, en orden (ver <see cref="TaxiMarks"/>).</summary>
    public IReadOnlyList<TaxiMark> Marks { get; internal set; } = [];

    // Para recalcular la ruta (RF-18b): grafo, punto de espera y entrada a la pista.
    internal TaxiGraph? Graph { get; init; }
    internal int HoldNode { get; init; } = -1;
    internal LocalPoint HoldPosition { get; init; }
    internal LocalPoint[] Tail { get; init; } = [];

    /// <summary>Metros que rueda por la pista hasta la cabecera antes de alinearse (0 sin backtrack, ADR-085).</summary>
    public double BacktrackM { get; init; }

    /// <summary>Velocidad al rodar por la pista en backtrack (APROXIMADO).</summary>
    public const double BacktrackSpeedKt = 20;

    /// <summary>Segundos extra que ocupa la pista por el backtrack (para no alinearlo con una llegada encima).</summary>
    public double BacktrackS => BacktrackM <= 0 ? 0 : BacktrackM / (BacktrackSpeedKt * 0.514444) + 60;

    /// <summary>
    /// Backtrack (ADR-085): de la entrada al eje, por la pista hacia la cabecera, vuelta de 180° (radio 15 m, en el ancho
    /// de la pista) y alineado 90 m dentro, mirando en el sentido del despegue.
    /// </summary>
    private static LocalPoint[] BacktrackTail(TaxiGraph graph, Runway runway, RunwayEnd end, LocalPoint entry, LocalPoint onCenterline)
    {
        var h = end.HeadingTrueDeg;
        var threshold = ProjectToCenterline(graph.Projection, runway, graph.Projection.ToLocal(end.Position));
        const double r = 15;
        var center = Polyline.Offset(threshold, h, 45);
        var points = new List<LocalPoint> { entry, onCenterline };
        for (var deg = 0; deg <= 180; deg += 30)
        {
            var t = deg * Math.PI / 180;
            points.Add(Polyline.Offset(Polyline.Offset(center, h + 90, r * Math.Cos(t)), h + 180, r * Math.Sin(t)));
        }
        points.Add(Polyline.Offset(threshold, h, 90));
        return [.. points];
    }

    /// <summary>Tramos del grafo entre el fin del pushback y el primer cruce (no los cubre ninguna marca).</summary>
    internal long[] StartEdges { get; init; } = [];

    /// <summary>Cuántas veces se le recalculó la ruta para resolver un bloqueo.</summary>
    public int Reroutes { get; private set; }

    /// <summary>
    /// Desvío para resolver un bloqueo de frente (RF-18b): desde un cruce que tenga por delante, antes del obstáculo y
    /// con espacio para frenar, busca otra ruta a su punto de espera sin pasar cerca del obstáculo. false si no hay.
    /// </summary>
    internal bool TryReroute(LocalPoint obstacle, double clearanceM = 30) => TryReroute(obstacle, [obstacle], clearanceM);

    /// <param name="avoid">Puntos a evitar: la posición del otro y lo que le queda de ruta por delante.</param>
    internal bool TryReroute(LocalPoint obstacle, IReadOnlyList<LocalPoint> avoid, double clearanceM)
    {
        if (Graph is not { } graph || HoldNode < 0 || Phase is not (DeparturePhase.Taxi or DeparturePhase.PushbackComplete))
            return false;
        var progress = TaxiProgress;
        var v = _ground.SpeedKt * 0.514444;
        var brake = v * v / (2 * _perf.Ground.DecelKtPerS * 0.514444) + 10;
        var obstacleS = _taxi.ProjectWithin(obstacle, progress, progress + 600, out _);
        bool Blocked(int x, int y)
        {
            var seg = new Polyline([graph.Position(x), graph.Position(y)]);
            foreach (var p in avoid)
            {
                if (LocalPoint.Distance(seg.PointAt(seg.Project(p)), p) < clearanceM)
                    return true;
            }
            return false;
        }
        foreach (var m in Marks)
        {
            if (m.S < progress + brake || m.S > obstacleS - clearanceM)
                continue;
            var route = graph.FindRoute(m.Node, HoldNode, Blocked);
            if (route is null || route.Nodes.Count < 2)
                continue;
            var corners = new List<LocalPoint> { _pos };
            for (var s = progress + 10; s < m.S; s += 10)
                corners.Add(_taxi.PointAt(s));
            foreach (var n in route.Nodes)
                corners.Add(graph.Position(n));
            corners.AddRange(Tail);
            var taxi = Polyline.Smooth(corners, _perf.Ground.CornerRadiusM);
            var speed = _ground.SpeedKt;
            _taxi = taxi;
            _holdAt = taxi.Project(HoldPosition);
            Marks = TaxiMarks(graph, taxi, _holdAt, tailEdges: Inbound);
            Taxiways = TaxiwaysAlong(graph, taxi, _holdAt);
            if (Phase == DeparturePhase.Taxi)
            {
                _ground = PathFollower.StartOn(taxi, reverse: false);
                _ground.SpeedKt = speed;
            }
            Reroutes++;
            return true;
        }
        return false;
    }

    public double WingspanM => _wingspanM;
    public double NoseGearM => _noseGearM;

    /// <summary>Límite que pone el coordinador sobre la ruta de rodaje (m de avance), o infinito.</summary>
    public double TaxiLimitS { get; internal set; } = double.PositiveInfinity;

    /// <summary>
    /// Autorizaciones del control (ADR-021): sin la de Ground no empuja; sin las de Tower no entra a la pista ni despega.
    /// Sin control (un avión solo, en pruebas) tiene todas.
    /// </summary>
    public ClearanceKind Clearances { get; private set; } = ClearanceKind.All;

    /// <summary>
    /// Autorizaciones ya dichas por radio y colacionadas (hito 4e-1, <see cref="AiRadio"/>): el avión actúa solo con la
    /// del control <b>y</b> la de la radio. Sin radio (pruebas, Headless) tiene todas.
    /// </summary>
    public ClearanceKind RadioClearances { get; internal set; } = ClearanceKind.All;

    public bool IsCleared(ClearanceKind kind) => (Clearances & RadioClearances & kind) == kind;

    /// <summary>Tipo OACI del avión ("A20N", "B738") para describirlo por radio ("the A320"); vacío si no se sabe.</summary>
    public string TypeDesignator { get; set; } = "";

    /// <summary>Segundos en la fase actual.</summary>
    public double PhaseTimeS => _phaseTime;

    /// <summary>Dónde se detiene antes de la pista (m de avance sobre la ruta de rodaje).</summary>
    public double HoldAt => _holdAt;

    private string? _runwayIdent;

    /// <summary>
    /// La cabecera de salida ("17R"), o null si no se sabe. Se arma una vez: <c>Ident</c> compone el texto en cada llamada
    /// y esto se lee en el tick (radio del hito 4e-1).
    /// </summary>
    public string? RunwayIdent => _runwayIdent ??= Runway is { } r ? (PrimaryEnd ? r.Primary.Ident : r.Secondary.Ident) : null;

    internal void SetClearance(ClearanceKind kind, bool granted) =>
        Clearances = granted ? Clearances | kind : Clearances & ~kind;

    /// <summary>Por qué está detenido o esperando (para el radar y el log), y por quién.</summary>
    public HoldKind Hold { get; internal set; }
    public uint HoldFor { get; internal set; }

    /// <summary>Detalle de la espera de pista (<see cref="HoldKind.Runway"/>).</summary>
    public RunwayHold RunwayHold { get; internal set; }

    /// <summary>Segundos que le quedan a la espera si se saben (estela, llegada); NaN si no.</summary>
    public double HoldLeftS { get; internal set; } = double.NaN;

    /// <summary>true mientras usa la ruta de rodaje (de fin del pushback a la pista).</summary>
    public bool OnTaxiPath => Phase is DeparturePhase.PushbackComplete or DeparturePhase.Taxi or DeparturePhase.HoldingShort
        or DeparturePhase.LineUp;

    /// <summary>Avance sobre la ruta de rodaje (m); 0 antes de empezar a rodar.</summary>
    public double TaxiProgress => Phase is DeparturePhase.Taxi or DeparturePhase.HoldingShort or DeparturePhase.LineUp ? _ground.Progress : 0;

    /// <summary>Proyección local del aeropuerto (la misma para todos los aviones).</summary>
    public LocalProjection Projection => _projection;

    /// <summary>Donde termina el pushback (inicio del rodaje).</summary>
    public LocalPoint PushEnd => _pushback.End;

    /// <summary>
    /// Segundos que le faltan al pushback para terminar de moverse (la espera con el beacon y el empuje), a velocidad
    /// de pushback: lo que Ground compara con lo que tarda en llegar el tráfico que viene. 0 si ya terminó.
    /// </summary>
    public double PushbackRemainingS => Phase switch
    {
        DeparturePhase.DoorsClosing => _timings.DoorsClosingS - _phaseTime + _timings.BeaconBeforePushS + _pushback.Length / PushbackSpeedMps,
        DeparturePhase.BeaconOn => Math.Max(0, _timings.BeaconBeforePushS - _phaseTime) + _pushback.Length / PushbackSpeedMps,
        DeparturePhase.Pushback => Math.Max(0, _pushback.Length - _ground.Progress) / PushbackSpeedMps,
        _ => 0,
    };

    private double PushbackSpeedMps => Math.Max(_perf.PushbackSpeedKt, 1) * 0.514444;

    /// <summary>Velocidad de rodaje de su tipo (m/s): la mayor a la que puede venir por una calle.</summary>
    public double TaxiSpeedMps => _perf.TaxiSpeedKt * 0.514444;
    public int EngineCount { get; }
    public DeparturePhase Phase { get; private set; }

    public bool EnginesRunning => _enginesOnFor >= 0;

    /// <summary>Palanca de gases actual (%), ya suavizada.</summary>
    public double ThrottlePercent => _throttle;

    /// <summary>true mientras el simulador debe apoyarlo en el suelo.</summary>
    public bool OnGround => !_airborne;

    /// <summary>Altura sobre la pista (ft); 0 en tierra.</summary>
    public double AglFt => _airborne ? _alt - _groundRefFt : 0;

    public LocalPoint Position => _pos;
    public double HeadingDeg => _heading;
    public double SpeedKt => _speedKt;

    /// <summary>Elevación real del suelo bajo el avión en el simulador (de las muestras), sin su altura de referencia.</summary>
    public double GroundElevationFt => _groundRefFt - _perf.ReferenceAglFt;

    /// <summary>Categoría de rendimiento (para el relevo en el aire, etapa 6).</summary>
    public PerformanceCategory Category => _perf.Category;

    /// <summary>Luces según la fase (RF-21).</summary>
    public LightFlags Lights => Phase switch
    {
        DeparturePhase.DoorsClosing => LightFlags.Nav,
        DeparturePhase.BeaconOn or DeparturePhase.Pushback or DeparturePhase.PushbackComplete => LightFlags.Nav | LightFlags.Beacon,
        DeparturePhase.Taxi or DeparturePhase.HoldingShort => LightFlags.Nav | LightFlags.Beacon | LightFlags.Taxi,
        // Alineado: strobes; las de aterrizaje recién con la carrera de despegue (como en la cabina; prueba de Marco
        // 2026-10-31: el brillo de las luces de aterrizaje de los que esperaban en la pista molestaba).
        DeparturePhase.LineUp or DeparturePhase.LinedUp => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe | LightFlags.Taxi,
        DeparturePhase.Climb when AglFt > 10_000 => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe,
        DeparturePhase.AtStand => EnginesRunning ? LightFlags.Nav | LightFlags.Beacon : LightFlags.Nav,
        _ => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe | LightFlags.Landing,
    };

    /// <summary>
    /// Muestra del simulador con la altura del avión y su AGL (ADR-017). En tierra fija la altura de referencia desde
    /// la que despegará, para que no haya un salto al pasar al modo "en el aire".
    /// </summary>
    public void OnAltitudeSample(double altitudeFt, double aglFt)
    {
        if (_airborne || aglFt > 50)
            return;
        _groundRefFt += (altitudeFt - _groundRefFt) * 0.5;
        _alt = _prevAlt = _groundRefFt;
    }

    /// <summary>Avanza un tick de simulación.</summary>
    public void Step(double dt)
    {
        _prevPos = _pos;
        _prevHeading = _heading;
        _prevAlt = _alt;
        _prevPitch = _pitch;
        _phaseTime += dt;
        if (_enginesOnFor >= 0)
            _enginesOnFor += dt;
        var speedBefore = _speedKt;
        StepPhase(dt);
        UpdateThrottle(dt, _speedKt - speedBefore);
        UpdateGearAndFlaps(dt);
        UpdateTug(dt);
    }

    /// <summary>
    /// Remolcador: enganchado a la rueda de nariz desde que se cierran las puertas hasta el fin del pushback; mientras
    /// empuja mira en la dirección en que avanza la rueda de nariz. Después se suelta, retrocede unos metros, vuelve al
    /// puesto por el costado del avión y se queda estacionado ahí.
    /// </summary>
    private void UpdateTug(double dt)
    {
        _tugPrevPos = _tugPos;
        _tugPrevHeading = _tugHeading;
        switch (_tugState)
        {
            case TugState.Attached:
                var nose = Polyline.Offset(_pos, _heading, _noseGearM);
                if (Phase == DeparturePhase.Pushback && LocalPoint.Distance(_tugPos, nose) > 0.005)
                    _tugHeading = Polyline.Bearing(_tugPos, nose);
                else if (Phase is DeparturePhase.DoorsClosing or DeparturePhase.BeaconOn)
                    _tugHeading = GeoMath.NormalizeDeg(_heading + 180);
                _tugPos = nose;
                if (Phase is not (DeparturePhase.DoorsClosing or DeparturePhase.BeaconOn or DeparturePhase.Pushback))
                {
                    _tugState = TugState.BackingOff;
                    _tugDetachedAt = nose;
                }
                break;
            case TugState.BackingOff:
                // Marcha atrás (su frente sigue mirando al avión).
                _tugBackOff = Math.Min(TugBackOffM, _tugBackOff + TugBackOffSpeedMs * dt);
                _tugPos = Polyline.Offset(_tugDetachedAt, _tugHeading + 180, _tugBackOff);
                if (_tugBackOff >= TugBackOffM)
                {
                    _tugReturn = TugReturnPath();
                    _tugReturnStartHeading = _tugHeading;
                    _tugState = TugState.Returning;
                }
                break;
            case TugState.Returning:
                _tugReturnS = Math.Min(_tugReturn!.Length, _tugReturnS + TugDriveSpeedMs * dt);
                _tugPos = _tugReturn.PointAt(_tugReturnS);
                var pathHeading = _tugReturn.HeadingAt(_tugReturnS);
                var blend = Math.Min(1, _tugReturnS / TugHeadingBlendM);
                _tugHeading = GeoMath.NormalizeDeg(_tugReturnStartHeading + GeoMath.AngleDiffDeg(_tugReturnStartHeading, pathHeading) * blend);
                if (_tugReturnS >= _tugReturn.Length)
                    _tugState = TugState.Parked;
                break;
        }
    }

    /// <summary>
    /// Vuelta al puesto: arranca hacia el avión y dobla enseguida hacia el lado donde quedó el puesto, pasa por el
    /// costado a media envergadura + margen, y termina frente al puesto mirando hacia afuera.
    /// </summary>
    private Polyline TugReturnPath()
    {
        var side = GeoMath.AngleDiffDeg(_heading, Polyline.Bearing(_pos, _standPos)) >= 0 ? 1 : -1;
        var lateral = _heading + 90 * side;
        var offset = _wingspanM / 2 + TugWingClearanceM;
        var start = _tugPos;
        var turnIn = Polyline.Offset(Polyline.Offset(start, _tugHeading, 6), lateral, 3);
        var besideNose = Polyline.Offset(Polyline.Offset(_pos, _heading, _noseGearM), lateral, offset);
        var besideTail = Polyline.Offset(Polyline.Offset(_pos, _heading + 180, _noseGearM * 1.5), lateral, offset);
        var park = Polyline.Offset(_standPos, _standHeading, _noseGearM);
        return Polyline.Smooth([start, turnIn, besideNose, besideTail, park], 10, maxDeviationM: 6, minRadiusM: 6);
    }

    /// <summary>
    /// Pose del remolcador para este cuadro (interpolada), o false si no hay que mandar nada: estacionado se manda una
    /// sola vez.
    /// </summary>
    public bool TugUpdate(double alpha, out TugPose pose)
    {
        pose = default;
        if (_tugState == TugState.Parked)
        {
            if (_tugParkedSent)
                return false;
            _tugParkedSent = true;
        }
        var e = _tugPrevPos.EastM + (_tugPos.EastM - _tugPrevPos.EastM) * alpha;
        var n = _tugPrevPos.NorthM + (_tugPos.NorthM - _tugPrevPos.NorthM) * alpha;
        var heading = GeoMath.NormalizeDeg(_tugPrevHeading + GeoMath.AngleDiffDeg(_tugPrevHeading, _tugHeading) * alpha);
        // Enganchado va exactamente en la rueda de nariz interpolada (si no, se notaría una separación al girar).
        LocalPoint at;
        if (_tugState == TugState.Attached)
        {
            var plane = Pose(alpha);
            at = Polyline.Offset(_projection.ToLocal(plane.Position), plane.HeadingTrueDeg, _noseGearM);
        }
        else
        {
            at = new LocalPoint(e, n);
        }
        pose = new TugPose(_projection.ToGeo(at), heading, _groundRefFt);
        return true;
    }

    /// <summary>
    /// Flaps de despegue al empezar a rodar (lista "after start"), recogidos sobre la altura de aceleración; tren arriba
    /// unos segundos después del despegue.
    /// </summary>
    private void UpdateGearAndFlaps(double dt)
    {
        if (_airborne)
        {
            _airborneFor += dt;
            if (_airborneFor >= GearUpDelayS)
                _gearDown = Math.Max(0, _gearDown - dt / GearCycleS);
        }
        var flapsTarget = Inbound ? 0 : Phase switch
        {
            DeparturePhase.Taxi or DeparturePhase.HoldingShort or DeparturePhase.LineUp or DeparturePhase.LinedUp
                or DeparturePhase.TakeoffRoll or DeparturePhase.RejectedTakeoff or DeparturePhase.Rotation => _perf.TakeoffFlapsPercent,
            DeparturePhase.Climb when AglFt < FlapsRetractAglFt => _perf.TakeoffFlapsPercent,
            _ => 0,
        };
        _flapsPercent = flapsTarget > _flapsPercent
            ? Math.Min(flapsTarget, _flapsPercent + FlapsRatePercentPerS * dt)
            : Math.Max(flapsTarget, _flapsPercent - FlapsRatePercentPerS * dt);
    }

    /// <summary>Tren abajo (1) o arriba (0), y flaps (%).</summary>
    public double GearDown => _gearDown;
    public double FlapsPercent => _flapsPercent;

    /// <summary>
    /// Gases según la fase y lo que hace el avión: empujón al empezar a rodar, más gases al acelerar, ralentí en
    /// velocidad constante o frenando; potencia de despegue en dos pasos y reducción a potencia de ascenso.
    /// </summary>
    private void UpdateThrottle(double dt, double speedChangeKt)
    {
        var target = 0.0;
        if (EnginesRunning)
        {
            target = Phase switch
            {
                DeparturePhase.Taxi or DeparturePhase.LineUp when speedChangeKt > 0.01 =>
                    _speedKt < 3 ? TaxiBreakawayThrottle : TaxiAccelThrottle,
                DeparturePhase.Taxi or DeparturePhase.LineUp when speedChangeKt > -0.01 && _speedKt > 1 => TaxiCruiseThrottle,
                DeparturePhase.TakeoffRoll => _phaseTime < TakeoffStabilizeS ? TakeoffStabilizeThrottle : TakeoffThrottle,
                DeparturePhase.Rotation => TakeoffThrottle,
                DeparturePhase.Climb => AglFt < ThrustReductionAglFt ? TakeoffThrottle : ClimbThrottle,
                _ => 0,
            };
        }
        _throttle = target > _throttle
            ? Math.Min(target, _throttle + ThrottleUpPerS * dt)
            : Math.Max(target, _throttle - ThrottleDownPerS * dt);
    }

    private void StepPhase(double dt)
    {
        switch (Phase)
        {
            case DeparturePhase.DoorsClosing:
                if (_phaseTime >= _timings.DoorsClosingS)
                    Next(DeparturePhase.BeaconOn);
                break;

            case DeparturePhase.BeaconOn:
                // Sin tráfico que lo impida (coordinador en tierra).
                if (_phaseTime >= _timings.BeaconBeforePushS && IsCleared(ClearanceKind.Pushback))
                    Next(DeparturePhase.Pushback);
                break;

            case DeparturePhase.Pushback:
                StepGround(_pushback, _perf.PushbackSpeedKt, dt, _pushback.Length);
                // Los motores se arrancan durante el pushback (RF-21b).
                if (_enginesOnFor < 0 && _ground.Progress > _pushback.Length * 0.4)
                    _enginesOnFor = 0;
                if (_ground.Arrived(_pushback))
                {
                    if (_enginesOnFor < 0)
                        _enginesOnFor = 0;
                    Next(DeparturePhase.PushbackComplete);
                }
                break;

            case DeparturePhase.PushbackComplete:
                // Si el pushback no pudo dejarlo alineado (puesto incómodo), gira despacio con el tractor (5°/s).
                var taxiHeading = _taxi.HeadingAt(0);
                if (Math.Abs(GeoMath.AngleDiffDeg(_heading, taxiHeading)) > 0.5)
                {
                    _heading = GeoMath.NormalizeDeg(_heading + Math.Clamp(GeoMath.AngleDiffDeg(_heading, taxiHeading), -5 * dt, 5 * dt));
                    break;
                }
                // Se espera a que se desconecte el tractor y a que los motores estén estabilizados.
                if (_phaseTime >= _timings.AfterPushS && _enginesOnFor >= _timings.EngineWarmupS && TaxiLimitS > 1)
                {
                    _ground = PathFollower.StartOn(_taxi, reverse: false);
                    Next(DeparturePhase.Taxi);
                }
                break;

            case DeparturePhase.Taxi when Inbound:
                // Libra la pista y espera la autorización de rodaje de Ground (sin radio, la tiene desde el principio).
                var cleared = IsCleared(ClearanceKind.Taxi);
                StepGround(_taxi, _perf.TaxiSpeedKt, dt,
                    Math.Min(Math.Min(cleared ? _taxi.Length : Math.Min(VacatedAt, _taxi.Length), TaxiLimitS), CrossingLimit()));
                if (_ground.Arrived(_taxi))
                    Next(DeparturePhase.AtStand);
                break;

            case DeparturePhase.AtStand:
                // Termina de alinearse con el puesto, apaga los motores y queda estacionado.
                _speedKt = 0;
                var standDiff = GeoMath.AngleDiffDeg(_heading, _standHeading);
                if (Math.Abs(standDiff) > 0.5)
                    _heading = GeoMath.NormalizeDeg(_heading + Math.Clamp(standDiff, -3 * dt, 3 * dt));
                if (_phaseTime >= 20)
                    _enginesOnFor = -1;
                break;

            case DeparturePhase.Taxi:
                StepGround(_taxi, _perf.TaxiSpeedKt, dt, Math.Min(Math.Min(_holdAt, TaxiLimitS), CrossingLimit()));
                if (_ground.StoppedAt(_holdAt))
                    Next(DeparturePhase.HoldingShort);
                break;

            case DeparturePhase.HoldingShort:
                // Se alinea cuando la pista está libre (coordinador; la autorización del ATC llega en la etapa 4).
                if (_phaseTime >= _timings.HoldShortS && IsCleared(ClearanceKind.LineUp))
                    Next(DeparturePhase.LineUp);
                break;

            case DeparturePhase.LineUp:
                // En backtrack rueda más rápido por la pista hasta acercarse a la vuelta en la cabecera.
                StepGround(_taxi, BacktrackM > 0 && _taxi.Length - _ground.Progress > 200 ? BacktrackSpeedKt : LineUpSpeedKt, dt, _taxi.Length);
                if (_ground.Arrived(_taxi))
                {
                    _heading = _runwayHeading;
                    Next(DeparturePhase.LinedUp);
                }
                break;

            case DeparturePhase.LinedUp:
                if (_phaseTime >= _timings.LinedUpS && IsCleared(ClearanceKind.Takeoff))
                    Next(DeparturePhase.TakeoffRoll);
                break;

            case DeparturePhase.TakeoffRoll:
                // La aceleración sigue a los gases: mientras los motores suben de potencia, acelera menos.
                var thrust = Math.Clamp((_throttle - TaxiCruiseThrottle) / (TakeoffThrottle - TaxiCruiseThrottle), 0.1, 1);
                _speedKt += _perf.TakeoffAccelKtPerS * thrust * dt;
                MoveStraight(dt);
                if (_speedKt >= _perf.RotateSpeedKt)
                    Next(DeparturePhase.Rotation);
                break;

            case DeparturePhase.RejectedTakeoff:
                _speedKt = Math.Max(0, _speedKt - RejectDecelKtPerS * dt);
                MoveStraight(dt);
                if (_speedKt <= 0)
                    Next(DeparturePhase.LinedUp); // detenido en la pista; despega de nuevo cuando Tower lo autorice
                break;

            case DeparturePhase.Rotation:
                _speedKt += _perf.TakeoffAccelKtPerS * 0.6 * dt;
                _pitchRate = Math.Min(RotationRateDegPerS, _pitchRate + RotationRateDegPerS / RotationRampS * dt);
                _pitch += _pitchRate * dt;
                MoveStraight(dt);
                if (_pitch >= _perf.LiftoffPitchDeg)
                {
                    // Despegue: desde la altura real del avión en la pista, sin salto (ADR-017).
                    _airborne = true;
                    _alt = _prevAlt = _groundRefFt;
                    Next(DeparturePhase.Climb);
                }
                break;

            case DeparturePhase.Climb:
                _speedKt = Math.Min(_perf.ClimbSpeedKt, _speedKt + _perf.ClimbAccelKtPerS * dt);
                _verticalSpeedFpm = Math.Min(_perf.InitialClimbFpm, _verticalSpeedFpm + _perf.InitialClimbFpm / ClimbRampS * dt);
                // La nariz sigue subiendo hasta el ángulo de ascenso, cada vez más despacio.
                _pitchRate = Math.Clamp((_perf.ClimbPitchDeg - _pitch) * 0.8, -RotationRateDegPerS, RotationRateDegPerS);
                _pitch += _pitchRate * dt;
                _alt += _verticalSpeedFpm / 60 * dt;
                MoveStraight(dt);
                break;

            case DeparturePhase.Departed:
                break;
        }
    }

    /// <summary>El motor la marca como terminada (fuera de la vista o retirada).</summary>
    public void MarkDeparted() => Phase = DeparturePhase.Departed;

    /// <summary>Pose interpolada entre el tick anterior y el actual (alpha en [0, 1]).</summary>
    public AircraftPose Pose(double alpha)
    {
        var e = _prevPos.EastM + (_pos.EastM - _prevPos.EastM) * alpha;
        var n = _prevPos.NorthM + (_pos.NorthM - _prevPos.NorthM) * alpha;
        var heading = GeoMath.NormalizeDeg(_prevHeading + GeoMath.AngleDiffDeg(_prevHeading, _heading) * alpha);
        var alt = _prevAlt + (_alt - _prevAlt) * alpha;
        var pitch = _prevPitch + (_pitch - _prevPitch) * alpha;
        // Marcha atrás la velocidad longitudinal es negativa: las ruedas ruedan hacia atrás.
        var longitudinal = Phase == DeparturePhase.Pushback ? -_speedKt : _speedKt;
        return new AircraftPose(_projection.ToGeo(new LocalPoint(e, n)), alt, heading, pitch, 0, !_airborne, _speedKt,
            longitudinal, _gearDown, _flapsPercent, _airborne ? _verticalSpeedFpm : 0);
    }

    private void StepGround(Polyline path, double maxSpeedKt, double dt, double stopAt)
    {
        _ground.Step(path, dt, _perf.Ground, maxSpeedKt, stopAt);
        _pos = _ground.Position;
        _heading = _ground.HeadingDeg;
        _speedKt = _ground.SpeedKt;
    }

    private void MoveStraight(double dt)
    {
        var step = _speedKt * 0.514444 * dt;
        _pos = Polyline.Offset(_pos, _heading, step);
    }

    private void Next(DeparturePhase phase)
    {
        Phase = phase;
        _phaseTime = 0;
    }

    /// <summary>
    /// Arma la salida de un avión estacionado: pushback hasta quedar sobre la calle alineado con el rodaje, ruta A*
    /// suavizada hasta el punto de espera y entrada a la pista terminando sobre su eje. null si no hay ruta.
    /// </summary>
    public static DepartureAgent? Plan(uint objectId, string callsign, AirportData airport, TaxiGraph graph,
        int parkingIndex, int runwayIndex, bool primaryEnd, AircraftPerformance perf, DepartureTimings timings,
        int engineCount, IReadOnlySet<int>? occupiedStands = null, double noseGearM = 0, double wingspanM = 0,
        GeoPoint? startPosition = null, double? startHeadingDeg = null, WakeCategory wake = WakeCategory.Medium,
        Func<RunwayEntry, bool>? entryFilter = null)
    {
        // La entrada más cercana a la cabecera a la que se llega desde el puesto (ADR-085); lejos de ella, backtrack.
        var from = graph.ParkingNode(parkingIndex);
        // Entre las entradas cerca del umbral (a ambos lados de la pista), la de rodaje más corto que no cruce la propia
        // pista de despegue (ADR-093, Headless de EGLL: una salida de la plataforma sur cruzaba la 27L para entrar por el
        // norte, quedaba detenida sobre la pista y trababa al que estaba alineado).
        var half = airport.Runways[runwayIndex].LengthM / 2;
        var endAlong = primaryEnd ? -half : half;
        RunwayEntry? entry = null;
        TaxiRoute? route = null;
        var backtrack = false;
        foreach (var e in graph.RunwayEntries)
        {
            if (e.RunwayIndex != runwayIndex || Math.Abs(e.AlongM - endAlong) > TaxiGraph.MaxEntryToRunwayEndM
                || entryFilter is not null && !entryFilter(e) || graph.FindRoute(from, e.HoldNode) is not { } r)
                continue;
            var crossesOwn = false;
            foreach (var n in r.Nodes)
                crossesOwn |= graph.RunwayAt(n) == runwayIndex;
            if (!crossesOwn && (route is null || r.LengthM < route.LengthM))
            {
                route = r;
                entry = e;
            }
        }
        if (entry is null)
        {
            entry = graph.DepartureEntryOrBacktrack(runwayIndex, primaryEnd, out backtrack,
                e => (entryFilter is null || entryFilter(e)) && graph.FindRoute(from, e.HoldNode) is not null);
            if (entry is null)
                return null;
            route = graph.FindRoute(from, entry.HoldNode);
        }
        if (route is null || route.Nodes.Count < 2)
            return null;

        // Parte de donde está realmente el avión (el Parked ATC queda en la línea de parada, no en el centro del puesto).
        var parking = airport.Parkings[parkingIndex];
        var stand = graph.Projection.ToLocal(startPosition ?? parking.Position);
        var standHeading = startHeadingDeg ?? parking.HeadingTrueDeg;

        // Rodaje + entrada a la pista en una sola ruta: nodos de la ruta A* (desde donde se conecta el puesto) hasta
        // el punto de espera, luego la entrada, el eje de la pista y 60 m por el eje. Curvas suaves en todas las esquinas.
        var runway = airport.Runways[runwayIndex];
        var end = primaryEnd ? runway.Primary : runway.Secondary;
        var holdPos = graph.Position(entry.HoldNode);
        var onCenterline = ProjectToCenterline(graph.Projection, runway, graph.Position(entry.RunwayNode));
        var corners = new List<LocalPoint>(route.Nodes.Count);
        for (var i = 1; i < route.Nodes.Count; i++)
            corners.Add(graph.Position(route.Nodes[i]));
        LocalPoint[] tail = backtrack
            ? BacktrackTail(graph, runway, end, graph.Position(entry.EntryNode), onCenterline)
            : [graph.Position(entry.EntryNode), onCenterline, Polyline.Offset(onCenterline, end.HeadingTrueDeg, 60)];

        var (pushback, taxi, holdAt) = PlanGround(graph, stand, standHeading, corners, tail, holdPos, entry.HoldNode, perf,
            parkingIndex, occupiedStands);
        return new DepartureAgent(objectId, callsign, perf, timings, graph.Projection, pushback, taxi, holdAt,
            end.HeadingTrueDeg, runway.ElevationFt, engineCount, noseGearM, wingspanM)
        {
            RunwayIndex = runwayIndex,
            Runway = runway,
            PrimaryEnd = primaryEnd,
            Wake = wake,
            Marks = TaxiMarks(graph, taxi, holdAt),
            StartEdges = StartEdgesOf(graph, taxi, holdAt),
            Graph = graph,
            HoldNode = entry.HoldNode,
            HoldPosition = holdPos,
            Tail = tail,
            BacktrackM = backtrack ? LocalPoint.Distance(onCenterline, ProjectToCenterline(graph.Projection, runway, graph.Projection.ToLocal(end.Position))) : 0,
            Taxiways = TaxiwaysAlong(graph, taxi, holdAt),
        };
    }

    /// <summary>
    /// Rodaje de una llegada (etapa 7): desde donde dejó la pista (<paramref name="start"/>, a velocidad de salida) por la
    /// salida <paramref name="exit"/> y la ruta A* hasta su puesto, donde entra de frente. null si no hay ruta.
    /// </summary>
    /// <summary>
    /// Aviso de llegada (ADR-094): rodaje previsto de una llegada que todavía está en final o en la pista (salida de pista
    /// prevista → su puesto). Los tramos dirigidos van completos (cortados, el tramo compartido "empezaba" a mitad de una
    /// calle y la salida esperaba adentro, en Montreal); los cruces, solo los de los primeros <paramref name="exitM"/>
    /// (Sídney: una salida cruzaba la calle de salida cuando la llegada salía de la pista). null sin salida o sin ruta.
    /// </summary>
    public static ArrivalRoute? ArrivalRouteFor(TaxiGraph graph, Flight.LandingRunway l, int standIndex, double exitM = 250)
    {
        if (standIndex < 0 || l.ExitEntry < 0 || l.ExitEntry >= graph.RunwayEntries.Count)
            return null;
        var exit = graph.RunwayEntries[l.ExitEntry];
        if (graph.FindRoute(exit.EntryNode, graph.ParkingNode(standIndex)) is not { } route)
            return null;
        var keys = new long[route.Nodes.Count - 1];
        var nodes = new List<int>();
        var along = 0.0;
        for (var i = 0; i + 1 < route.Nodes.Count; i++)
        {
            keys[i] = TaxiMark.SegmentKey(route.Nodes[i], route.Nodes[i + 1]);
            if (along < exitM)
                nodes.Add(route.Nodes[i + 1]);
            along += LocalPoint.Distance(graph.Position(route.Nodes[i]), graph.Position(route.Nodes[i + 1]));
        }
        return new ArrivalRoute(keys, [.. nodes]);
    }

    public static DepartureAgent? PlanArrival(uint objectId, string callsign, AirportData airport, TaxiGraph graph, RunwayEntry exit,
        int standIndex, GeoPoint start, double startHeadingDeg, double startSpeedKt, AircraftPerformance perf, int engineCount,
        double noseGearM = 0, double wingspanM = 0, WakeCategory wake = WakeCategory.Medium, double standBackM = 0)
    {
        var standNode = graph.ParkingNode(standIndex);
        var route = graph.FindRoute(exit.EntryNode, standNode);
        if (route is null || route.Nodes.Count < 2)
            return null;
        var from = graph.Projection.ToLocal(start);
        var corners = new List<LocalPoint>(route.Nodes.Count + 40) { from };
        // El viraje de salida empieza en el eje (donde terminó la carrera, con el rumbo de la pista) y es un arco del radio que
        // pide la velocidad: sin saltos de rumbo (prueba de Marco: en SCEL 17L el avión rotaba de golpe hacia la calle).
        var runwayNode = graph.Position(exit.RunwayNode);
        var entryNode = graph.Position(exit.EntryNode);
        corners.AddRange(ExitArc(from, startHeadingDeg, runwayNode, entryNode, startSpeedKt));
        foreach (var n in route.Nodes)
            corners.Add(graph.Position(n));
        var stand = airport.Parkings[standIndex];
        // Un fuselaje ancho en una puerta más chica se detiene antes (ADR-167).
        if (standBackM > 0)
        {
            while (corners.Count > 2 && LocalPoint.Distance(corners[^2], corners[^1]) < standBackM + 2)
                corners.RemoveAt(corners.Count - 2);
            corners[^1] = Polyline.Offset(corners[^1], stand.HeadingTrueDeg + 180, standBackM);
        }
        var taxi = Polyline.Smooth(corners, perf.Ground.CornerRadiusM);
        var runway = airport.Runways[exit.RunwayIndex];
        var a = new DepartureAgent(objectId, callsign, perf, DepartureTimings.Default, graph.Projection,
            new Polyline([from, Polyline.Offset(from, startHeadingDeg, 1)]), taxi, taxi.Length, startHeadingDeg, runway.ElevationFt,
            engineCount, noseGearM, wingspanM)
        {
            RunwayIndex = exit.RunwayIndex,
            Runway = runway,
            Wake = wake,
            Marks = TaxiMarks(graph, taxi, taxi.Length, tailEdges: true),
            StartEdges = StartEdgesOf(graph, taxi, taxi.Length),
            Graph = graph,
            HoldNode = standNode,
            HoldPosition = graph.Position(standNode),
            Tail = [],
            ExitTaxiway = exit.TaxiwayName,
            StandSpoken = SpokenStand(stand),
        };
        // La cola pasa el punto de espera de la salida: largo del avión (~ envergadura) más 15 m de margen.
        var holdS = taxi.Project(graph.Position(exit.HoldNode));
        a.VacatedAt = Math.Min(taxi.Length, holdS + Math.Max(35, wingspanM) + 15);
        // Las calles que siguen desde ahí (las que dirá Ground), sin la de salida.
        a.Taxiways = TaxiwaysFrom(graph, taxi, a.VacatedAt, exit.TaxiwayName);
        a.StartAsArrival(standIndex, stand.HeadingTrueDeg, startSpeedKt);
        return a;
    }

    /// <summary>El puesto como se dice por radio: "C4" (GateC 4) o el número.</summary>
    public static string SpokenStand(Parking p)
    {
        return Airports.StandNames.Spoken(p);
    }

    /// <summary>Calles con nombre de la ruta desde <paramref name="fromS"/> hasta el final, sin repetir seguidas ni la de salida.</summary>
    private static List<string> TaxiwaysFrom(TaxiGraph graph, Polyline taxi, double fromS, string exitName)
    {
        var all = TaxiwaysAlong(graph, taxi, taxi.Length);
        var before = TaxiwaysAlong(graph, taxi, Math.Min(taxi.Length, fromS));
        var list = new List<string>();
        // Las de la parte ya recorrida (la de salida) no se repiten, salvo que la ruta siga por ella.
        var skip = before.Count > 0 ? before.Count - 1 : 0;
        for (var i = skip; i < all.Count; i++)
        {
            if (list.Count == 0 && all[i] == exitName && i + 1 < all.Count)
                continue;
            if (list.Count == 0 || list[^1] != all[i])
                list.Add(all[i]);
        }
        return list;
    }

    /// <summary>
    /// Puntos del viraje de salida de pista: recto por el eje desde <paramref name="from"/>, arco tangente hacia la calle
    /// (de <paramref name="runwayNode"/> a <paramref name="entryNode"/>) con radio v²/1,5 m/s² (al menos 40 m) y la calle.
    /// </summary>
    internal static List<LocalPoint> ExitArc(LocalPoint from, double headingDeg, LocalPoint runwayNode, LocalPoint entryNode, double speedKt)
    {
        var pts = new List<LocalPoint>();
        // El nodo de la salida no siempre está en el eje: el viraje se arma desde su proyección sobre el eje.
        var h = headingDeg * GeoMath.DegToRad;
        var along = (runwayNode.EastM - from.EastM) * Math.Sin(h) + (runwayNode.NorthM - from.NorthM) * Math.Cos(h);
        var corner = Polyline.Offset(from, headingDeg, Math.Max(along, 10));
        var exitBearing = GeoMath.NormalizeDeg(Math.Atan2(entryNode.EastM - corner.EastM, entryNode.NorthM - corner.NorthM) * GeoMath.RadToDeg);
        var turn = GeoMath.AngleDiffDeg(headingDeg, exitBearing);
        var theta = Math.Abs(turn) * GeoMath.DegToRad;
        var straight = LocalPoint.Distance(from, corner);
        var leg = LocalPoint.Distance(corner, entryNode);
        if (theta < 3 * GeoMath.DegToRad)
        {
            pts.Add(corner);
            pts.Add(entryNode);
            return pts;
        }
        runwayNode = corner;
        var v = speedKt * 0.514444;
        var radius = Math.Max(40, v * v / 1.5);
        var tan = Math.Tan(Math.Min(theta, 175 * GeoMath.DegToRad) / 2);
        // La tangencia no puede pasar del tramo recto que queda ni de la calle de salida.
        // Siempre unos metros de recta antes del arco (el rumbo inicial es el de la pista).
        var t = Math.Min(radius * tan, Math.Min(straight - 6, leg * 0.8));
        t = Math.Max(t, 2);
        radius = t / tan;
        var p1 = Polyline.Offset(runwayNode, headingDeg + 180, t);
        pts.Add(p1);
        var side = Math.Sign(turn);
        var center = Polyline.Offset(p1, headingDeg + 90 * side, radius);
        var start = GeoMath.NormalizeDeg(Math.Atan2(p1.EastM - center.EastM, p1.NorthM - center.NorthM) * GeoMath.RadToDeg);
        var steps = Math.Max(3, (int)Math.Ceiling(radius * theta / 4));
        for (var k = 1; k <= steps; k++)
            pts.Add(Polyline.Offset(center, start + side * theta * GeoMath.RadToDeg * k / steps, radius));
        pts.Add(entryNode);
        return pts;
    }

    private void StartAsArrival(int standIndex, double standHeadingDeg, double speedKt)
    {
        Inbound = true;
        StandIndex = standIndex;
        _standHeading = standHeadingDeg;
        _ground = PathFollower.StartOn(_taxi, reverse: false);
        _ground.SpeedKt = speedKt;
        _speedKt = speedKt;
        _pos = _prevPos = _taxi.Start;
        _heading = _prevHeading = _taxi.HeadingAt(0);
        _enginesOnFor = _timings.EngineWarmupS;
        _tugState = TugState.Parked;
        _tugParkedSent = true; // sin remolque
        _gearDown = 1;
        Clearances = ClearanceKind.All;
        Phase = DeparturePhase.Taxi;
        _phaseTime = 0;
    }

    private static long[] StartEdgesOf(TaxiGraph graph, Polyline taxi, double holdAt)
    {
        var marks = TaxiMarks(graph, taxi, holdAt);
        return EdgesBetween(graph, taxi, 0, marks.Count > 0 ? marks[0].S : holdAt);
    }

    /// <summary>Tramos del grafo (dirigidos según el avance) que recorre la ruta entre dos distancias, sin repetir.</summary>
    private static long[] EdgesBetween(TaxiGraph graph, Polyline taxi, double fromS, double toS)
    {
        var keys = new List<long>();
        for (var s = fromS + 2; s < toS - 2; s += 8)
        {
            var p = taxi.PointAt(s);
            if (!graph.NearestEdge(p, out var a, out var b))
                continue;
            // Sentido: el del avance sobre la ruta.
            var h = taxi.HeadingAt(s) * GeoMath.DegToRad;
            var pa = graph.Position(a);
            var pb = graph.Position(b);
            var forward = (pb.EastM - pa.EastM) * Math.Sin(h) + (pb.NorthM - pa.NorthM) * Math.Cos(h) >= 0;
            var key = forward ? TaxiMark.SegmentKey(a, b) : TaxiMark.SegmentKey(b, a);
            if (!keys.Contains(key))
                keys.Add(key);
        }
        return [.. keys];
    }

    /// <summary>Calles por las que pasa el rodaje hasta el punto de espera, en orden y sin repetir ("vía L, K, A").</summary>
    public static IReadOnlyList<string> TaxiwaysAlong(TaxiGraph graph, Polyline taxi, double holdAt)
    {
        // Tramos (calle, muestras cada 20 m); un tramo de una sola muestra entre dos de la misma calle es un conector
        // que la ruta apenas roza: no cuenta.
        var runs = new List<(string Name, int Samples)>();
        for (var s = 0.0; s <= holdAt; s += 20)
        {
            var name = graph.NearestTaxiwayName(taxi.PointAt(s));
            if (name.Length == 0)
                continue;
            if (runs.Count > 0 && runs[^1].Name == name)
                runs[^1] = (name, runs[^1].Samples + 1);
            else
                runs.Add((name, 1));
        }
        var names = new List<string>();
        for (var i = 0; i < runs.Count; i++)
        {
            var brief = runs[i].Samples == 1 && i > 0 && i + 1 < runs.Count && runs[i - 1].Name == runs[i + 1].Name;
            if (!brief && (names.Count == 0 || names[^1] != runs[i].Name))
                names.Add(runs[i].Name);
        }
        return names;
    }

    /// <summary>Distancia máxima entre la ruta suavizada y un cruce para considerar que pasa por él (m).</summary>
    public const double MarkMaxOffsetM = 8;

    /// <summary>
    /// Cruces (nodos con 3 o más calles, fuera de pistas) por los que pasa el rodaje hasta el punto de espera, en orden,
    /// con el tramo hasta el cruce siguiente: lo que reserva el coordinador en tierra (RF-18).
    /// Con <paramref name="tailEdges"/> (las llegadas, ADR-128), el último cruce lleva también el tramo hasta el final de la
    /// ruta: sin él, el último trecho de una llegada hasta su puesto no existía para el coordinador, y una salida empujaba o
    /// rodaba de frente por esa calle (Headless `lemg-busy`: la llegada doblaba a su puesto a 22 m de la salida).
    /// </summary>
    public static IReadOnlyList<TaxiMark> TaxiMarks(TaxiGraph graph, Polyline taxi, double holdAt, bool tailEdges = false)
    {
        var found = new List<(int Node, double S)>();
        for (var n = 0; n < graph.NodeCount; n++)
        {
            if (graph.IsParking(n) || graph.RunwayAt(n) >= 0 || graph.TaxiDegree(n) < 3)
                continue;
            var p = graph.Position(n);
            var s = taxi.ProjectWithin(p, 0, holdAt + 5, out var lateral);
            if (lateral <= MarkMaxOffsetM && s > 1)
                found.Add((n, s));
        }
        found.Sort((a, b) => a.S.CompareTo(b.S));
        var marks = new TaxiMark[found.Count];
        for (var i = 0; i < found.Count; i++)
            marks[i] = new TaxiMark(found[i].Node, found[i].S, i + 1 < found.Count ? EdgesBetween(graph, taxi, found[i].S, found[i + 1].S)
                : tailEdges ? EdgesBetween(graph, taxi, found[i].S, holdAt) : []);
        return marks;
    }

    /// <summary>Máximo quiebre entre puntos de la curva de pushback (°) y radio mínimo aceptable (m).</summary>
    public const double PushbackMaxKinkDeg = 8;
    public const double PushbackMinRadiusM = 8;


    /// <summary>Radio alrededor del puesto donde se busca el fin del pushback (m) y distancia mínima de empuje (m).</summary>
    public const double PushbackSearchRadiusM = 150;
    public const double PushbackMinDistanceM = 25;

    /// <summary>Cuánto pesa cada metro de pushback frente a uno de rodaje (va a 3 kt, unas 4 veces más lento).</summary>
    public const double PushbackCostFactor = 4;

    /// <summary>Costo extra (m) de terminar el pushback en el ramal de entrada de otro puesto.</summary>
    public const double SpurPenaltyM = 300;

    /// <summary>Tramo recto mínimo entre el fin del pushback y el nodo siguiente de la calle (m).</summary>
    public const double PushbackMinStraightM = 10;

    /// <summary>
    /// Pushback y ruta de rodaje (que sigue hasta el eje de la pista). El pushback termina sobre una calle cercana
    /// mirando en la dirección del rodaje, para que después no haya giros. Se buscan puntos en todas las calles cercanas
    /// al puesto, en ambas direcciones; para cada uno se calcula la ruta A* hacia adelante (sin volver por el mismo
    /// tramo) y se elige el menor recorrido total. Una curva sirve si no tiene cúspides ni giros demasiado cerrados.
    /// Si nada sirve: retroceso recto, y el avión gira despacio antes de rodar.
    /// </summary>
    public static (Polyline Pushback, Polyline Taxi, double HoldAt) PlanGround(TaxiGraph graph, LocalPoint stand,
        double standHeadingDeg, List<LocalPoint> corners, IReadOnlyList<LocalPoint> tail, LocalPoint holdPos, int holdNode,
        AircraftPerformance perf, int ownStand, IReadOnlySet<int>? occupiedStands)
    {
        var radius = perf.Ground.CornerRadiusM;
        double[] shapes = [0.55, 0.4, 0.7, 0.3];
        Polyline? bestPush = null;
        List<LocalPoint>? bestCorners = null;
        var bestCost = double.MaxValue;
        var edges = graph.TaxiEdgesNear(stand, PushbackSearchRadiusM).ToList();

        // 1) Pushback real: el avión retrocede por su línea de entrada (al revés de como entró) y, al llegar a la calle,
        //    gira en un solo arco hasta quedar sobre ella mirando hacia el rodaje. El giro puede empezar en cualquier
        //    vértice de la línea de entrada (líneas curvas o quebradas). Si el puesto está al fondo de una calle sin
        //    salida, la cola pasa más allá del final y el avión queda mirando hacia la única salida.
        var chain = graph.LeadIn(ownStand);
        var origins = new List<(LocalPoint Origin, double Back, double MaxStraight, List<LocalPoint> Prefix)>
        {
            (stand, standHeadingDeg + 180,
                chain.Count > 1 ? LocalPoint.Distance(stand, graph.Position(chain[1])) + PushbackStraightSlackM : PushbackSearchRadiusM,
                [stand]),
        };
        // La línea de entrada solo sirve si sale por detrás del avión (si está delante de la nariz no es un pushback).
        var leadInBehind = chain.Count > 1 && Math.Abs(GeoMath.AngleDiffDeg(standHeadingDeg + 180,
            Polyline.Bearing(stand, graph.Position(chain[1])))) < PushbackLeadInMaxAngleDeg;
        for (var i = 1; leadInBehind && i < chain.Count - 1; i++)
        {
            var q = graph.Position(chain[i]);
            if (LocalPoint.Distance(stand, q) > PushbackSearchRadiusM)
                break;
            var next = graph.Position(chain[i + 1]);
            origins.Add((q, Polyline.Bearing(q, next), LocalPoint.Distance(q, next) + PushbackStraightSlackM,
                [stand, .. chain.Skip(1).Take(i).Select(graph.Position)]));
        }
        var chainNodes = chain.ToHashSet();
        foreach (var (a, b, spurStand) in edges)
        {
            // El ramal de otro puesto, solo libre y penalizado.
            if (spurStand >= 0 && spurStand != ownStand && occupiedStands?.Contains(spurStand) == true)
                continue;
            var penalty = spurStand >= 0 && spurStand != ownStand ? SpurPenaltyM : 0;
            foreach (var (ahead, behind) in new[] { (b, a), (a, b) })
            {
                // Si la calle termina en behind (fondo o esquina), la cola puede pasar más allá de la línea pintada.
                var extension = graph.TaxiDegree(behind) <= 2 ? PushbackMaxExtensionM : 0;
                foreach (var (origin, back, maxStraight, prefix) in origins)
                {
                    var fillet = PushbackFillet(origin, back, graph.Position(ahead), graph.Position(behind), out var turnRadius, extension, maxStraight);
                    if (fillet is null)
                        continue;
                    // fillet[1] es donde empieza el arco: antes de girar, la cola debe alejarse del puesto, y el tramo
                    // recto no puede cruzar otra calle (el pushback termina en la primera calle, no en la siguiente).
                    var prefixLength = prefix.Count > 1 ? new Polyline(prefix).Length : 0;
                    if (prefixLength + LocalPoint.Distance(origin, fillet[1]) < PushbackMinBeforeTurnM ||
                        CrossesTaxiway(graph, edges, chainNodes, a, b, origin, fillet[1]))
                        continue;
                    // Tramo por la línea de entrada (esquinas suavizadas) hasta donde empieza el arco.
                    List<LocalPoint> curve = prefix.Count > 1
                        ? [.. Polyline.Smooth([.. prefix, fillet[1]], PushbackTurnRadiiM[^1], maxDeviationM: 2, minRadiusM: 8).Points, .. fillet.Skip(2)]
                        : fillet;
                    var pushEnd = curve[^1];
                    // Los giros cerrados solo si no hay otra forma.
                    var pushLength = (new Polyline(curve).Length + Math.Max(0, PushbackTurnRadiiM[0] - turnRadius)) * PushbackCostFactor + penalty;
                    if (pushLength >= bestCost)
                        continue;
                    var route = graph.FindRoute(ahead, holdNode);
                    if (route is null || (route.Nodes.Count > 1 && route.Nodes[1] == behind))
                        continue;
                    // Que el rodaje no empiece con un giro casi en horquilla en el primer cruce.
                    if (route.Nodes.Count > 1 && Math.Abs(GeoMath.AngleDiffDeg(Polyline.Bearing(pushEnd, graph.Position(ahead)),
                            Polyline.Bearing(graph.Position(ahead), graph.Position(route.Nodes[1])))) > PushbackMaxFirstTurnDeg)
                        continue;
                    var cost = pushLength + LocalPoint.Distance(pushEnd, graph.Position(ahead)) + route.LengthM;
                    if (cost >= bestCost)
                        continue;
                    bestCost = cost;
                    bestPush = new Polyline(curve);
                    bestCorners = [pushEnd, .. route.Nodes.Select(graph.Position)];
                }
            }
        }
        if (bestPush is not null && bestCorners is not null)
        {
            var taxi = Polyline.Smooth([.. bestCorners, .. tail], radius);
            return (bestPush, taxi, taxi.Project(holdPos));
        }

        // 2) Puestos donde la línea del puesto no llega bien a una calle: curva libre (Bézier) sin inflexión.
        foreach (var (a, b, spurStand) in edges)
        {
            // El ramal del propio puesto no sirve (sería volver a entrar); el de otro puesto, solo si está libre y
            // con penalización: se usa cuando no hay otra forma (puestos de fondo).
            if (spurStand == ownStand || (spurStand >= 0 && occupiedStands?.Contains(spurStand) == true))
                continue;
            var penalty = spurStand >= 0 ? SpurPenaltyM : 0;
            var pa = graph.Position(a);
            var pb = graph.Position(b);
            var length = LocalPoint.Distance(pa, pb);
            for (var along = 0.0; along <= length; along += 5)
            {
                var t = length > 0 ? along / length : 0;
                var pushEnd = new LocalPoint(pa.EastM + (pb.EastM - pa.EastM) * t, pa.NorthM + (pb.NorthM - pa.NorthM) * t);
                var dist = LocalPoint.Distance(stand, pushEnd);
                if (dist < PushbackMinDistanceM || dist > PushbackSearchRadiusM)
                    continue;
                // Las dos direcciones del tramo: hacia b (sigue por b) o hacia a (sigue por a).
                foreach (var (ahead, behind) in new[] { (b, a), (a, b) })
                {
                    // Debe quedar un tramo recto hasta el nodo siguiente: si el pushback terminara sobre el nodo, el
                    // rodaje empezaría girando hacia la calle siguiente (giro en el lugar).
                    if (LocalPoint.Distance(pushEnd, graph.Position(ahead)) < PushbackMinStraightM)
                        continue;
                    var direction = Polyline.Bearing(graph.Position(behind), graph.Position(ahead));
                    List<LocalPoint>? curve = null;
                    foreach (var shape in shapes)
                    {
                        var c = PushbackCurve(stand, standHeadingDeg, pushEnd, direction, shape);
                        if (IsSmooth(c))
                        {
                            curve = c;
                            break;
                        }
                    }
                    if (curve is null)
                        continue;
                    var pushLength = new Polyline(curve).Length * PushbackCostFactor + penalty;
                    if (pushLength >= bestCost)
                        continue;
                    var route = graph.FindRoute(ahead, holdNode);
                    if (route is null || (route.Nodes.Count > 1 && route.Nodes[1] == behind))
                        continue; // daría media vuelta sobre el mismo tramo
                    var cost = pushLength + LocalPoint.Distance(pushEnd, graph.Position(ahead)) + route.LengthM;
                    if (cost >= bestCost)
                        continue;
                    bestCost = cost;
                    bestPush = new Polyline(curve);
                    bestCorners = [pushEnd, .. route.Nodes.Select(graph.Position)];
                }
            }
        }

        if (bestPush is not null && bestCorners is not null)
        {
            var taxi = Polyline.Smooth([.. bestCorners, .. tail], radius);
            return (bestPush, taxi, taxi.Project(holdPos));
        }

        // Puesto incómodo: retroceso recto; el avión gira despacio antes de rodar (fase PushbackComplete).
        var full = Polyline.Smooth([.. corners, .. tail], radius);
        var straightEnd = Polyline.Offset(stand, standHeadingDeg + 180, perf.PushbackDistanceM);
        var from = full.Project(straightEnd);
        return (new Polyline([stand, straightEnd]), full.From(from), full.Project(holdPos) - from);
    }
    /// <summary>Radios del arco del pushback, en orden de preferencia (m), y tramo recto final para enderezar (m).</summary>
    public static readonly double[] PushbackTurnRadiiM = [25, 20, 30, 15, 12, 10, 8];
    public const double PushbackSettleM = 4;

    /// <summary>Cuánto puede pasar la cola más allá del final de una calle sin salida (m).</summary>
    public const double PushbackMaxExtensionM = 45;

    /// <summary>Giro máximo del pushback (°): más que eso sería casi dar media vuelta.</summary>
    public const double PushbackMaxTurnDeg = 150;

    /// <summary>Retroceso mínimo antes de empezar a girar (m): la cola se aleja del puesto antes de doblar.</summary>
    public const double PushbackMinBeforeTurnM = 20;

    /// <summary>
    /// true si el tramo recto p→q cruza alguna calle cercana que no sea la de destino (a-b) ni la línea de entrada.
    /// </summary>
    private static bool CrossesTaxiway(TaxiGraph graph, List<(int A, int B, int SpurStand)> edges, HashSet<int> chainNodes,
        int a, int b, LocalPoint p, LocalPoint q)
    {
        foreach (var (ea, eb, _) in edges)
        {
            if ((ea == a && eb == b) || chainNodes.Contains(ea) || chainNodes.Contains(eb))
                continue;
            if (SegmentsCross(p, q, graph.Position(ea), graph.Position(eb)))
                return true;
        }
        return false;
    }

    private static bool SegmentsCross(LocalPoint p1, LocalPoint p2, LocalPoint q1, LocalPoint q2)
    {
        static double Cross(LocalPoint o, LocalPoint a, LocalPoint b) =>
            (a.EastM - o.EastM) * (b.NorthM - o.NorthM) - (a.NorthM - o.NorthM) * (b.EastM - o.EastM);
        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>Ángulo máximo (°) entre la cola del avión y el primer tramo de la línea de entrada para seguirla.</summary>
    public const double PushbackLeadInMaxAngleDeg = 60;

    /// <summary>Giro máximo (°) del rodaje en el primer cruce después del pushback.</summary>
    public const double PushbackMaxFirstTurnDeg = 100;

    /// <summary>Cuánto más allá del vértice siguiente de la línea de entrada puede seguir el tramo recto (m).</summary>
    public const double PushbackStraightSlackM = 25;

    /// <summary>Cuánto puede quedar la esquina del giro más allá del final de la calle (m).</summary>
    public const double PushbackMaxCornerBeyondM = 20;

    /// <summary>
    /// Pushback como lo hace un remolcador: retrocede recto desde <paramref name="origin"/> en dirección
    /// <paramref name="backBearingDeg"/>, gira en un solo arco tangente a la calle (la cola hacia
    /// <paramref name="behind"/>) y endereza unos metros, quedando con la nariz hacia <paramref name="ahead"/>.
    /// null si la geometría no da (sin espacio para el arco, fuera del tramo o tramo recto demasiado largo).
    /// </summary>
    public static List<LocalPoint>? PushbackFillet(LocalPoint origin, double backBearingDeg, LocalPoint ahead, LocalPoint behind,
        out double radiusM, double maxExtensionM = 0, double maxStraightM = PushbackSearchRadiusM, double maxTurnDeg = PushbackMaxTurnDeg)
    {
        radiusM = 0;
        var stand = origin;
        var back = backBearingDeg;
        var motion = Polyline.Bearing(ahead, behind); // la cola avanza hacia behind
        var turn = GeoMath.AngleDiffDeg(back, motion);
        if (Math.Abs(turn) < 5 || Math.Abs(turn) > maxTurnDeg)
            return null;

        // Intersección de la línea del puesto (hacia atrás) con la línea de la calle.
        var (bx, by) = (Math.Sin(back * GeoMath.DegToRad), Math.Cos(back * GeoMath.DegToRad));
        var (mx, my) = (Math.Sin(motion * GeoMath.DegToRad), Math.Cos(motion * GeoMath.DegToRad));
        var den = bx * my - by * mx;
        var (dx, dy) = (ahead.EastM - stand.EastM, ahead.NorthM - stand.NorthM);
        var tStand = (dx * my - dy * mx) / den;           // distancia recta desde el puesto hasta la calle
        var tEdge = (dx * by - dy * bx) / den;            // posición de la intersección medida desde ahead hacia behind
        var edgeLength = LocalPoint.Distance(ahead, behind);
        if (tStand > maxStraightM || tEdge > edgeLength + Math.Min(maxExtensionM, PushbackMaxCornerBeyondM))
            return null;
        var x = Polyline.Offset(stand, back, tStand);
        var halfTurn = Math.Abs(turn) / 2 * GeoMath.DegToRad;
        var sign = Math.Sign(turn);

        foreach (var r in PushbackTurnRadiiM)
        {
            var tangent = r * Math.Tan(halfTurn);
            var straight = tStand - tangent;
            var endAlong = tEdge + tangent + PushbackSettleM; // fin del pushback medido desde ahead hacia behind
            if (straight < 0 || tStand > PushbackSearchRadiusM)
                continue;
            if (endAlong < PushbackMinStraightM || endAlong > edgeLength + maxExtensionM)
                continue;
            var p1 = Polyline.Offset(x, back + 180, tangent);
            var center = Polyline.Offset(p1, back + 90 * sign, r);
            var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(turn) / 2));
            var points = new List<LocalPoint>(steps + 3) { stand, p1 };
            for (var i = 1; i <= steps; i++)
            {
                var bearing = back + turn * i / steps;
                points.Add(Polyline.Offset(center, bearing - 90 * sign, r));
            }
            points.Add(Polyline.Offset(points[^1], motion, PushbackSettleM));
            radiusM = r;
            return points;
        }
        return null;
    }

    /// <summary>Giro máximo (°) en sentido contrario al principal: un pushback real gira hacia un solo lado, sin "S".</summary>
    public const double PushbackMaxCounterTurnDeg = 3;

    private static bool IsSmooth(List<LocalPoint> curve)
    {
        double left = 0, right = 0;
        for (var i = 1; i < curve.Count - 1; i++)
        {
            var h1 = Polyline.Bearing(curve[i - 1], curve[i]);
            var h2 = Polyline.Bearing(curve[i], curve[i + 1]);
            var signed = GeoMath.AngleDiffDeg(h1, h2);
            var turn = Math.Abs(signed);
            if (turn > PushbackMaxKinkDeg)
                return false;
            var len = LocalPoint.Distance(curve[i], curve[i + 1]);
            if (turn > 0.01 && len / (turn * GeoMath.DegToRad) < PushbackMinRadiusM)
                return false;
            if (signed > 0)
                right += signed;
            else
                left -= signed;
        }
        // Sin inflexión: la cola se va hacia un lado y el avión queda mirando hacia el otro.
        return Math.Min(left, right) <= PushbackMaxCounterTurnDeg;
    }

    /// <summary>Punto del eje de la pista más cercano a p.</summary>
    public static LocalPoint ProjectToCenterline(LocalProjection projection, Runway runway, LocalPoint p)
    {
        var c = projection.ToLocal(runway.Center);
        var h = runway.HeadingTrueDeg * GeoMath.DegToRad;
        var along = (p.EastM - c.EastM) * Math.Sin(h) + (p.NorthM - c.NorthM) * Math.Cos(h);
        return new LocalPoint(c.EastM + Math.Sin(h) * along, c.NorthM + Math.Cos(h) * along);
    }

    /// <summary>
    /// Curva del pushback (Bézier cúbica): sale del puesto marcha atrás (cola primero) y llega a
    /// <paramref name="pushEnd"/> con la nariz apuntando a <paramref name="endHeadingDeg"/>.
    /// </summary>
    public static List<LocalPoint> PushbackCurve(LocalPoint stand, double standHeadingDeg, LocalPoint pushEnd, double endHeadingDeg,
        double shape = 0.55)
    {
        var dist = LocalPoint.Distance(stand, pushEnd);
        var k = Math.Clamp(dist * shape, 8, 50);
        var b1 = Polyline.Offset(stand, standHeadingDeg + 180, k);   // retrocede en la dirección de la cola
        var b2 = Polyline.Offset(pushEnd, endHeadingDeg, k);         // llega moviéndose hacia atrás de su rumbo final
        // 80 tramos: el último coincide con la tangente final a menos de 1° (no hay giro al empezar a rodar).
        var points = new List<LocalPoint>(81);
        for (var i = 0; i <= 80; i++)
        {
            var t = i / 80.0;
            var u = 1 - t;
            points.Add(new LocalPoint(
                u * u * u * stand.EastM + 3 * u * u * t * b1.EastM + 3 * u * t * t * b2.EastM + t * t * t * pushEnd.EastM,
                u * u * u * stand.NorthM + 3 * u * u * t * b1.NorthM + 3 * u * t * t * b2.NorthM + t * t * t * pushEnd.NorthM));
        }
        return points;
    }
}
