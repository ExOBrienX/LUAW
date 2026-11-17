using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;

namespace SimATC.Core.Flight;

/// <summary>Fases de un vuelo IA en el aire (etapas 6 y 7).</summary>
public enum FlightPhase
{
    Climb,
    Cruise,
    Descent,
    Approach,
    Final,
    Flare,
    Rollout,
    GoAround,
    Holding,
    /// <summary>Aterrizó y llegó a la salida de pista: el motor lo pasa al rodaje hasta el puesto.</summary>
    Landed,
    /// <summary>Terminó lejos del piloto (otro aeropuerto, o salió de la ventana): el motor lo olvida.</summary>
    Finished,
}

/// <summary>
/// Rendimiento en el aire por categoría (RF-19). Todos los valores son APROXIMADOS (órdenes de magnitud de operación de
/// línea, sin fuente única); velocidades IAS salvo el crucero, que es TAS.
/// </summary>
public sealed record AirborneProfile(
    double ClimbFpm,
    double ClimbIasKt,
    double CruiseTasKt,
    double CruiseAltFt,
    double DescentFpm,
    double DescentIasKt,
    double ApproachIasKt,
    double FinalIasKt,
    double ReferenceAglFt)
{
    public static AirborneProfile Turboprop { get; } = new(1800, 170, 270, 22_000, 1500, 230, 160, 115, 8);
    public static AirborneProfile MediumJet { get; } = new(2500, 290, 450, 35_000, 2200, 280, 210, 140, 11);
    public static AirborneProfile HeavyJet { get; } = new(2000, 300, 485, 37_000, 2200, 290, 210, 150, 18);

    public static AirborneProfile For(PerformanceCategory category) => category switch
    {
        PerformanceCategory.Turboprop => Turboprop,
        PerformanceCategory.HeavyJet => HeavyJet,
        _ => MediumJet,
    };
}

/// <summary>Pista de aterrizaje en el aeropuerto cargado: umbral, rumbo, elevación y la salida de pista elegida.</summary>
/// <remarks><see cref="Exits"/>: todas las salidas (distancia desde el umbral, ordenadas), por si aterriza largo.</remarks>
/// <summary>
/// Una salida de pista para una llegada: dónde empieza el viraje (m desde el umbral; la carrera termina ahí), dónde está su
/// nodo en el eje, el ángulo con la pista (30° una salida rápida, 90° una normal), su lado (+1 derecha, −1 izquierda), la
/// velocidad a la que se toma y la entrada del grafo (<see cref="Taxi.TaxiGraph.RunwayEntries"/>).
/// </summary>
public readonly record struct RunwayExit(double TurnStartM, double NodeAlongM, double AngleDeg, int Side, double SpeedKt, int EntryIndex);

public sealed record LandingRunway(int RunwayIndex, bool PrimaryEnd, string Ident, GeoPoint Threshold, double HeadingDeg,
    double ElevationFt, double ExitAlongM, GeoPoint ExitPoint, IReadOnlyList<double>? Exits = null, double LengthM = 3500)
{
    /// <summary>Las salidas utilizables en orden (sin las que vuelven hacia atrás), o null sin grafo.</summary>
    public IReadOnlyList<RunwayExit>? ExitList { get; init; }

    /// <summary>La salida elegida (índice en <see cref="Taxi.TaxiGraph.RunwayEntries"/>), o −1 si no se sabe.</summary>
    public int ExitEntry { get; init; } = -1;

    /// <summary>Velocidad al llegar al inicio del viraje de salida (kt).</summary>
    public double ExitSpeedKt { get; init; } = AirborneAgent.ExitSpeedKt;

    /// <summary>La misma pista con otra salida (ADR-092: la prevista tenía tráfico de frente).</summary>
    public LandingRunway WithExit(RunwayExit e) => With(e);

    private LandingRunway With(RunwayExit e) => this with
    {
        ExitAlongM = e.TurnStartM, ExitPoint = GeoMath.Destination(Threshold, HeadingDeg, e.TurnStartM), ExitEntry = e.EntryIndex,
        ExitSpeedKt = e.SpeedKt,
    };

    /// <summary>
    /// La primera salida a la que llega frenando a <paramref name="decelMs2"/> desde <paramref name="touchdownM"/> (cada
    /// salida con su velocidad: una rápida se toma a 30 kt, una a 90° más despacio). Sin salidas descritas, por distancia.
    /// </summary>
    public LandingRunway ExitAfter(double touchdownM, double speedMs, double decelMs2)
    {
        if (ExitList is { Count: > 0 } list)
        {
            foreach (var e in list)
            {
                var v = e.SpeedKt * 0.514444;
                if (e.TurnStartM >= touchdownM + Math.Max(0, (speedMs * speedMs - v * v) / (2 * decelMs2)))
                    return With(e);
            }
            return With(list[^1]);
        }
        var exitV = ExitSpeedKt * 0.514444;
        var need = touchdownM + Math.Max(0, (speedMs * speedMs - exitV * exitV) / (2 * decelMs2));
        if (ExitAlongM >= need || Exits is null)
            return this;
        foreach (var x in Exits)
        {
            if (x >= need)
                return this with { ExitAlongM = x, ExitPoint = GeoMath.Destination(Threshold, HeadingDeg, x) };
        }
        return Exits.Count > 0 && Exits[^1] > ExitAlongM
            ? this with { ExitAlongM = Exits[^1], ExitPoint = GeoMath.Destination(Threshold, HeadingDeg, Exits[^1]) }
            : this;
    }

    /// <summary>
    /// Deja solo las salidas del lado de <paramref name="p"/> (el puesto: no cruzar la pista ni rodear por el otro lado), si
    /// hay alguna después de <paramref name="minRollM"/>; si no, todas. Elige la primera después del recorrido mínimo.
    /// </summary>
    public LandingRunway TowardSide(GeoPoint p, double minRollM)
    {
        if (ExitList is not { Count: > 0 } list)
            return this;
        var rel = GeoMath.AngleDiffDeg(HeadingDeg, GeoMath.InitialBearingDeg(Threshold, p));
        var side = rel >= 0 ? 1 : -1;
        var same = list.Where(e => e.Side == side).ToList();
        var use = same.Any(e => e.TurnStartM >= minRollM) ? same : [.. list];
        var first = use.FirstOrDefault(e => e.TurnStartM >= minRollM);
        var chosen = first == default ? use[^1] : first;
        return (this with { ExitList = use }).With(chosen);
    }
}

/// <summary>
/// Un vuelo IA en el aire (§5.3): existe siempre en el motor y solo se ve en el simulador cerca del piloto
/// (<see cref="ObjectId"/> distinto de 0). Sigue su <see cref="FlightPath"/> con giros anticipados, sube a crucero, baja
/// con un perfil de ~3° y aterriza por la senda. Avanza en ticks fijos sin asignar memoria.
/// </summary>
public sealed class AirborneAgent
{
    private const double G = 9.80665;
    private const double KtToMs = 0.514444;
    private const double MaxBankDeg = 25;
    private const double MaxTurnRateDegPerS = 3;
    private const double GlideTan = 0.05241; // tan(3°)
    private const double FtPerNmOnGlide = 318.4;
    /// <summary>Altura a la que se cruza el umbral (ft).</summary>
    public const double ThresholdCrossingFt = 50;
    /// <summary>Velocidad con la que deja la pista (kt). APROXIMADO.</summary>
    public const double ExitSpeedKt = 25;
    /// <summary>Distancia al umbral desde la que cuenta como final y pide aterrizar (NM).</summary>
    public const double FinalNm = 10;

