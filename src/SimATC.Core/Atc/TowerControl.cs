using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>Qué autorización de pista tiene el piloto (RF-26: la Torre lo secuencia con los IA).</summary>
public enum PilotTowerClearance
{
    None,
    /// <summary>Avisó que está listo y espera en el punto de espera: entra en la fila de salida (por orden de llegada).</summary>
    Ready,
    LineUp,
    Takeoff,
}

/// <summary>
/// La pista del piloto vista por la Torre, para decidir con las mismas reglas que a los IA: si está libre (y quién la
/// ocupa, con su tipo y si ya está despegando), cuántos IA salen antes que él (la fila del punto de espera, por orden de
/// llegada), si él ya está en el punto de espera, cuánto falta por estela y quién está dentro de la separación de salida.
/// </summary>
public readonly record struct PilotRunwayStatus(int RunwayIndex, string? OccupiedBy, double WakeWaitS, string? SpacingBehind, bool PilotDeparted,
    int AheadCount = 0, bool AtHoldingPoint = true, string? OccupantType = null, bool OccupantDeparting = false,
    double ArrivalInS = double.PositiveInfinity, double OccupantWakeS = 0)
{
    /// <summary>
    /// La próxima llegada a esa pista está lo bastante lejos para que el piloto entre, se alinee y despegue (ADR-129: en
    /// LEMG la Torre lo autorizó a despegar con un A319 en final corta de su misma pista; solo miraba las salidas).
    /// </summary>
    /// Cuenta la espera por estela que falta (o la que dejará el pesado que despega delante, ADR-173: alineado detrás de un
    /// B777 en EGLL, la llegada frustró mientras esperaba sus 2 minutos).
    public bool ArrivalAllowsLineUp => ArrivalInS >= TowerControl.PilotLineUpBeforeArrivalS + Math.Max(WakeWaitS, OccupantWakeS);

    /// <summary>Ya alineado: la próxima llegada deja tiempo para la carrera de despegue.</summary>
    public bool ArrivalAllowsRoll => ArrivalInS >= TowerControl.TakeoffBeforeArrivalS;

    /// <summary>La pista está libre y el piloto es el siguiente: puede alinearse.</summary>
    public bool Free => OccupiedBy is null && AheadCount == 0 && AtHoldingPoint;
    public bool TakeoffClear => Free && WakeWaitS <= 0 && SpacingBehind is null;
}

/// <summary>
/// Torre (ADR-021): dueña de la pista. Autoriza a alinearse con la pista libre y a despegar con la separación de la
/// salida anterior (6000 ft por la pista, FAA JO 7110.65 §3-9-6) y por estela (2 min detrás de un pesado). En la etapa 3
/// autoriza en silencio; en la etapa 4 lo dirá por su frecuencia ("line up and wait", "cleared for take-off").
/// </summary>
public sealed class TowerControl
{
    /// <summary>Separación por la pista entre salidas: 6000 ft (FAA JO 7110.65 §3-9-6).</summary>
    public const double DepartureSpacingM = 1829;
    /// <summary>Tiempo detrás de un pesado para un avión más liviano (estela, 2 min).</summary>
    public const double WakeDelayS = 120;

    /// <summary>Pista mínima por delante para volver a despegar después de abortar (m). APROXIMADO.</summary>
    public const double MinTakeoffRemainingM = 1500;

    /// <summary>Métricas de RF-18c: incursiones del piloto y de la IA (estas deben ser 0: serían un bug).</summary>
    public int PilotIncursions { get; private set; }
    public int AiIncursions { get; private set; }
    public int RejectedTakeoffs { get; private set; }

    public Action<string>? Log { get; set; }

    private readonly HashSet<uint> _aiOnRunwayLogged = [];

    public TowerControl() => ClearArrivals();
    private int _userRunway = -1;

    private readonly Dictionary<int, LastDeparture> _lastDeparture = [];
    private readonly HashSet<uint> _departed = [];
    private double _time;

    /// <summary>Cuándo llegó cada IA al punto de espera (la fila de salida, RF-26).</summary>
    private readonly Dictionary<uint, double> _holdingSince = [];

    /// <summary>A esta distancia (m) de su punto de espera, el piloto está en la fila.</summary>
    public const double HoldingPointM = 60;