    /// <summary>Hasta esta distancia del umbral (NM) se mantienen 160 kt en la final; después, la de aproximación.</summary>
    public const double FinalSpeedGateNm = 4.5;

    /// <summary>Desde esta distancia por la ruta (NM) se baja a 180 kt (base e interceptación). Con 18 NM caía el flujo (RKSI: 26 aterrizajes contra 32).</summary>
    public const double BaseSpeedGateNm = 12;

    private readonly AirborneProfile _perf;
    private readonly double _vapp;

    /// <summary>Velocidad de aproximación final de este tipo (kt IAS).</summary>
    public double VappKt => _vapp;

    /// <summary>
    /// Velocidad de aproximación típica por tipo OACI (Vref + 5 a un peso de aterrizaje medio), en kt. APROXIMADO: valores
    /// redondeados de uso común; sin tipo conocido, la de su categoría. En la vida real depende del peso y del viento.
    /// </summary>
    public static double TypicalVappKt(string type, AirborneProfile perf) => type switch
    {
        "A318" => 125, "A319" or "A19N" => 130, "A320" or "A20N" => 136, "A321" or "A21N" => 142,
        "A332" or "A333" or "A338" or "A339" => 139, "A342" or "A343" => 140, "A345" or "A346" => 150,
        "A359" => 142, "A35K" => 147, "A388" => 140,
        "B712" => 130, "B733" or "B734" or "B735" or "B736" or "B737" => 135, "B738" or "B38M" => 144, "B739" or "B39M" or "B3XM" => 148,
        "B744" => 152, "B748" => 153, "B752" => 135, "B753" => 142, "B762" => 136, "B763" => 142, "B764" => 150,
        "B772" => 140, "B77L" => 145, "B77W" => 149, "B788" => 140, "B789" => 145, "B78X" => 148, "MD11" => 155,
        "E170" => 125, "E75L" or "E75S" or "E175" => 128, "E190" or "E290" => 130, "E195" or "E295" => 135,
        "CRJ2" or "CRJ7" => 135, "CRJ9" => 138, "CRJX" => 140, "BCS1" => 130, "BCS3" => 135, "F100" => 130, "SU95" => 135,
        "AT43" or "AT45" or "AT46" => 105, "AT72" or "AT75" or "AT76" => 113, "DH8C" => 110, "DH8D" => 122,
        _ => perf.FinalIasKt,
    };
    private FlightPath _path;
    private int _target;

    private GeoPoint _prevPos, _pos;
    private double _prevAlt, _alt, _prevHeading, _heading, _prevPitch, _pitch, _prevBank, _bank;
    private double _tasKt;
    private double _vsFpm;
    private double _gearDown;
    private double _flaps;
    private double _phaseTime;
    private double _rolloutM;

    /// <summary>Segundos estimados para que deje libre la pista desde que está en la enderezada o la carrera (frenado hasta la salida y el viraje).</summary>
    public double RunwayClearInS
    {
        get
        {
            if (Landing is not { } l || Phase is not (FlightPhase.Flare or FlightPhase.Rollout))
                return double.PositiveInfinity;
            var v = Math.Max(10, (_tasKt + l.ExitSpeedKt) / 2 * KtToMs);
            return Math.Max(0, l.ExitAlongM - _rolloutM) / v + ExitClearS;
        }
    }

    /// <summary>Del inicio del viraje de salida hasta tener la cola fuera de la pista (s). APROXIMADO.</summary>
    public const double ExitClearS = 22;
    private double _goAroundAltFt;
    private double _holdTurnSign = 1;
    // Espera en circuito (ADR-076): fijo, rumbo de acercamiento, tramo y tiempo en el tramo de alejamiento.
    private GeoPoint _holdFix;
    private double _holdInboundDeg;
    private HoldLeg _holdLeg;
    private double _holdLegS;
    private double _speedCapKt;
    private double _trailTimer;
    private readonly GeoPoint[] _trail = new GeoPoint[400];
    private readonly double[] _trailAlt = new double[400];
    private int _trailCount;
    private readonly Lock _trailLock = new();

    /// <summary>Cada cuánto se guarda un punto de la traza recorrida (s).</summary>
    public const double TrailEveryS = 10;

    public AirborneAgent(string callsign, string airline, string type, string origin, string destination,
        AircraftModel? model, PerformanceCategory category, FlightPath path, GeoPoint position, double altitudeFt,
        double headingDeg, double speedKt, double verticalSpeedFpm, double cruiseAltFt, LandingRunway? landing)
    {
        _vapp = TypicalVappKt(type, AirborneProfile.For(category));
        Callsign = callsign;
        Airline = airline;
        TypeDesignator = type;
        Origin = origin;
        Destination = destination;
        Model = model;
        Category = category;
        _perf = AirborneProfile.For(category);
        _path = path;
        _target = StartIndex(path, position);
        _pos = _prevPos = position;
        _alt = _prevAlt = altitudeFt;
        _heading = _prevHeading = headingDeg;
        _tasKt = speedKt;
        _vsFpm = verticalSpeedFpm;
        CruiseAltFt = cruiseAltFt;
        Landing = landing;
        EndElevationFt = landing?.ElevationFt ?? 0;
        Phase = altitudeFt >= cruiseAltFt - 300 ? FlightPhase.Cruise : verticalSpeedFpm < -300 ? FlightPhase.Descent : FlightPhase.Climb;
        _pitch = _prevPitch = PitchFor(Phase);
        Wake = model?.Wake ?? (category == PerformanceCategory.HeavyJet ? WakeCategory.Heavy : WakeCategory.Medium);
    }

    public string Callsign { get; }
    public string Airline { get; }
    public string TypeDesignator { get; }
    public string Origin { get; }
    public string Destination { get; }
    public AircraftModel? Model { get; }
    public PerformanceCategory Category { get; }
    public WakeCategory Wake { get; }
    public int EngineCount => Model?.EngineCount is > 0 and var n ? n : 2;

    /// <summary>Identificador estable para el radar (no cambia al inyectarlo o retirarlo del simulador).</summary>
    public uint TrackId { get; internal set; }

    // Lo último que se mandó al simulador (luces, motores, gases), para mandar solo los cambios.
    internal LightFlags SentLights;
    internal bool SentEngines;
    internal double SentThrottle = -1;

    /// <summary>Cuándo le dio el ATC la última instrucción de velocidad (s del reloj del motor).</summary>
    internal double LastSpeedCallS = double.NegativeInfinity;

    /// <summary>Hasta dónde llegó su radio con la Torre (0 = nada, 1 = pasado a la Torre). Lo usa el motor.</summary>
    internal int RadioStage;

    /// <summary>Radio en ruta (ADR-089): 0 sin hablar, 1 hizo su primer contacto con el Centro del piloto, 2 recibió descenso.</summary>
    internal int EnrouteStage;

    /// <summary>Objeto en el simulador, o 0 si es virtual (lejos del piloto).</summary>
    public uint ObjectId { get; internal set; }

    /// <summary>Se pidió crearlo en el simulador y todavía no llega su ObjectId.</summary>
    public bool SpawnPending { get; internal set; }

    public bool Injected => ObjectId != 0;
    public FlightPhase Phase { get; private set; }
    public double CruiseAltFt { get; private set; }

    /// <summary>
    /// Llegada al aeropuerto cargado, bajo nuestro ATC (secuencia, Torre, rodaje). false para los que van a otro aeropuerto
    /// con su STAR y aproximación: los sigue el motor sin controlarlos y terminan antes de aterrizar.
    /// </summary>
    public bool ControlledArrival { get; set; }

    /// <summary>Pista de aterrizaje (en el aeropuerto cargado o en su destino), o null si va directo.</summary>
    public LandingRunway? Landing { get; private set; }

    /// <summary>Elevación del final de la ruta (umbral de la pista o aeropuerto de destino).</summary>
    public double EndElevationFt { get; set; }

    /// <summary>
    /// Elevación real del umbral en el simulador (de las salidas alineadas en esa cabecera o de las muestras de altura de
    /// este avión sobre la pista); NaN si no se sabe y se usa la de los datos del aeropuerto.
    /// </summary>
    public double LearnedRunwayFt { get; set; } = double.NaN;

    private double RunwayFt => double.IsNaN(LearnedRunwayFt) ? EndElevationFt : LearnedRunwayFt;

    /// <summary>Altura de las ruedas sobre la pista (ft): la del punto de referencia menos su altura sobre el suelo.</summary>
    private double WheelsAglFt => _alt - RunwayFt - _perf.ReferenceAglFt;

    /// <summary>Muestra de altura del simulador (MSL y AGL): sobre la pista da su elevación real.</summary>
    public void OnAltitudeSample(double altitudeFt, double aglFt)
    {
        if (Landing is not { } l || Phase is not (FlightPhase.Final or FlightPhase.Flare))
            return;
        var along = AlongFromThreshold(l);
        var ground = altitudeFt - aglFt;
        // Solo sobre la pista (o justo antes): el terreno de la aproximación no es la pista.
        if (along > -250 && Math.Abs(ground - l.ElevationFt) < 150)
            LearnedRunwayFt = double.IsNaN(LearnedRunwayFt) ? ground : LearnedRunwayFt + (ground - LearnedRunwayFt) * 0.5;
    }

    /// <summary>Altitud que debería tener aquí según su perfil (restricciones de la carta y senda), o NaN si no tiene.</summary>
    public double ProfileAltitudeFt
    {
        get
        {
            if (!_path.HasProfile || _path.Count == 0)
                return double.NaN;
            var t = _path[_target];
            var offset = RunwayFt - EndElevationFt + _perf.ReferenceAglFt;
            if (_target == 0)
                return t.ProfileFt + offset;
            var prev = _path[_target - 1];
            var seg = Math.Max(1, prev.RemainingM - t.RemainingM);
            var d = GeoMath.DistanceM(_pos, t.Position);
            return t.ProfileFt + (prev.ProfileFt - t.ProfileFt) * Math.Clamp(d / seg, 0, 1) + offset;
        }
    }

    /// <summary>Límite de velocidad de la carta que tiene ahora (kt IAS), o 0.</summary>
    public double ChartSpeedLimitKt => _speedCapKt;

    /// <summary>
    /// Altitud del perfil a <paramref name="aheadNm"/> NM por delante (el nivel que pedirá el ATC), y la mayor altitud
    /// mínima de la carta en ese tramo (no se autoriza bajo ella). NaN sin perfil.
    /// </summary>
    public (double ProfileFt, double FloorFt) ProfileAhead(double aheadNm)
    {
        if (!_path.HasProfile)
            return (double.NaN, 0);
        var here = RemainingM;
        var floor = 0.0;
        var offset = RunwayFt - EndElevationFt + _perf.ReferenceAglFt;
        for (var j = _target; j < _path.Count; j++)
        {
            var p = _path[j];
            floor = Math.Max(floor, p.MinAltFt);
            if (here - p.RemainingM >= aheadNm * UnitConversions.MetersPerNauticalMile || j == _path.Count - 1)
                return (p.ProfileFt + offset, floor);
        }
        return (double.NaN, floor);
    }

    /// <summary>Índice del punto de la ruta al que va.</summary>
    public int TargetIndex => _target;

    /// <summary>
    /// Punto de la ruta al que vuela al crearse (el siguiente al punto del plan donde aparece, ADR-062). Sin esto se busca el
    /// más cercano por delante.
    /// </summary>
    public int StartTarget
    {
        init => _target = Math.Clamp(value, 0, Math.Max(0, _path.Count - 1));
    }

    /// <summary>La traza recorrida (posición y altitud cada <see cref="TrailEveryS"/> s), copiada para la interfaz.</summary>
    public (GeoPoint[] Points, double[] Altitudes) Trail()
    {
        lock (_trailLock)
            return (_trail[.._trailCount], _trailAlt[.._trailCount]);
    }

    private void RecordTrail(double dt)
    {
        _trailTimer += dt;
        if (_trailTimer < TrailEveryS && _trailCount > 0)
            return;
        _trailTimer = 0;
        lock (_trailLock)
        {
            if (_trailCount == _trail.Length)
            {
                // Lleno: se descarta la mitad más vieja (sin asignar).
                Array.Copy(_trail, _trail.Length / 2, _trail, 0, _trail.Length / 2);
                Array.Copy(_trailAlt, _trailAlt.Length / 2, _trailAlt, 0, _trailAlt.Length / 2);
                _trailCount = _trail.Length / 2;
            }
            _trail[_trailCount] = _pos;
            _trailAlt[_trailCount] = _alt;
            _trailCount++;
        }
    }

    /// <summary>Puesto reservado para la llegada (índice en el aeropuerto), o -1.</summary>
    public int StandIndex { get; set; } = -1;

    /// <summary>
    /// Nivel autorizado por el ATC (ft): una llegada no baja de aquí aunque su perfil lo pida. NaN = sin límite (vuelos sin
    /// control de LUAW o ya autorizados a la aproximación, que siguen las restricciones de la carta).
    /// </summary>
    public double ClearedAltitudeFt { get; set; } = double.NaN;

    /// <summary>Autorizado a la aproximación (sigue las altitudes de la carta).</summary>
    public bool ApproachCleared { get; set; }

    /// <summary>Hay una autorización por radio en curso (no se pide otra hasta que termine la colación).</summary>
    public bool ClearancePending { get; set; }

    /// <summary>Tower autorizó el aterrizaje (si no lo autoriza antes de 1 NM, hace "go around").</summary>
    public bool LandingCleared { get; set; }

    /// <summary>Velocidad asignada por el ATC (kt IAS, dicha por radio), o 0 sin asignación. Es la que el avión respeta.</summary>
    public double AssignedSpeedKt { get; set; }