    private readonly record struct LastDeparture(DepartureAgent Agent, double LiftoffTime, WakeCategory Wake);

    // Piloto (hito 4c, parte 3). La sesión del piloto corre en el hilo de la interfaz y la Torre en el del motor: lo
    // que se comparte va con un candado (solo copias de structs, sin asignar memoria en el tick).
    private readonly Lock _pilotLock = new();
    private int _pilotRunway = -1;
    private PilotTowerClearance _pilotClearance;
    private int _pilotDepartedRunway = -1;
    private PilotRunwayStatus _pilotStatus = new(-1, null, 0, null, false);
    private GeoPoint? _pilotHoldingPoint;
    private double? _pilotQueuedAt;

    // Llegadas IA (etapa 7): segundos hasta el umbral de la próxima llegada por pista (índice del aeropuerto). Lo llena el
    // motor en cada tick antes de Update; sin asignar memoria.
    private readonly double[] _arrivalS = new double[16];

    /// <summary>Con una llegada a menos de esto del umbral, nadie entra a la pista (s). APROXIMADO (≈ 5 NM a 140 kt).</summary>
    public const double LineUpBeforeArrivalS = 130;

    /// <summary>
    /// Lo mismo para el piloto, con más margen: una persona tarda más que un IA en colacionar, entrar y dar potencia. APROXIMADO.
    /// </summary>
    public const double PilotLineUpBeforeArrivalS = 170;

    /// <summary>
    /// Autorizado a alinearse o despegar pero todavía fuera de la pista, con una llegada a menos de esto (s): la Torre le
    /// cancela ("hold position") y la llegada aterriza. APROXIMADO.
    /// </summary>
    public const double PilotCancelBeforeArrivalS = 75;
    /// <summary>Con una llegada a menos de esto, el alineado no despega (s). APROXIMADO.</summary>
    public const double TakeoffBeforeArrivalS = 55;

    /// <summary>Con una llegada a menos de esto del umbral no se cruza la pista (s). APROXIMADO (≈ 3,5 NM).</summary>
    public const double CrossBeforeArrivalS = 90;

    // Autorización condicional (ADR-095): la llegada que está aterrizando en cada pista (posición y tipo) y los segundos de
    // la siguiente, sin contarla. Lo llena el motor junto con _arrivalS.
    private readonly double[] _nextArrivalS = new double[16];
    private readonly GeoPoint?[] _landingAt = new GeoPoint?[16];
    private readonly string?[] _landingType = new string?[16];

    /// <summary>
    /// Autorización condicional (ADR-095, CAP 413 §4.33): la llegada que aterriza tiene que estar al menos esto más allá del
    /// punto de entrada de la salida (m) para alinear detrás de ella.
    /// </summary>
    public const double LineUpBehindPassedM = 150;

    /// <summary>Olvida las llegadas del tick anterior (las vuelve a dar el motor).</summary>
    public void ClearArrivals()
    {
        Array.Fill(_arrivalS, double.PositiveInfinity);
        Array.Fill(_nextArrivalS, double.PositiveInfinity);
        Array.Fill(_landingAt, null);
        Array.Fill(_landingType, null);
    }

    /// <summary>La llegada que está aterrizando en esa pista (en la carrera o en el traspaso a tierra): dónde está y su tipo.</summary>
    public void SetLanding(int runwayIndex, GeoPoint position, string? typeDesignator)
    {
        if ((uint)runwayIndex >= (uint)_landingAt.Length)
            return;
        _landingAt[runwayIndex] = position;
        _landingType[runwayIndex] = typeDesignator ?? "";
    }

    /// <summary>Una llegada a la pista <paramref name="runwayIndex"/> a <paramref name="seconds"/> s del umbral.</summary>
    public void SetArrival(int runwayIndex, double seconds)
    {
        if ((uint)runwayIndex < (uint)_arrivalS.Length && seconds < _arrivalS[runwayIndex])
            _arrivalS[runwayIndex] = seconds;
    }

    /// <summary>Una llegada todavía en el aire (no la que aterriza) a esa pista: para la autorización condicional.</summary>
    public void SetNextArrival(int runwayIndex, double seconds)
    {
        SetArrival(runwayIndex, seconds);
        if ((uint)runwayIndex < (uint)_nextArrivalS.Length && seconds < _nextArrivalS[runwayIndex])
            _nextArrivalS[runwayIndex] = seconds;
    }