    /// <summary>Velocidad que la secuencia querría (kt IAS) o 0; el ATC la asigna por radio (<see cref="AssignedSpeedKt"/>).</summary>
    public double SequenceSpeedKt { get; set; }

    /// <summary>
    /// TAS máxima (kt) para no alcanzar al de adelante en la ruta (separación en ruta, <see cref="EnrouteSpacing"/>), o 0.
    /// Vale en crucero y descenso.
    /// </summary>
    public double TrailTasKt { get; set; }

    /// <summary>Lo que <see cref="EnrouteSpacing"/> calcula en esta vuelta (para aplicarlo de una vez).</summary>
    internal double NextTrailTasKt;

    /// <summary>Cuántas veces dio motor y al aire (para el log y los escenarios).</summary>
    public int GoArounds { get; private set; }

    public GeoPoint Position => _pos;
    public double AltitudeFt => _alt;
    public double HeadingDeg => _heading;
    public double SpeedKt => _tasKt;
    public double VerticalSpeedFpm => _vsFpm;
    public double AglFt => _alt - EndElevationFt;

    /// <summary>Elevación del aeropuerto de salida (para la altura sobre la pista en el ascenso inicial).</summary>
    public double OriginElevationFt { get; init; }
    public FlightPath Path => _path;
    public bool OnGround => Phase is FlightPhase.Rollout or FlightPhase.Landed;

    /// <summary>Distancia que le queda por la ruta hasta su final (m).</summary>
    public double RemainingM => _path.Count == 0 ? 0
        : GeoMath.DistanceM(_pos, _path[_target].Position) + _path[_target].RemainingM;

    public double RemainingNm => RemainingM / UnitConversions.MetersPerNauticalMile;

    /// <summary>Segundos estimados hasta el umbral (o el final de la ruta).</summary>
    public double SecondsToEnd => RemainingM / Math.Max(1, _tasKt * KtToMs);

    public PathSection Section => _path.Count == 0 ? PathSection.Enroute : _path[_target].Section;

    public LightFlags Lights => Phase switch
    {
        FlightPhase.Rollout or FlightPhase.Landed => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe | LightFlags.Landing | LightFlags.Taxi,
        _ when _alt - EndElevationFt > 10_000 && _alt > 10_000 => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe,
        _ => LightFlags.Nav | LightFlags.Beacon | LightFlags.Strobe | LightFlags.Landing,
    };

    public double ThrottlePercent => Phase switch
    {
        FlightPhase.Climb or FlightPhase.GoAround => 85,
        FlightPhase.Cruise => 70,
        FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Flare => 5,
        _ => _vsFpm < -1200 ? 20 : 45,
    };

    /// <summary>Cambia la ruta (motor y al aire, desvíos). Retoma por el punto más cercano por delante.</summary>
    public void SetPath(FlightPath path)
    {
        _path = path;
        _target = StartIndex(path, _pos);
    }

    /// <summary>
    /// Por dónde empieza a volar una ruta: si la ruta parte de donde está el avión (las que arma LUAW), desde su primer
    /// punto, en orden; si no, el más cercano por delante. Antes se buscaba siempre el más cercano y una ruta podía saltar
    /// a la final del otro lado del aeropuerto (prueba de Marco: "se salta puntos y da una vuelta entera").
    /// </summary>
    private static int StartIndex(FlightPath path, GeoPoint position)
    {
        if (path.Count < 2)
            return 0;
        return GeoMath.DistanceM(path[0].Position, position) < 2000 ? 1 : path.NearestAhead(position);
    }

    /// <summary>Asigna la pista de aterrizaje y la ruta de llegada (al entrar al área del aeropuerto cargado).</summary>
    public void SetArrival(FlightPath path, LandingRunway landing)
    {
        Landing = landing;
        EndElevationFt = landing.ElevationFt;
        SetPath(path);
    }

    /// <summary>El fijo de la espera ("UMKAL"), o vacío si espera sobre un punto sin nombre.</summary>
    public string HoldFixName { get; private set; } = "";

    /// <summary>Tramos de la espera en circuito (OACI Doc 8168: virajes de régimen 1 y alejamiento de 1 o 1,5 min).</summary>
    private enum HoldLeg { Inbound, TurnOutbound, Outbound, TurnInbound }

    /// <summary>Tramo de alejamiento (s): 1 min hasta 14 000 ft, 1,5 min por encima (OACI Doc 8168).</summary>
    public double HoldOutboundS => _alt <= 14_000 ? 60 : 90;

    /// <summary>
    /// Entra en espera (ADR-076) hasta <see cref="ResumeFromHolding"/>: circuito con virajes a la derecha sobre el próximo
    /// fijo de su ruta (si está a menos de 30 NM; si no, sobre un punto 5 NM adelante), con el rumbo de acercamiento de la
    /// ruta hacia ese fijo, a la altitud actual. Entrada directa (vuela al fijo y empieza el circuito).
    /// </summary>
    public void Hold()
    {
        if (Phase is FlightPhase.Final or FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Holding)
            return;
        _holdTurnSign = 1; // a la derecha: espera estándar
        HoldFixName = "";
        _holdFix = GeoMath.Destination(_pos, _heading, 5 * UnitConversions.MetersPerNauticalMile);
        for (var j = _target; j < _path.Count; j++)
        {
            var p = _path[j];
            if (GeoMath.DistanceM(_pos, p.Position) > 30 * UnitConversions.MetersPerNauticalMile)
                break;
            if (p.IsFix && p.Section != PathSection.Final)
            {
                _holdFix = p.Position;
                HoldFixName = p.Name;
                break;
            }
        }
        _holdInboundDeg = GeoMath.InitialBearingDeg(_pos, _holdFix);
        _holdLeg = HoldLeg.Inbound;
        _holdLegS = 0;
        Next(FlightPhase.Holding);
    }

    /// <summary>Rumbo deseado en la espera: al fijo, viraje de alejamiento, alejamiento por tiempo y viraje de acercamiento.</summary>
    private double HoldingHeading(double dt)
    {
        var toFix = GeoMath.InitialBearingDeg(_pos, _holdFix);
        var outbound = GeoMath.NormalizeDeg(_holdInboundDeg + 180);
        switch (_holdLeg)
        {
            case HoldLeg.Inbound:
                if (GeoMath.DistanceM(_pos, _holdFix) < 0.5 * UnitConversions.MetersPerNauticalMile
                    || Math.Abs(GeoMath.AngleDiffDeg(_heading, toFix)) > 100)
                    _holdLeg = HoldLeg.TurnOutbound;
                return toFix;
            case HoldLeg.TurnOutbound:
                if (Math.Abs(GeoMath.AngleDiffDeg(_heading, outbound)) < 10)
                {
                    _holdLeg = HoldLeg.Outbound;
                    _holdLegS = 0;
                    return outbound;
                }
                return GeoMath.NormalizeDeg(_heading + _holdTurnSign * 30);
            case HoldLeg.Outbound:
                _holdLegS += dt;
                if (_holdLegS >= HoldOutboundS)
                    _holdLeg = HoldLeg.TurnInbound;
                return outbound;
            default:
                if (Math.Abs(GeoMath.AngleDiffDeg(_heading, toFix)) < 20)
                {
                    _holdLeg = HoldLeg.Inbound;
                    return toFix;
                }
                return GeoMath.NormalizeDeg(_heading + _holdTurnSign * 30);
        }
    }

    /// <summary>
    /// Nueva llegada en vuelo (ADR-082: cambio de pista en uso o llegadas paralelas): otra pista y la ruta nueva (STAR y
    /// aproximación) desde donde está; sigue volando sin saltos (empieza por el punto de la ruta nueva más cercano por delante).
    /// No en final, enderezamiento, carrera ni en tierra.
    /// </summary>
    public bool Reroute(FlightPath path, LandingRunway landing)
    {
        if (Phase is FlightPhase.Final or FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Finished
            or FlightPhase.GoAround or FlightPhase.Climb || path.Count == 0)
            return false;
        _path = path;
        _target = path.NearestAhead(_pos);
        Landing = landing;
        ApproachCleared = false;
        LandingCleared = false;
        if (Phase is FlightPhase.Holding or FlightPhase.Approach)
            Next(FlightPhase.Descent);
        return true;
    }

    /// <summary>
    /// ADR-085: un vuelo que va al aeropuerto que acaba de quedar activo pasa a ser llegada controlada (secuencia, Torre,
    /// salida de pista y rodaje a <paramref name="stand"/>), por la ruta nueva desde donde está. Mantiene su nivel hasta
    /// que Approach lo baje, como las llegadas creadas por el director.
    /// </summary>
    public bool Adopt(FlightPath path, LandingRunway landing, int stand)
    {
        if (ControlledArrival || !Reroute(path, landing))
            return false;
        ControlledArrival = true;
        StandIndex = stand;
        RadioStage = 0;
        ClearedAltitudeFt = Math.Floor(_alt / 1000) * 1000;
        return true;
    }

    /// <summary>
    /// ADR-088/089: un vuelo cuya ruta ya termina en una pista en uso del aeropuerto que acaba de quedar activo pasa a ser
    /// llegada controlada sin cambiar su ruta (su STAR y su aproximación; una ruta nueva desde ahí lo desviaba, por DAMIP en
    /// SCIE, o daba una vuelta de 35 NM en final): solo toma los datos de esa misma pista (salidas) y el puesto. Lejos de la
    /// pista mantiene su nivel hasta que Approach lo baje, como las demás llegadas.
    /// </summary>
    public bool AdoptOnFinal(LandingRunway landing, int stand)
    {
        if (ControlledArrival || Landing is not { } current || !string.Equals(current.Ident, landing.Ident, StringComparison.OrdinalIgnoreCase)
            || Phase is FlightPhase.Flare or FlightPhase.Rollout or FlightPhase.Landed or FlightPhase.Finished or FlightPhase.GoAround
                or FlightPhase.Climb)
            return false;
        Landing = landing;
        ControlledArrival = true;
        StandIndex = stand;
        RadioStage = 0;
        if (RemainingNm > HoldLevelBeyondNm && Phase is FlightPhase.Cruise or FlightPhase.Descent)
            ClearedAltitudeFt = Math.Floor(_alt / 1000) * 1000;
        return true;
    }

    /// <summary>Al adoptarlo más lejos que esto (NM) mantiene su nivel hasta que Approach lo baje. APROXIMADO.</summary>
    public const double HoldLevelBeyondNm = 40;

    /// <summary>
    /// ADR-085: llegada a un aeropuerto que dejó de estar activo: sigue volando sin ATC (nadie la secuencia ni le habla) y
    /// termina lejos del piloto como los vuelos de paso.
    /// </summary>
    public void Release()
    {
        ControlledArrival = false;
        StandIndex = -1;
        ClearancePending = false;
        ClearedAltitudeFt = double.NaN;
        if (Phase == FlightPhase.Holding)
            ResumeFromHolding();
    }

    /// <summary>
    /// Terminado pero todavía a la vista del piloto (va a un aeropuerto sin nuestro control): en vez de quedar congelado en
    /// el aire, sigue recto y sube a 3000 ft sobre el final de su ruta hasta que el motor lo retire al alejarse (ADR-085).
    /// </summary>
    private void Coast(double dt)
    {
        var targetFt = EndElevationFt + 3000;
        _vsFpm = _alt < targetFt - 50 ? 1500 : 0;
        _alt = Math.Min(targetFt, _alt + _vsFpm / 60 * dt);
        _tasKt = Math.Max(_tasKt, 160);
        _pos = GeoMath.Destination(_pos, _heading, _tasKt * KtToMs * dt);
    }

    public void ResumeFromHolding()
    {
        if (Phase != FlightPhase.Holding)
            return;
        _target = _path.NearestAhead(_pos, Math.Max(0, _target - 1));
        Next(FlightPhase.Descent);
    }

    /// <summary>Motor y al aire (RF-28 de los IA, etapa 7): sube a <paramref name="altitudeFt"/> por la nueva ruta.</summary>
    public void GoAround(FlightPath path, double altitudeFt)
    {
        GoArounds++;
        LandingCleared = false;
        _goAroundAltFt = altitudeFt;
        _path = path;
        _target = 0;
        Next(FlightPhase.GoAround);
    }

    /// <summary>Un tick (s).</summary>
    public void Step(double dt)
    {
        _prevPos = _pos;
        _prevAlt = _alt;
        _prevHeading = _heading;
        _prevPitch = _pitch;
        _prevBank = _bank;
        _phaseTime += dt;
        RecordTrail(dt);

        if (Phase is FlightPhase.Rollout)
        {
            StepRollout(dt);
            return;
        }
        if (Phase is FlightPhase.Landed)
            return;
        if (Phase is FlightPhase.Finished)
        {
            Coast(dt);
            return;
        }

        var remainingNm = RemainingNm;
        UpdateChartSpeed();
        UpdatePhase(remainingNm);
        UpdateVertical(dt, remainingNm);
        UpdateSpeed(dt, remainingNm);
        UpdateLateral(dt);
        UpdateConfiguration(dt, remainingNm);
        var targetPitch = PitchFor(Phase);
        _pitch += Math.Clamp(targetPitch - _pitch, -1.5 * dt, 1.5 * dt);
    }