    /// <summary>
    /// ¿Se puede alinear detrás de la llegada que aterriza? (ADR-095, CAP 413 §4.33 "behind the landing DC9, line up
    /// behind"): ya pasó el punto de entrada de la salida y va pista abajo, nadie más ocupa la pista y la siguiente llegada
    /// está a más de <see cref="LineUpBeforeArrivalS"/>. En pistas mixtas ahorra la ocupación de la llegada en cada hueco.
    /// </summary>
    private bool CanLineUpBehindLanding(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user, out string? type)
    {
        type = null;
        var r = a.RunwayIndex;
        if ((uint)r >= (uint)_landingAt.Length || a.BacktrackS > 0 || a.Runway is not { } rw || _nextArrivalS[r] < LineUpBeforeArrivalS)
            return false;
        // La llegada en tierra que todavía no libró la pista (ya traspasada) o la que va en la carrera (todavía en el aire).
        uint landingId = 0;
        LocalPoint? at = null;
        string? landingType = null;
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (o.RunwayIndex == r && o.OnRunwayInbound)
            {
                if (at is not null)
                    return false; // dos llegadas en la pista: nada de condicionales
                at = o.Position;
                landingId = o.ObjectId;
                landingType = o.TypeDesignator;
            }
        }
        if (at is null && _landingAt[r] is { } g)
        {
            at = a.Projection.ToLocal(g);
            landingType = _landingType[r];
        }
        if (at is not { } p)
            return false;
        // Más allá de la entrada de la salida, en su sentido de despegue: le queda menos pista que a la salida.
        var far = a.Projection.ToLocal(a.PrimaryEnd ? rw.Secondary.Position : rw.Primary.Position);
        if (LocalPoint.Distance(p, far) > a.RunwayRemainingM - LineUpBehindPassedM)
            return false;
        if (!RunwayFree(a, agents, user, landingId))
            return false;
        type = landingType;
        return true;
    }

    /// <summary>Segundos de la próxima llegada a esa pista (infinito si no hay).</summary>
    public double ArrivalSeconds(int runwayIndex) =>
        (uint)runwayIndex < (uint)_arrivalS.Length ? _arrivalS[runwayIndex] : double.PositiveInfinity;

    /// <summary>¿El piloto tiene esa pista (autorizado a alinearse o despegar)? Para no autorizar un aterrizaje IA.</summary>
    public bool PilotHoldsRunway(int runwayIndex)
    {
        lock (_pilotLock)
            return _pilotRunway == runwayIndex && _pilotClearance is PilotTowerClearance.LineUp or PilotTowerClearance.Takeoff;
    }

    private bool ArrivalAllows(DepartureAgent a, double minSeconds)
    {
        var s = ArrivalSeconds(a.RunwayIndex);
        if (s >= minSeconds)
            return true;
        a.Hold = HoldKind.Runway;
        a.RunwayHold = RunwayHold.Arrival;
        a.HoldLeftS = s;
        return false;
    }

    /// <summary>El punto de espera de la ruta de rodaje del piloto (lo fija su sesión al dar el rodaje).</summary>
    public void SetPilotHoldingPoint(GeoPoint? point)
    {
        lock (_pilotLock)
        {
            _pilotHoldingPoint = point;
            _pilotQueuedAt = null;
        }
    }

    /// <summary>Posición del piloto (en tierra o en el aire), la fija el motor en cada cuadro; null si no se sabe.</summary>
    public GeoPoint? PilotPosition { get; set; }

    /// <summary>
    /// La sesión del piloto dice qué pista va a usar y qué autorización tiene. Con <see cref="PilotTowerClearance.LineUp"/>
    /// o <see cref="PilotTowerClearance.Takeoff"/> ningún IA se alinea ni despega en esa pista hasta que el piloto salga.
    /// </summary>
    public void SetPilot(int runwayIndex, PilotTowerClearance clearance)
    {
        lock (_pilotLock)
        {
            if (runwayIndex != _pilotRunway)
            {
                _pilotDepartedRunway = -1;
                _pilotQueuedAt = null;
            }
            _pilotRunway = runwayIndex;
            _pilotClearance = clearance;
            if (runwayIndex < 0)
                _pilotStatus = new PilotRunwayStatus(-1, null, 0, null, false);
        }
    }

    /// <summary>El estado de la pista del piloto en el último tick.</summary>
    public PilotRunwayStatus PilotStatus
    {
        get
        {
            lock (_pilotLock)
                return _pilotStatus;
        }
    }

    /// <summary>Un tick: registra despegues y da (o retira) las autorizaciones de pista.</summary>
    public void Update(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user, double dt)
    {
        _time += dt;
        WatchIncursions(agents, user);
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Phase == DeparturePhase.HoldingShort)
                _holdingSince.TryAdd(a.ObjectId, _time);
        }
        UpdatePilot(agents, user);
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Phase is DeparturePhase.Climb or DeparturePhase.Departed)
            {
                if (_departed.Add(a.ObjectId))
                    _lastDeparture[a.RunwayIndex] = new LastDeparture(a, _time, a.Wake);
                continue;
            }
            // Las llegadas en tierra (rodando a su puesto) no usan la Torre.
            if (a.Inbound)
                continue;
            if (a.Phase == DeparturePhase.HoldingShort)
            {
                var lineUp = RunwayFree(a, agents, user) && ArrivalAllows(a, LineUpBeforeArrivalS + a.BacktrackS);
                string? behind = null;
                if (!lineUp && CanLineUpBehindLanding(a, agents, user, out behind))
                    lineUp = true;
                a.LineUpBehind = lineUp ? behind : null;
                a.SetClearance(ClearanceKind.LineUp, lineUp);
            }
            else
            {
                a.SetClearance(ClearanceKind.LineUp, true);
            }
            a.SetClearance(ClearanceKind.Takeoff, a.Phase != DeparturePhase.LinedUp
                || TakeoffClear(a, agents, user) && ArrivalAllows(a, TakeoffBeforeArrivalS));
        }
        UpdateCrossings(agents, user);
    }

    /// <summary>
    /// Cruces de pista (ADR-068, CAP 413 §4.15): al que espera para cruzar se le autoriza si la pista está libre (nadie
    /// alineado, despegando, aterrizando, sin librarla ni cruzando, ni el piloto en ella) y no hay llegada a menos de
    /// <see cref="CrossBeforeArrivalS"/>. Uno por pista a la vez.
    /// </summary>
    private void UpdateCrossings(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Phase != DeparturePhase.Taxi || a.PendingCrossing is not { } c || a.CrossingsCleared > a.CrossingsDone
                || a.TaxiProgress < c.HoldS - 80)
                continue;
            if (RunwayBusy(c.RunwayIndex, a, agents) || ArrivalSeconds(c.RunwayIndex) < CrossBeforeArrivalS)
                continue;
            if (user is { } u && a.Graph?.Runways is { } rw && InsideRunway(a, rw[c.RunwayIndex], a.Projection.ToLocal(u.Position), 5))
                continue;
            a.CrossingsCleared = a.CrossingsDone + 1;
            Log?.Invoke(Tr.F("{0}: autorizado a cruzar la pista {1}.", a.Callsign, c.Ident));
        }
    }

    /// <summary>¿Alguien usa la pista? Salidas en ella, una llegada que no la libró o alguien cruzándola (menos <paramref name="except"/>).</summary>
    private static bool RunwayBusy(int runwayIndex, DepartureAgent? except, IReadOnlyList<DepartureAgent> agents)
    {
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ReferenceEquals(o, except))
                continue;
            if (o.RunwayIndex == runwayIndex && OnRunway(o) || o.CrossingRunway == runwayIndex)
                return true;
        }
        return false;
    }

    public void Remove(DepartureAgent a)
    {
        _departed.Remove(a.ObjectId);
        _holdingSince.Remove(a.ObjectId);
    }

    public void Clear()
    {
        _lastDeparture.Clear();
        _departed.Clear();
        _holdingSince.Clear();
        _aiOnRunwayLogged.Clear();
        SetPilotHoldingPoint(null);
        _userRunway = -1;
        SetPilot(-1, PilotTowerClearance.None);
    }

    // ---------- El piloto en la secuencia (RF-26) ----------

    /// <summary>
    /// Registra el despegue del piloto (autorizado y ya no en tierra) y publica el estado de su pista con las mismas
    /// reglas que <see cref="RunwayFree"/> y <see cref="TakeoffClear"/>. El piloto se considera de estela media.
    /// </summary>
    private void UpdatePilot(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        lock (_pilotLock)
        {
            var r = _pilotRunway;
            if (r < 0)
                return;
            if (_pilotClearance == PilotTowerClearance.Takeoff && user is null && PilotPosition is not null)
            {
                _pilotDepartedRunway = r;
                _pilotClearance = PilotTowerClearance.None;
                Log?.Invoke(Tr.F("Piloto: despegó de la pista {0}.", RunwayName(agents, r)));
            }
            // En la fila desde que llega a su punto de espera (en tierra).
            if (_pilotQueuedAt is null && _pilotHoldingPoint is { } hp && user is not null && PilotPosition is { } at
                && GeoMath.DistanceM(at, hp) < HoldingPointM)
                _pilotQueuedAt = _time;
            // Ocupada: un IA en la pista, o uno ya autorizado a alinearse (aunque todavía no lo haya dicho la radio ni
            // se haya movido: si no, el piloto y él se alineaban a la vez, prueba de Marco 2026-10-31).
            DepartureAgent? occupant = null;
            var ahead = 0;
            for (var j = 0; j < agents.Count; j++)
            {
                var o = agents[j];
                if (o.RunwayIndex != r)
                    continue;
                if (OnRunway(o) || o.Phase == DeparturePhase.HoldingShort && (o.Clearances & ClearanceKind.LineUp) != 0)
                    occupant ??= o;
                else if (o.Phase == DeparturePhase.HoldingShort && _holdingSince.TryGetValue(o.ObjectId, out var since)
                         && (_pilotQueuedAt is not { } q || since < q))
                    ahead++;
            }
            var occupiedBy = occupant?.Callsign;
            var wakeWait = 0.0;
            string? spacing = null;
            if (_lastDeparture.TryGetValue(r, out var last))
            {
                if (last.Wake is WakeCategory.Heavy or WakeCategory.Super)
                    wakeWait = Math.Max(0, WakeDelayS - (_time - last.LiftoffTime));
                if (last.Agent.Phase != DeparturePhase.Departed && PilotPosition is { } pp
                    && LocalPoint.Distance(last.Agent.Position, last.Agent.Projection.ToLocal(pp)) < DepartureSpacingM)
                    spacing = last.Agent.Callsign;
            }
            _pilotStatus = new PilotRunwayStatus(r, occupiedBy, wakeWait, spacing, _pilotDepartedRunway == r, ahead,
                _pilotQueuedAt is not null || _pilotHoldingPoint is null, occupant?.TypeDesignator,
                occupant?.Phase is DeparturePhase.TakeoffRoll or DeparturePhase.Rotation, ArrivalSeconds(r),
                occupant?.Wake is WakeCategory.Heavy or WakeCategory.Super ? WakeDelayS : 0);
        }
    }

    /// <summary>
    /// El piloto tiene la pista de <paramref name="a"/>: está autorizado a alinearse o despegar, o llegó antes que
    /// <paramref name="a"/> al punto de espera y ya avisó que está listo (la fila es por orden de llegada). El IA espera.
    /// </summary>
    private bool PilotHasRunway(DepartureAgent a)
    {
        lock (_pilotLock)
        {
            if (_pilotRunway != a.RunwayIndex)
                return false;
            if (_pilotClearance is PilotTowerClearance.LineUp or PilotTowerClearance.Takeoff)
                return true;
            // El que ya está en la pista sale primero aunque el piloto haya llegado antes a la fila (EGLL 2026-11-27: el
            // piloto avisó "listo" con el IA ya alineado y se esperaban el uno al otro).
            if (OnRunway(a))
                return false;
            return _pilotClearance == PilotTowerClearance.Ready && _pilotQueuedAt is { } q
                && (!_holdingSince.TryGetValue(a.ObjectId, out var since) || q < since);
        }
    }

    private bool PilotCleared(int runway)
    {
        lock (_pilotLock)
            return _pilotRunway == runway && _pilotClearance is PilotTowerClearance.LineUp or PilotTowerClearance.Takeoff;
    }

    /// <summary>El piloto despegó de la pista de <paramref name="a"/> y todavía está a menos de 6000 ft de él.</summary>
    private bool PilotTooClose(DepartureAgent a)
    {
        lock (_pilotLock)
        {
            return _pilotDepartedRunway == a.RunwayIndex && PilotPosition is { } pp
                && LocalPoint.Distance(a.Projection.ToLocal(pp), a.Position) < DepartureSpacingM;
        }
    }

    // ---------- Incursiones (RF-18c) ----------

    /// <summary>
    /// Vigila las pistas: si el piloto entra a una pista activa (un IA se alinea o despega en ella), el que corre aborta
    /// ("cancel take-off") y nadie se alinea ni despega hasta que salga; si un IA pisa una pista sin autorización, es un
    /// bug y se registra. Las frases ("hold position", "cancel take-off") llegan con el ATC de la etapa 4; la llegada en
    /// final ("go around"), con las llegadas (etapa 7).
    /// </summary>
    private void WatchIncursions(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        // El piloto en una pista activa.
        var userRunway = -1;
        for (var i = 0; i < agents.Count && user is not null; i++)
        {
            var a = agents[i];
            if (OnRunway(a) && InsideRunway(a, a.Projection.ToLocal(user.Value.Position), 5))
            {
                userRunway = a.RunwayIndex;
                break;
            }
        }
        // Autorizado a alinearse (también detrás del que despega) o a despegar en esa pista: no es una incursión (ADR-173).
        if (userRunway >= 0 && PilotCleared(userRunway))
            userRunway = -1;
        if (userRunway >= 0)
        {
            if (_userRunway != userRunway)
            {
                PilotIncursions++;
                Log?.Invoke(Tr.F("INCURSIÓN: el piloto entró a la pista activa {0}.", RunwayName(agents, userRunway)));
            }
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a.RunwayIndex == userRunway && a.CancelTakeoff())
                {
                    RejectedTakeoffs++;
                    Log?.Invoke(Tr.F("{0}: despegue abortado por el piloto en la pista (cancel take-off).", a.Callsign));
                }
            }
        }
        _userRunway = userRunway;

        // Un IA sobre una pista sin autorización (nunca debería pasar).
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (!a.OnGround || OnRunway(a) || a.Phase is DeparturePhase.Climb or DeparturePhase.Departed)
                continue;
            var runways = a.Graph?.Runways;
            for (var k = 0; runways is not null && k < runways.Count; k++)
            {
                var r = runways[k];
                // La llegada que deja su pista de aterrizaje está autorizada (etapa 7).
                if (a.Inbound && k == a.RunwayIndex || k == a.CrossingRunway || !InsideRunway(a, r, a.Position, 0))
                    continue;
                if (_aiOnRunwayLogged.Add(a.ObjectId))
                {
                    AiIncursions++;
                    Log?.Invoke(Tr.F("BUG INCURSIÓN IA: {0} en la pista {1} sin autorización ({2}, {3:F0},{4:F0}).", a.Callsign, r.Name, a.Phase, a.Position.EastM, a.Position.NorthM));
                }
                break;
            }
        }
    }

    private static string RunwayName(IReadOnlyList<DepartureAgent> agents, int runway)
    {
        foreach (var a in agents)
        {
            if (a.RunwayIndex == runway && a.Runway is { } r)
                return r.Name;
        }
        return runway.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool InsideRunway(DepartureAgent a, LocalPoint p, double marginM) =>
        a.Runway is { } r && InsideRunway(a, r, p, marginM);

    private static bool InsideRunway(DepartureAgent a, Airports.Runway r, LocalPoint p, double marginM)
    {
        var c = a.Projection.ToLocal(r.Center);
        var h = r.HeadingTrueDeg * GeoMath.DegToRad;
        var dx = p.EastM - c.EastM;
        var dy = p.NorthM - c.NorthM;
        var along = dx * Math.Sin(h) + dy * Math.Cos(h);
        var across = dx * Math.Cos(h) - dy * Math.Sin(h);
        return Math.Abs(along) <= r.LengthM / 2 && Math.Abs(across) <= r.WidthM / 2 + marginM;
    }

    private static bool OnRunway(DepartureAgent o) =>
        !o.Inbound && o.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll or DeparturePhase.RejectedTakeoff
            or DeparturePhase.Rotation
        || o.OnRunwayInbound;

    /// <summary>
    /// ¿<paramref name="o"/> está alineado detrás de <paramref name="a"/> (ya alineado), en otra entrada de la misma pista?
    /// Entonces no le estorba: sale primero el de más adelante (ADR-093, EGLL: dos alineados por N2W y S se esperaban).
    /// </summary>
    private static bool BehindOnRunway(DepartureAgent o, DepartureAgent a) =>
        a.Phase == DeparturePhase.LinedUp && o.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp && !o.OnRunwayInbound
        && o.RunwayRemainingM > a.RunwayRemainingM + BehindMarginM;

    /// <summary>Diferencia de pista restante (m) para decir que un alineado está detrás de otro.</summary>
    public const double BehindMarginM = 60;

    private static bool UserOnRunway(DepartureAgent a, GroundObstacle? user)
    {
        if (user is not { } u || a.Runway is not { } r)
            return false;
        var p = a.Projection.ToLocal(u.Position);
        var c = a.Projection.ToLocal(r.Center);
        var h = r.HeadingTrueDeg * GeoMath.DegToRad;
        var dx = p.EastM - c.EastM;
        var dy = p.NorthM - c.NorthM;
        var along = dx * Math.Sin(h) + dy * Math.Cos(h);
        var across = dx * Math.Cos(h) - dy * Math.Sin(h);
        return Math.Abs(along) <= r.LengthM / 2 + 30 && Math.Abs(across) <= r.WidthM / 2 + u.WingspanM / 2;
    }

    private bool RunwayFree(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user, uint ignore = 0)
    {
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ignore != 0 && o.ObjectId == ignore)
                continue;
            if (!ReferenceEquals(o, a) && o.CrossingRunway == a.RunwayIndex)
            {
                a.Hold = HoldKind.Runway;
                a.RunwayHold = RunwayHold.Occupied;
                a.HoldFor = o.ObjectId;
                return false;
            }
            if (!ReferenceEquals(o, a) && o.RunwayIndex == a.RunwayIndex
                && (OnRunway(o) && !BehindOnRunway(o, a)
                    || o.Phase == DeparturePhase.HoldingShort && (o.Clearances & ClearanceKind.LineUp) != 0))
            {
                a.Hold = HoldKind.Runway;
                a.RunwayHold = RunwayHold.Occupied;
                a.HoldFor = o.ObjectId;
                return false;
            }
        }
        if (UserOnRunway(a, user))
        {
            a.Hold = HoldKind.Runway;
            a.RunwayHold = RunwayHold.UserOnRunway;
            return false;
        }
        if (PilotHasRunway(a))
        {
            a.Hold = HoldKind.Runway;
            a.RunwayHold = RunwayHold.PilotTurn;
            return false;
        }
        return true;
    }

    private bool TakeoffClear(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        if (!RunwayFree(a, agents, user))
            return false;
        // Después de abortar: solo si le queda pista suficiente.
        if (a.RunwayRemainingM < MinTakeoffRemainingM)
        {
            a.Hold = HoldKind.Runway;
            a.RunwayHold = RunwayHold.ShortRunway;
            return false;
        }
        if (user is null && PilotTooClose(a))
        {
            a.Hold = HoldKind.DepartureSeparation;
            return false;
        }
        if (!_lastDeparture.TryGetValue(a.RunwayIndex, out var last) || ReferenceEquals(last.Agent, a))
            return true;
        // Estela: 2 min detrás de un pesado para uno más liviano.
        var heavy = last.Wake is WakeCategory.Heavy or WakeCategory.Super;
        if (heavy && a.Wake is not (WakeCategory.Heavy or WakeCategory.Super) && _time - last.LiftoffTime < WakeDelayS)
        {
            a.Hold = HoldKind.Wake;
            a.HoldFor = last.Agent.ObjectId;
            a.HoldLeftS = WakeDelayS - (_time - last.LiftoffTime);
            return false;
        }
        // El anterior en el aire y a 6000 ft por la pista (si ya se retiró del motor, está lejos).
        if (last.Agent.Phase != DeparturePhase.Departed && LocalPoint.Distance(last.Agent.Position, a.Position) < DepartureSpacingM)
        {
            a.Hold = HoldKind.DepartureSeparation;
            a.HoldFor = last.Agent.ObjectId;
            return false;
        }
        return true;
    }
}