    private void UpdatePhase(double remainingNm)
    {
        var endAglFt = _alt - EndElevationFt;
        switch (Phase)
        {
            case FlightPhase.Climb:
                if (_alt >= CruiseAltFt - 50)
                {
                    _alt = CruiseAltFt;
                    Next(FlightPhase.Cruise);
                }
                else if (TopOfDescent(remainingNm))
                {
                    Next(FlightPhase.Descent);
                }
                break;
            case FlightPhase.Cruise:
                if (TopOfDescent(remainingNm)
                    || Landing is not null && remainingNm < Math.Max(0, _alt - EndElevationFt) / 300 + 25)
                    Next(FlightPhase.Descent);
                break;
            case FlightPhase.Descent:
                if (Section is PathSection.Approach or PathSection.Final || (Landing is not null && remainingNm < 25))
                    Next(FlightPhase.Approach);
                break;
            case FlightPhase.Approach:
                if (Landing is not null && Section == PathSection.Final && remainingNm <= FinalNm)
                    Next(FlightPhase.Final);
                break;
            case FlightPhase.Final:
                if (Landing is { } l)
                {
                    // Enderezada sobre el umbral; si llegó alto, igual a los 300 m (aterrizaje largo).
                    var along = AlongFromThreshold(l);
                    if (along > -350 && WheelsAglFt < 30)
                        Next(FlightPhase.Flare);
                    else if (!Stable(l, along))
                        GoAroundSelf("no estabilizado");
                }
                break;
            case FlightPhase.Flare:
                if (Landing is { } fl && WheelsAglFt > 0.5 && AlongFromThreshold(fl) > Math.Min(900, fl.ExitAlongM / 2))
                {
                    GoAroundSelf("aterrizaje largo");
                    break;
                }
                if (Landing is { } t && WheelsAglFt <= 0.5)
                {
                    _alt = RunwayFt + _perf.ReferenceAglFt;
                    _vsFpm = 0;
                    _rolloutM = Math.Max(0, AlongFromThreshold(t));
                    // Si aterrizó largo, toma una salida más adelante.
                    Landing = t.ExitAfter(_rolloutM, _tasKt * KtToMs, 1.6);
                    Next(FlightPhase.Rollout);
                }
                break;
            case FlightPhase.GoAround:
                if (_alt >= _goAroundAltFt - 50)
                {
                    _alt = _goAroundAltFt;
                    Next(FlightPhase.Approach);
                }
                break;
        }

        // Sin pista en el aeropuerto cargado: termina al llegar a su destino (otro aeropuerto, lejos del piloto).
        if (!ControlledArrival && Phase is FlightPhase.Descent or FlightPhase.Approach or FlightPhase.Final && remainingNm < 3 && endAglFt < 3500)
            Next(FlightPhase.Finished);
    }

    private bool TopOfDescent(double remainingNm)
    {
        if (_path.HasProfile)
        {
            var profile = ProfileAltitudeFt;
            return profile < CruiseAltFt - 200 && _alt > profile + 300;
        }
        var drop = _alt - (EndElevationFt + 1500);
        // ~3° (300 ft/NM) más 10 NM para bajar la velocidad.
        return drop > 0 && remainingNm <= drop / 300 + 10;
    }

    private void UpdateVertical(double dt, double remainingNm)
    {
        double targetVs;
        switch (Phase)
        {
            case FlightPhase.Climb:
                // Restricción "at or below" de la SID: se nivela hasta pasar ese punto.
                var cap = SidAltitudeCapFt();
                targetVs = _alt >= cap - 50 ? 0 : _perf.ClimbFpm * Math.Max(0.35, 1 - _alt / 45_000);
                if (_alt > cap)
                    _alt = Math.Max(cap, _alt - 1);
                break;
            case FlightPhase.GoAround:
                targetVs = 2000;
                break;
            case FlightPhase.Cruise:
            case FlightPhase.Holding:
                targetVs = 0;
                break;
            case FlightPhase.Final:
            {
                // Senda de 3° hasta cruzar el umbral a 50 ft (distancia por el eje, no por la ruta).
                var toThresholdNm = Landing is { } fl ? Math.Max(0, -AlongFromThreshold(fl)) / UnitConversions.MetersPerNauticalMile : remainingNm;
                // Las ruedas cruzan el umbral a 50 ft (punto de toma ≈ 300 m adentro, donde apunta el ILS y el PAPI).
                var glide = RunwayFt + _perf.ReferenceAglFt + ThresholdCrossingFt + toThresholdNm * FlightPlanner.GlideFtPerNm(_path.GlideDeg);
                var nominal = -_tasKt * 101.27 * Math.Tan(_path.GlideDeg * Math.PI / 180);
                targetVs = Math.Clamp(nominal + Math.Clamp((glide - _alt) * 4, -500, 500), -1500, 300);
                break;
            }
            case FlightPhase.Flare:
                targetVs = -200;
                break;
            default:
            {
                // Descenso y aproximación: perfil de ~3° hasta el final de la ruta; nunca por debajo de 1500 ft sobre el
                // destino antes de la final. Si está bajo el perfil, se mantiene nivelado.
                var desired = Math.Min(CruiseAltFt, EndElevationFt + 1500 + Math.Max(0, remainingNm - 5) * 300);
                if (Phase == FlightPhase.Approach && Section == PathSection.Final)
                    desired = Math.Max(EndElevationFt + ThresholdCrossingFt + remainingNm * FtPerNmOnGlide, EndElevationFt + 1000);
                // Con perfil (restricciones de la STAR y la aproximación): se sigue el perfil.
                if (_path.HasProfile && Phase is FlightPhase.Descent or FlightPhase.Approach)
                    desired = Math.Min(CruiseAltFt, ProfileAltitudeFt);
                // Nunca bajo lo autorizado por el ATC.
                if (!double.IsNaN(ClearedAltitudeFt))
                    desired = Math.Max(desired, ClearedAltitudeFt);
                targetVs = _alt > desired + 100 ? -Math.Min(_perf.DescentFpm, (_alt - desired) * 2) : 0;
                break;
            }
        }
        _vsFpm += Math.Clamp(targetVs - _vsFpm, -600 * dt, 600 * dt);
        _alt += _vsFpm / 60 * dt;
        if (Phase == FlightPhase.Climb && _alt > CruiseAltFt)
            _alt = CruiseAltFt;
    }

    private void UpdateSpeed(double dt, double remainingNm)
    {
        double ias;
        switch (Phase)
        {
            case FlightPhase.Climb:
                ias = _alt < 10_000 ? 250 : _perf.ClimbIasKt;
                break;
            case FlightPhase.Cruise:
                // En crucero se vuela a TAS (≈ Mach constante).
                var cruiseTas = _alt < 10_000 ? Math.Min(_perf.CruiseTasKt, 250 * (1 + 0.02 * Math.Max(0, _alt) / 1000)) : _perf.CruiseTasKt;
                if (TrailTasKt > 0)
                    cruiseTas = Math.Min(cruiseTas, TrailTasKt);
                _tasKt += Math.Clamp(cruiseTas - _tasKt, -1.5 * dt, 1.5 * dt);
                return;
            case FlightPhase.GoAround:
                ias = 180;
                break;
            case FlightPhase.Holding:
                ias = Math.Min(230, _alt < 10_000 ? 220 : 230);
                break;
            // Como en la práctica (control de velocidad OACI, "160 kt hasta 4 DME"): 210 kt lejos, 180 kt en base e
            // interceptación, 160 kt en la final hasta ~4,5 NM y de ahí la velocidad de aproximación de su tipo. Manda la
            // carta si publica menos (_speedCapKt) y el ATC si asigna otra. APROXIMADO.
            case FlightPhase.Final or FlightPhase.Flare:
                ias = remainingNm > FinalSpeedGateNm ? Math.Max(_vapp, Math.Min(160, _perf.ApproachIasKt)) : _vapp;
                break;
            case FlightPhase.Approach:
                ias = remainingNm < BaseSpeedGateNm ? Math.Min(_perf.ApproachIasKt, 180) : _perf.ApproachIasKt;
                break;
            default:
                ias = _alt < 10_000 ? 250 : _perf.DescentIasKt;
                break;
        }
        if (AssignedSpeedKt > 0 && Phase is not (FlightPhase.Climb or FlightPhase.GoAround or FlightPhase.Final or FlightPhase.Flare))
            ias = Math.Max(_vapp, Math.Min(ias, AssignedSpeedKt));
        if (_speedCapKt > 0 && Phase is not (FlightPhase.Cruise or FlightPhase.GoAround))
            ias = Math.Max(_vapp, Math.Min(ias, _speedCapKt));
        // Bajo 10.000 ft, 250 kt como máximo (salvo que su velocidad mínima sea mayor).
        if (_alt < 10_000)
            ias = Math.Min(ias, Math.Max(250, _vapp));
        var tas = ias * (1 + 0.02 * Math.Max(0, _alt) / 1000);
        if (TrailTasKt > 0 && Phase == FlightPhase.Descent)
            tas = Math.Max(Math.Min(tas, TrailTasKt), _vapp * (1 + 0.02 * Math.Max(0, _alt) / 1000));
        _tasKt += Math.Clamp(tas - _tasKt, -1.2 * dt, 1.5 * dt);
    }

    private void UpdateLateral(double dt)
    {
        var v = Math.Max(60, _tasKt) * KtToMs;
        double desired;
        if (Phase == FlightPhase.Holding)
        {
            // Circuito de espera sobre el fijo (virajes de régimen 1, 3°/s).
            desired = HoldingHeading(dt);
        }
        else if (Phase is FlightPhase.Final or FlightPhase.Flare && Landing is { } l)
        {
            AdvanceTarget(v);
            desired = CenterlineHeading(l, 1500);
        }
        else
        {
            AdvanceTarget(v);
            desired = GeoMath.InitialBearingDeg(_pos, _path[_target].Position);
        }

        var maxRate = Math.Min(MaxTurnRateDegPerS, G * Math.Tan(MaxBankDeg * Math.PI / 180) / v * 180 / Math.PI);
        var diff = GeoMath.AngleDiffDeg(_heading, desired);
        var rate = Math.Clamp(diff * 0.6, -maxRate, maxRate);
        _heading = GeoMath.NormalizeDeg(_heading + rate * dt);
        var bank = Math.Atan(v * rate * Math.PI / 180 / G) * 180 / Math.PI;
        _bank += Math.Clamp(bank - _bank, -5 * dt, 5 * dt);
        _pos = GeoMath.Destination(_pos, _heading, v * dt);
    }

    /// <summary>
    /// Límite de velocidad de la carta: en la SID rige hasta el punto que lo tiene; en la STAR y la aproximación, desde
    /// ese punto en adelante (y se anticipa 10 NM antes para llegar ya frenado).
    /// </summary>
    private void UpdateChartSpeed()
    {
        var here = RemainingM;
        var cap = 0.0;
        for (var j = _target; j < _path.Count; j++)
        {
            var p = _path[j];
            if (here - p.RemainingM > 10 * UnitConversions.MetersPerNauticalMile && p.Section != PathSection.Sid)
                break;
            if (p.SpeedMaxKt > 0)
            {
                cap = cap == 0 ? p.SpeedMaxKt : Math.Min(cap, p.SpeedMaxKt);
                if (p.Section != PathSection.Sid)
                    break;
            }
            if (p.Section != PathSection.Sid && _path[_target].Section == PathSection.Sid)
                break;
        }
        // Los ya pasados en la STAR y la aproximación siguen valiendo.
        for (var j = _target - 1; j >= 0; j--)
        {
            var p = _path[j];
            if (p.Section is PathSection.Sid or PathSection.Enroute)
                break;
            if (p.SpeedMaxKt > 0)
            {
                cap = cap == 0 ? p.SpeedMaxKt : Math.Min(cap, p.SpeedMaxKt);
                break;
            }
        }
        _speedCapKt = cap;
    }

    /// <summary>Tope de ascenso por la SID: la próxima restricción "at or below" por delante (o el crucero).</summary>
    private double SidAltitudeCapFt()
    {
        for (var j = _target; j < _path.Count && _path[j].Section == PathSection.Sid; j++)
        {
            if (_path[j].MaxAltFt > 0)
                return Math.Min(CruiseAltFt, _path[j].MaxAltFt);
        }
        return CruiseAltFt;
    }

    /// <summary>Pasa al siguiente punto cuando corresponde girar (anticipando el viraje) o si el punto quedó atrás.</summary>
    private void AdvanceTarget(double v)
    {
        var radius = v * v / (G * Math.Tan(MaxBankDeg * Math.PI / 180));
        while (_target < _path.Count - 1)
        {
            var p = _path[_target].Position;
            var d = GeoMath.DistanceM(_pos, p);
            var toPoint = GeoMath.InitialBearingDeg(_pos, p);
            var next = GeoMath.InitialBearingDeg(p, _path[_target + 1].Position);
            var turn = Math.Abs(GeoMath.AngleDiffDeg(toPoint, next));
            var lead = Math.Min(radius * Math.Tan(Math.Min(turn, 150) * Math.PI / 360), 5 * UnitConversions.MetersPerNauticalMile);
            // Un punto de costado dentro del radio de giro no se alcanza sin dar una vuelta completa: se da por pasado.
            var off = Math.Abs(GeoMath.AngleDiffDeg(_heading, toPoint));
            var behind = off > 100 && d < 2 * radius || off > 50 && d < 1.2 * radius;
            if (d <= Math.Max(lead, 200) || behind)
                _target++;
            else
                break;
        }
    }

    /// <summary>Rumbo para mantenerse sobre el eje de la pista (persecución a <paramref name="lookAheadM"/> por delante).</summary>
    private double CenterlineHeading(LandingRunway l, double lookAheadM)
    {
        var along = AlongFromThreshold(l);
        var aim = GeoMath.Destination(l.Threshold, l.HeadingDeg, along + lookAheadM);
        return GeoMath.InitialBearingDeg(_pos, aim);
    }

    /// <summary>Distancia a lo largo del eje desde el umbral (negativa antes del umbral), en metros.</summary>
    private double AlongFromThreshold(LandingRunway l)
    {
        var d = GeoMath.DistanceM(l.Threshold, _pos);
        if (d < 1)
            return 0;
        var b = GeoMath.InitialBearingDeg(l.Threshold, _pos);
        return d * Math.Cos(GeoMath.AngleDiffDeg(l.HeadingDeg, b) * Math.PI / 180);
    }

    /// <summary>
    /// ¿La calle de esa salida está ocupada por alguien de frente o detenido? La fija el motor (ADR-092: en LEMD las
    /// llegadas a 32R salían por una calle donde venía otro de frente y quedaban trabadas).
    /// </summary>
    public Func<AirborneAgent, LandingRunway, RunwayExit, bool>? ExitBlocked { get; set; }

    private int _exitChecked = -1;

    /// <summary>A esta distancia (m) de su salida, la llegada mira si la calle está libre; si no, sigue a la siguiente.</summary>
    public const double ExitCheckM = 500;

    private void CheckExit(LandingRunway l)
    {
        if (ExitBlocked is not { } blocked || l.ExitList is not { Count: > 0 } list || l.ExitEntry == _exitChecked
            || l.ExitAlongM - _rolloutM > ExitCheckM)
            return;
        _exitChecked = l.ExitEntry;
        var cur = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].EntryIndex == l.ExitEntry)
                cur = i;
        }
        if (cur < 0 || !blocked(this, l, list[cur]))
            return;
        for (var k = cur + 1; k < list.Count; k++)
        {
            if (list[k].TurnStartM > _rolloutM + 50 && !blocked(this, l, list[k]))
            {
                Landing = l.WithExit(list[k]);
                _exitChecked = list[k].EntryIndex;
                return;
            }
        }
    }

    private void StepRollout(double dt)
    {
        CheckExit(Landing!);
        var l = Landing!;
        var v = _tasKt * KtToMs;
        var exitV = l.ExitSpeedKt * KtToMs;
        var toExit = l.ExitAlongM - _rolloutM;
        // Frenado para llegar a la salida a la velocidad de salida (entre 0,5 y 2,2 m/s², reversores y autobrake).
        var decel = toExit > 1 ? Math.Clamp((v * v - exitV * exitV) / (2 * toExit), 0.5, 2.2) : 2.2;
        if (_phaseTime < 2)
            decel = 0.5; // apoyo de la nariz
        v = Math.Max(exitV, v - decel * dt);
        _tasKt = v / KtToMs;
        _rolloutM += v * dt;
        _pos = GeoMath.Destination(l.Threshold, l.HeadingDeg, _rolloutM);
        _heading = l.HeadingDeg;
        _bank = 0;
        _pitch = Math.Max(0, _pitch - 1.5 * dt);
        _flaps = Math.Max(0, _flaps - 4 * dt);
        if (_rolloutM >= l.ExitAlongM)
            Next(FlightPhase.Landed);
    }

    private void UpdateConfiguration(double dt, double remainingNm)
    {
        var landingSoon = Landing is not null && Phase is FlightPhase.Final or FlightPhase.Flare;
        var gearTarget = landingSoon && remainingNm < 8 || Phase == FlightPhase.Climb && _alt - OriginElevationFt < 400 ? 1.0 : 0.0;
        if (Phase == FlightPhase.GoAround && _phaseTime > 4)
            gearTarget = 0;
        _gearDown += Math.Clamp(gearTarget - _gearDown, -dt / DepartureAgent.GearCycleS, dt / DepartureAgent.GearCycleS);
        var flapsTarget = Phase switch
        {
            FlightPhase.Final or FlightPhase.Flare => remainingNm < 5 ? 100 : 60,
            FlightPhase.Approach when remainingNm < 18 => 30,
            FlightPhase.GoAround => 40,
            FlightPhase.Climb when _alt - OriginElevationFt < DepartureAgent.FlapsRetractAglFt => 25,
            _ => 0,
        };
        _flaps += Math.Clamp(flapsTarget - _flaps, -DepartureAgent.FlapsRatePercentPerS * dt, DepartureAgent.FlapsRatePercentPerS * dt);
    }

    private static double PitchFor(FlightPhase phase) => phase switch
    {
        FlightPhase.Climb or FlightPhase.GoAround => 8,
        FlightPhase.Cruise or FlightPhase.Holding => 2.5,
        FlightPhase.Descent => 0.5,
        FlightPhase.Approach => 3,
        FlightPhase.Final => 2.5,
        FlightPhase.Flare => 5,
        _ => 0,
    };

    /// <summary>
    /// Aproximación estabilizada (criterio de las aerolíneas a 1000-500 ft): a menos de 1,5 NM, sin más de 300 ft sobre la
    /// senda ni más de 30 kt de más. Si no, motor y al aire.
    /// </summary>
    private bool Stable(LandingRunway l, double along)
    {
        if (along < -1.5 * UnitConversions.MetersPerNauticalMile)
            return true;
        var toThresholdNm = Math.Max(0, -along) / UnitConversions.MetersPerNauticalMile;
        var glide = RunwayFt + _perf.ReferenceAglFt + ThresholdCrossingFt + toThresholdNm * FlightPlanner.GlideFtPerNm(_path.GlideDeg);
        var ias = _tasKt / (1 + 0.02 * Math.Max(0, _alt) / 1000);
        return _alt - glide < 300 && ias < _vapp + 30;
    }

    /// <summary>Motor y al aire decidido por el piloto (no estabilizado, aterrizaje largo).</summary>
    public string? LastGoAroundReason { get; private set; }

    private void GoAroundSelf(string reason)
    {
        if (Landing is not { } l)
            return;
        LastGoAroundReason = reason;
        GoAround(FlightPlanner.GoAround(l, _pos), l.ElevationFt + 3000);
    }

    /// <summary>El motor lo marca como terminado (fuera de la ventana).</summary>
    public void MarkFinished() => Next(FlightPhase.Finished);

    private void Next(FlightPhase phase)
    {
        Phase = phase;
        _phaseTime = 0;
    }

    /// <summary>Pose interpolada entre el tick anterior y el actual (alpha en [0, 1]).</summary>
    public AircraftPose Pose(double alpha)
    {
        var lat = _prevPos.LatDeg + (_pos.LatDeg - _prevPos.LatDeg) * alpha;
        var lon = _prevPos.LonDeg + (_pos.LonDeg - _prevPos.LonDeg) * alpha;
        var heading = GeoMath.NormalizeDeg(_prevHeading + GeoMath.AngleDiffDeg(_prevHeading, _heading) * alpha);
        var alt = _prevAlt + (_alt - _prevAlt) * alpha;
        var pitch = _prevPitch + (_pitch - _prevPitch) * alpha;
        var bank = _prevBank + (_bank - _prevBank) * alpha;
        return new AircraftPose(new GeoPoint(lat, lon), alt, heading, pitch, bank, OnGround, _tasKt, _tasKt, _gearDown, _flaps,
            OnGround ? 0 : _vsFpm);
    }

    /// <summary>
    /// Toma el relevo de una salida ya en el aire (mismo objeto en el simulador): misma posición, altura, rumbo y
    /// velocidad, sin saltos.
    /// </summary>
    public static AirborneAgent FromDeparture(DepartureAgent d, string airline, string origin, string destination, AircraftModel? model,
        FlightPath path, double cruiseAltFt, double originElevationFt, double destinationElevationFt, LandingRunway? landing = null)
    {
        var p = d.Pose(1);
        var category = d.Category;
        var a = new AirborneAgent(d.Callsign, airline, d.TypeDesignator.Length > 0 ? d.TypeDesignator : model?.IcaoType ?? "", origin, destination, model, category,
            path, p.Position, p.AltitudeFt, p.HeadingTrueDeg, d.SpeedKt, p.VerticalSpeedFpm, cruiseAltFt, landing)
        {
            ObjectId = d.ObjectId,
            OriginElevationFt = originElevationFt,
            EndElevationFt = destinationElevationFt,
        };
        a._pitch = a._prevPitch = p.PitchDeg;
        a._gearDown = p.GearDown;
        a._flaps = p.FlapsPercent;
        a.Phase = FlightPhase.Climb;
        return a;
    }
}
