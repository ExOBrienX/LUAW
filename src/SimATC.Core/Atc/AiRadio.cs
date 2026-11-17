using System.Collections.Concurrent;
using SimATC.Core.Engine;
using SimATC.Core.Geo;

namespace SimATC.Core.Atc;

/// <summary>Qué se dice en una línea de radio de un avión IA o del ATC que le habla (hito 4e-1).</summary>
public enum RadioLineKind
{
    RequestPushback,
    PushbackApproved,
    Standby,
    ReadbackPushback,
    RequestTaxi,
    Taxi,
    ReadbackTaxi,
    ContactTower,
    ReadbackContactTower,
    ReadyForDeparture,
    HoldShort,
    ReadbackHoldShort,
    LineUp,
    ReadbackLineUp,
    Takeoff,
    ReadbackTakeoff,
    ContactDeparture,
    ReadbackContactDeparture,
    /// <summary>Ground le dice al IA que ceda el paso al piloto (<see cref="RadioLine.Detail"/>: de qué lado pasa).</summary>
    GiveWay,
    ReadbackGiveWay,
    /// <summary>Ground le dice al IA que siga al piloto, que va adelante en el mismo sentido.</summary>
    Follow,
    ReadbackFollow,
    // Llegadas (etapa 7, §8.3b).
    /// <summary>El piloto IA avisa que está en final (a la Torre).</summary>
    ArrivalFinal,
    ContinueApproach,
    ReadbackContinueApproach,
    ClearedToLand,
    ReadbackClearedToLand,
    GoAround,
    ReadbackGoAround,
    /// <summary>Approach autoriza el descenso (<see cref="RadioLine.Detail"/>: nivel en ft).</summary>
    Descend,
    ReadbackDescend,
    /// <summary>Approach autoriza la aproximación (<see cref="RadioLine.Detail"/>: "ILS", "RNP"…).</summary>
    ClearedApproach,
    ReadbackClearedApproach,
    /// <summary>Approach asigna velocidad (<see cref="RadioLine.Detail"/>: kt) o la libera (Detail "0").</summary>
    Speed,
    ReadbackSpeed,
    /// <summary>En ruta (ADR-089): el IA hace su primer contacto con el Centro (<see cref="RadioLine.Detail"/>: su nivel en ft).</summary>
    EnrouteCheckIn,
    /// <summary>El Centro: "identified".</summary>
    EnrouteIdentified,
    /// <summary>Approach/Centro manda a esperar (ADR-076; <see cref="RadioLine.Detail"/>: "fijo|altitud ft|minutos de demora").</summary>
    Hold,
    ReadbackHold,
    // Llegada en tierra (ADR-065, §8.3b).
    /// <summary>Torre: "vacate via (calle), when vacated contact Ground" (<see cref="RadioLine.Detail"/>: la calle o vacía).</summary>
    VacateContactGround,
    ReadbackVacateContactGround,
    /// <summary>El piloto IA a Ground: "runway vacated".</summary>
    RunwayVacated,
    /// <summary>Ground: "taxi to stand (puesto) via (calles)" (<see cref="RadioLine.Detail"/>: el puesto).</summary>
    TaxiToStand,
    ReadbackTaxiToStand,
    /// <summary>Ground: "cross runway (pista), report vacated" (<see cref="RadioLine.Detail"/>: la pista), ADR-068.</summary>
    CrossRunway,
    ReadbackCrossRunway,
    /// <summary>
    /// Approach avisa el cambio de pista a una llegada (ADR-097): "expect (aproximación) approach runway (pista)"
    /// (<see cref="RadioLine.Detail"/>: "ILS", "RNP"…).
    /// </summary>
    ExpectApproach,
    ReadbackExpectApproach,
}

/// <summary>Qué se le dice a una llegada IA (lo pide el motor al pasar de fase o la secuencia de llegadas).</summary>
public enum ArrivalCall
{
    /// <summary>Approach la pasa a la Torre y el piloto colaciona; después avisa en final y la Torre le dice que siga.</summary>
    HandoffToTower,
    ClearedToLand,
    GoAround,
    Descend,
    ClearedApproach,
    Speed,
    /// <summary>A esperar sobre un fijo (ADR-076).</summary>
    Hold,
    /// <summary>Cambio de pista: "expect (aproximación) approach runway (pista)" (ADR-097).</summary>
    ExpectApproach,
}

/// <summary>
/// Una línea dicha en una frecuencia: quién (la dependencia o el avión), qué y los datos para armar la frase. El texto lo
/// arma la app con la fraseología (§8); el motor solo publica esto, sin asignar texto en el tick.
/// </summary>
public readonly record struct RadioLine(RadioLineKind Kind, AtcRole Station, int Khz, uint ObjectId, string Callsign, string? Runway,
    IReadOnlyList<string> Taxiways, int NextKhz, double TimeS, string? Detail = null)
{
    public bool FromAtc => Kind is RadioLineKind.PushbackApproved or RadioLineKind.Standby or RadioLineKind.Taxi
        or RadioLineKind.ContactTower or RadioLineKind.HoldShort or RadioLineKind.LineUp or RadioLineKind.Takeoff
        or RadioLineKind.ContactDeparture or RadioLineKind.GiveWay or RadioLineKind.Follow or RadioLineKind.ContinueApproach
        or RadioLineKind.ClearedToLand or RadioLineKind.GoAround or RadioLineKind.Descend or RadioLineKind.ClearedApproach
        or RadioLineKind.Speed or RadioLineKind.VacateContactGround or RadioLineKind.TaxiToStand or RadioLineKind.CrossRunway
        or RadioLineKind.EnrouteIdentified or RadioLineKind.ExpectApproach;

    /// <summary>"left to right" / "right to left" (de <see cref="RadioLineKind.GiveWay"/>, visto desde el IA).</summary>
    public const string LeftToRight = "left to right";
    public const string RightToLeft = "right to left";
}

/// <summary>
/// Cola de líneas del motor a la interfaz: un búfer circular preasignado con candado, que no asigna en el tick y no
/// crece si la interfaz no lo vacía (en ese caso se pierden las líneas más viejas).
/// </summary>
public sealed class RadioLineBuffer
{
    private readonly RadioLine[] _buffer = new RadioLine[512];
    private readonly Lock _lock = new();
    private int _head;
    private int _count;

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
                return _count == 0;
        }
    }

    public void Enqueue(in RadioLine line)
    {
        lock (_lock)
        {
            _buffer[(_head + _count) % _buffer.Length] = line;
            if (_count < _buffer.Length)
                _count++;
            else
                _head = (_head + 1) % _buffer.Length;
        }
    }

    public bool TryDequeue(out RadioLine line)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                line = default;
                return false;
            }
            line = _buffer[_head];
            _buffer[_head] = default;
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return true;
        }
    }
}

/// <summary>
/// Frecuencias de las dependencias que les hablan a los IA (0 = no hay; salida y aproximación son opcionales). Los descensos,
/// velocidades y la autorización de aproximación son de Aproximación: sin su frecuencia no se oyen (nunca los dice la Torre).
/// </summary>
public sealed record RadioFrequencies(int GroundKhz, int TowerKhz, int DepartureKhz, int ApproachKhz = 0)
{
    /// <summary>La de Aproximación (o la de Salida si es la misma dependencia), o 0.</summary>
    public int ArrivalKhz => ApproachKhz > 0 ? ApproachKhz : DepartureKhz;
}

/// <summary>
/// Los aviones IA en la frecuencia (hito 4e-1, RF-26, RF-38). Lo que el control ya decide (ADR-021) se dice por radio:
/// el piloto IA pide el pushback, Ground lo aprueba o dice "standby", el piloto colaciona; rodaje; paso a Torre; listo
/// para salir; alinear y despegar; paso a Salida. Cada avión <b>solo actúa cuando escuchó la autorización</b>: el
/// avión tiene autorizaciones por radio (<see cref="DepartureAgent.RadioClearances"/>) que se dan al terminar su colación.
/// Una cola por frecuencia: nunca se pisan dos transmisiones (RF-38), tampoco con las del piloto
/// (<see cref="MarkBusy"/>). Corre en el hilo del motor; la interfaz lee <see cref="Lines"/>.
/// </summary>
public sealed class AiRadio
{
    /// <summary>Pausa entre las líneas de un intercambio (s) y después de terminarlo, antes del siguiente.</summary>
    public const double LineGapS = 1.0;
    public const double ExchangeGapS = 1.0;

    /// <summary>Segundos con el beacon antes de pedir el pushback (más que la revisión de Ground, que es cada 1 s).</summary>
    public const double PushRequestAfterS = 2;

    /// <summary>A esta distancia del punto de espera (m), Ground pasa al IA a Torre.</summary>
    public const double TowerHandoffM = 300;

    /// <summary>Altura (ft) a la que Torre pasa al IA a Salida.</summary>
    public const double DepartureHandoffFt = 500;

    /// <summary>Lo que dura cada línea (s), aproximado por el largo típico de la frase.</summary>
    public static double DurationS(RadioLineKind kind) => kind switch
    {
        RadioLineKind.Taxi => 5,
        RadioLineKind.ReadbackTaxi => 4.5,
        RadioLineKind.Takeoff => 4,
        RadioLineKind.ReadyForDeparture => 3.5,
        RadioLineKind.Standby => 1.5,
        RadioLineKind.GiveWay => 3.5,
        RadioLineKind.ClearedToLand or RadioLineKind.ReadbackClearedToLand => 4,
        RadioLineKind.GoAround => 3.5,
        RadioLineKind.VacateContactGround or RadioLineKind.ReadbackVacateContactGround => 4,
        RadioLineKind.TaxiToStand or RadioLineKind.ReadbackTaxiToStand => 4.5,
        RadioLineKind.CrossRunway or RadioLineKind.ReadbackCrossRunway => 3.5,
        _ => 2.5,
    };

    private enum ExchangeType
    {
        PushRequest,
        PushApproval,
        TaxiRequest,
        TowerHandoff,
        Ready,
        LineUpCall,
        TakeoffCall,
        DepartureHandoff,
        UserTraffic,
        /// <summary>Llegada: la Torre la manda a librar y a Ground.</summary>
        Vacate,
        /// <summary>Llegada: "runway vacated" y la autorización de rodaje al puesto.</summary>
        ArrivalTaxi,
        /// <summary>Cruce de pista: Ground autoriza y el piloto colaciona.</summary>
        CrossClearance,
        /// <summary>Cruce hecho: el piloto avisa "runway vacated".</summary>
        CrossVacated,
    }

    private sealed class AgentState
    {
        public bool Managed;
        public bool Queued;
        public bool PushRequested;
        public bool PushGranted;
        public bool TaxiDone;
        public bool HandoffDone;
        public bool ReadyCalled;
        public bool LineUpGiven;
        public bool TakeoffGiven;
        public bool DepartureDone;
        public bool UserAdvised;
        public bool VacateDone;
        public bool ArrivalTaxiDone;
        public int CrossCalls;
        public int CrossReports;
        public long SeenTick;
    }

    private struct Pending
    {
        public DepartureAgent Agent;
        public ExchangeType Type;
        public int Khz;
    }

    /// <summary>Un intercambio en el aire: hasta tres líneas, con su hora.</summary>
    private struct Active
    {
        public DepartureAgent Agent;
        public ExchangeType Type;
        public int Khz;
        public RadioLineKind K0, K1, K2;
        public double T0, T1, T2;
        public int Count;
        public int Emitted;
        public ClearanceKind Grant;
        public string? Detail;
    }

    private readonly Dictionary<uint, AgentState> _agents = [];
    private readonly Dictionary<int, double> _busyUntil = [];
    private readonly List<Pending> _waiting = [];
    private readonly List<Active> _active = [];
    private readonly ConcurrentQueue<(int Khz, double Seconds)> _busyRequests = new();
    private readonly List<uint> _gone = [];
    private long _tick;
    private GroundObstacle? _user;

    /// <summary>Un intercambio con una llegada: hasta tres líneas en una frecuencia (con su dependencia).</summary>
    private struct ArrivalExchange
    {
        public uint Id;
        public string Callsign;
        public string? Runway;
        public int Khz;
        public int NextKhz;
        public AtcRole Station;
        public RadioLineKind K0, K1, K2;
        public int Count;
        public bool Urgent;
        public double T0, T1, T2;
        public int Emitted;
        public string? Detail;
        public Action? Done;
    }

    private readonly List<ArrivalExchange> _arrivalWaiting = [];
    private readonly List<ArrivalExchange> _arrivalActive = [];

    /// <summary>
    /// Pide decirle algo a una llegada (en el hilo del motor; pasa pocas veces por vuelo). Va a la cola de su frecuencia:
    /// nunca se pisa con otra transmisión (RF-38). "Motor y al aire" va primero.
    /// </summary>
    /// <param name="detail">Dato de la línea (nivel en ft, tipo de aproximación).</param>
    /// <param name="done">Al terminar la colación (en el hilo del motor): la autorización pasa a valer.</param>
    public void QueueArrival(uint id, string callsign, string? runway, ArrivalCall call, string? detail = null, Action? done = null)
    {
        if (Frequencies is not { TowerKhz: > 0 } f)
        {
            done?.Invoke(); // sin frecuencias no hay radio: vale en el acto
            return;
        }
        void Add(int khz, AtcRole station, int next, RadioLineKind k0, RadioLineKind k1, RadioLineKind k2, int count, bool urgent) =>
            _arrivalWaiting.Add(new ArrivalExchange
            {
                Id = id, Callsign = callsign, Runway = runway, Khz = khz, NextKhz = next, Station = station,
                K0 = k0, K1 = k1, K2 = k2, Count = count, Urgent = urgent, Detail = detail, Done = done,
            });
        var approachKhz = f.ArrivalKhz;
        const AtcRole approachRole = AtcRole.Approach;
        // Sin frecuencia de Aproximación, sus instrucciones valen sin oírse (la Torre no da descensos).
        if (approachKhz == 0 && call is ArrivalCall.Descend or ArrivalCall.Speed or ArrivalCall.ClearedApproach or ArrivalCall.Hold
                or ArrivalCall.ExpectApproach)
        {
            done?.Invoke();
            return;
        }
        switch (call)
        {
            case ArrivalCall.Descend:
                Add(approachKhz, approachRole, 0, RadioLineKind.Descend, RadioLineKind.ReadbackDescend, default, 2, false);
                break;
            case ArrivalCall.Speed:
                Add(approachKhz, approachRole, 0, RadioLineKind.Speed, RadioLineKind.ReadbackSpeed, default, 2, false);
                break;
            case ArrivalCall.ClearedApproach:
                Add(approachKhz, approachRole, 0, RadioLineKind.ClearedApproach, RadioLineKind.ReadbackClearedApproach, default, 2, false);
                break;
            case ArrivalCall.ExpectApproach:
                Add(approachKhz, approachRole, 0, RadioLineKind.ExpectApproach, RadioLineKind.ReadbackExpectApproach, default, 2, false);
                break;
            case ArrivalCall.Hold:
                Add(approachKhz, approachRole, 0, RadioLineKind.Hold, RadioLineKind.ReadbackHold, default, 2, false);
                break;
            case ArrivalCall.HandoffToTower:
                if (approachKhz > 0 && approachKhz != f.TowerKhz)
                    Add(approachKhz, AtcRole.Approach, f.TowerKhz, RadioLineKind.ContactTower, RadioLineKind.ReadbackContactTower, default, 2, false);
                Add(f.TowerKhz, AtcRole.Tower, 0, RadioLineKind.ArrivalFinal, RadioLineKind.ContinueApproach, RadioLineKind.ReadbackContinueApproach, 3, false);
                break;
            case ArrivalCall.ClearedToLand:
                Add(f.TowerKhz, AtcRole.Tower, 0, RadioLineKind.ClearedToLand, RadioLineKind.ReadbackClearedToLand, default, 2, false);
                break;
            case ArrivalCall.GoAround:
                // La orden anterior (aterrizar) ya no vale.
                _arrivalWaiting.RemoveAll(e => e.Id == id && e.K0 == RadioLineKind.ClearedToLand);
                Add(f.TowerKhz, AtcRole.Tower, 0, RadioLineKind.GoAround, RadioLineKind.ReadbackGoAround, default, 2, true);
                break;
        }
    }

    /// <summary>Empieza las llegadas en espera cuya frecuencia está libre (las urgentes primero) y publica sus líneas.</summary>
    private void UpdateArrivals(double now)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < _arrivalWaiting.Count; i++)
            {
                var e = _arrivalWaiting[i];
                if (e.Urgent != (pass == 0) || BusyUntil(e.Khz) > now || OnAir(e.Khz) || ArrivalOnAir(e.Khz))
                    continue;
                // Una llegada a la vez por avión: el cambio a Torre antes del aviso en final.
                if (ArrivalBusy(e.Id))
                    continue;
                e.T0 = now;
                e.T1 = e.T0 + DurationS(e.K0) + LineGapS;
                e.T2 = e.T1 + DurationS(e.K1) + LineGapS;
                var last = e.Count switch { 1 => e.T0 + DurationS(e.K0), 2 => e.T1 + DurationS(e.K1), _ => e.T2 + DurationS(e.K2) };
                _busyUntil[e.Khz] = last + ExchangeGapS;
                _arrivalActive.Add(e);
                _arrivalWaiting.RemoveAt(i);
                i--;
            }
        }
        for (var i = 0; i < _arrivalActive.Count; i++)
        {
            var e = _arrivalActive[i];
            while (e.Emitted < e.Count)
            {
                var (kind, at) = e.Emitted switch { 0 => (e.K0, e.T0), 1 => (e.K1, e.T1), _ => (e.K2, e.T2) };
                if (at > now)
                    break;
                var next = kind is RadioLineKind.ContactTower or RadioLineKind.ReadbackContactTower ? e.NextKhz : 0;
                Lines.Enqueue(new RadioLine(kind, e.Station, e.Khz, e.Id, e.Callsign, e.Runway, [], next, at, e.Detail));
                e.Emitted++;
            }
            if (e.Emitted < e.Count)
            {
                _arrivalActive[i] = e;
                continue;
            }
            _arrivalActive.RemoveAt(i);
            i--;
            e.Done?.Invoke();
        }
    }

    private bool ArrivalOnAir(int khz)
    {
        for (var i = 0; i < _arrivalActive.Count; i++)
        {
            if (_arrivalActive[i].Khz == khz)
                return true;
        }
        return false;
    }

    private bool ArrivalBusy(uint id)
    {
        for (var i = 0; i < _arrivalActive.Count; i++)
        {
            if (_arrivalActive[i].Id == id)
                return true;
        }
        return false;
    }

    /// <summary>Lo dicho en todas las frecuencias, en orden; la interfaz lo vacía.</summary>
    public RadioLineBuffer Lines { get; } = new();

    /// <summary>Frecuencias de la dependencia del aeropuerto (las fija la app al cargarlo). Sin ellas, los IA no hablan.</summary>
    public RadioFrequencies? Frequencies { get; set; }

    /// <summary>
    /// Frecuencia del Centro con que está el piloto en ruta (0 si no): los IA en crucero cerca de él hablan ahí, como en el ATC
    /// del simulador (ADR-089). La fija la app.
    /// </summary>
    public int EnrouteKhz { get; set; }

    /// <summary>Un intercambio en ruta con el Centro del piloto (primer contacto o descenso), en la cola de llegadas.</summary>
    public void QueueEnroute(uint id, string callsign, RadioLineKind k0, RadioLineKind k1, string? detail)
    {
        if (EnrouteKhz <= 0)
            return;
        _arrivalWaiting.Add(new ArrivalExchange
        {
            Id = id, Callsign = callsign, Khz = EnrouteKhz, Station = AtcRole.Center, K0 = k0, K1 = k1, Count = 2, Detail = detail,
        });
    }

    /// <summary>El piloto (o el ATC que le responde) ocupa la frecuencia durante <paramref name="seconds"/> (desde cualquier hilo).</summary>
    public void MarkBusy(int khz, double seconds) => _busyRequests.Enqueue((khz, seconds));

    /// <summary>Un paso, en el hilo del motor, después del control y antes de mover a los aviones.</summary>
    public void Update(IReadOnlyList<DepartureAgent> agents, double now, GroundObstacle? user = null)
    {
        _tick++;
        _user = user;
        while (_busyRequests.TryDequeue(out var b))
            _busyUntil[b.Khz] = Math.Max(BusyUntil(b.Khz), now + b.Seconds);
        if (Frequencies is not { GroundKhz: > 0, TowerKhz: > 0 } f)
            return;

        for (var i = 0; i < agents.Count; i++)
            Watch(agents[i], f, now);
        Transmit(now);
        StartWaiting(now);
        UpdateArrivals(now);
        if (_tick % 100 == 0)
            Forget();
    }

    /// <summary>Qué tiene que decirse ahora para este avión, según su fase y lo que decidió el control.</summary>
    private void Watch(DepartureAgent a, RadioFrequencies f, double now)
    {
        if (!_agents.TryGetValue(a.ObjectId, out var s))
        {
            // Se manejan los que se ven antes del pushback y las llegadas recién aterrizadas; los demás siguen sin radio.
            s = new AgentState { Managed = a.Phase is DeparturePhase.DoorsClosing or DeparturePhase.BeaconOn || a.Inbound && a.Phase == DeparturePhase.Taxi };
            _agents[a.ObjectId] = s;
            if (s.Managed)
            {
                a.RadioClearances = ClearanceKind.None;
                a.CrossingsHeard = a.CrossingsDone;
            }
        }
        s.SeenTick = _tick;
        if (!s.Managed || s.Queued)
            return;
        if (WatchCrossing(a, s, f))
            return;
        if (a.Inbound)
        {
            WatchArrival(a, s, f);
            return;
        }
        switch (a.Phase)
        {
            // El pedido sale cuando Ground ya decidió (revisa una vez por segundo): si no, contestaba "standby" a todos y
            // segundos después aprobaba (prueba de Marco, 2026-10-31).
            case DeparturePhase.BeaconOn when !s.PushRequested && a.PhaseTimeS >= PushRequestAfterS:
                Queue(s, a, ExchangeType.PushRequest, f.GroundKhz);
                break;
            case DeparturePhase.BeaconOn when s.PushRequested && !s.PushGranted && (a.Clearances & ClearanceKind.Pushback) != 0:
                Queue(s, a, ExchangeType.PushApproval, f.GroundKhz);
                break;
            case DeparturePhase.PushbackComplete when !s.TaxiDone:
                Queue(s, a, ExchangeType.TaxiRequest, f.GroundKhz);
                break;
            // Se detiene por el piloto (hito 4e-2): Ground se lo dice ("give way" o "follow"), una vez por avión.
            case DeparturePhase.Taxi when s.TaxiDone && !s.UserAdvised && a.Hold == HoldKind.UserAircraft && _user is not null:
                s.UserAdvised = true;
                Queue(s, a, ExchangeType.UserTraffic, s.HandoffDone ? f.TowerKhz : f.GroundKhz);
                break;
            case DeparturePhase.Taxi when s.TaxiDone && !s.HandoffDone && a.HoldAt - a.TaxiProgress <= TowerHandoffM:
                Queue(s, a, f.TowerKhz == f.GroundKhz ? ExchangeType.Ready : ExchangeType.TowerHandoff, f.GroundKhz);
                if (f.TowerKhz == f.GroundKhz)
                    s.HandoffDone = true;
                break;
            case DeparturePhase.Taxi or DeparturePhase.HoldingShort when s.HandoffDone && !s.ReadyCalled:
                Queue(s, a, ExchangeType.Ready, f.TowerKhz);
                break;
            case DeparturePhase.HoldingShort when s.ReadyCalled && !s.LineUpGiven && (a.Clearances & ClearanceKind.LineUp) != 0:
                Queue(s, a, ExchangeType.LineUpCall, f.TowerKhz);
                break;
            case DeparturePhase.LinedUp when s.LineUpGiven && !s.TakeoffGiven && (a.Clearances & ClearanceKind.Takeoff) != 0:
                Queue(s, a, ExchangeType.TakeoffCall, f.TowerKhz);
                break;
            case DeparturePhase.Climb when !s.DepartureDone && a.AglFt >= DepartureHandoffFt:
                s.DepartureDone = true;
                if (f.DepartureKhz > 0)
                    Queue(s, a, ExchangeType.DepartureHandoff, f.TowerKhz);
                break;
        }
    }

    /// <summary>
    /// Llegada en tierra (ADR-065, CAP 413 cap. 4): al dejar la pista la Torre la manda a Ground ("vacate via B3, when
    /// vacated contact Ground"); con la pista libre avisa a Ground y Ground la autoriza al puesto con las calles. Si Ground y
    /// Torre comparten frecuencia, solo el rodaje.
    /// </summary>
    private void WatchArrival(DepartureAgent a, AgentState s, RadioFrequencies f)
    {
        if (a.Phase != DeparturePhase.Taxi)
            return;
        if (!s.VacateDone)
        {
            if (f.GroundKhz == f.TowerKhz)
                s.VacateDone = true;
            else
            {
                Queue(s, a, ExchangeType.Vacate, f.TowerKhz);
                return;
            }
        }
        if (!s.ArrivalTaxiDone && a.TaxiProgress >= a.VacatedAt - 25)
            Queue(s, a, ExchangeType.ArrivalTaxi, f.GroundKhz);
    }

    /// <summary>
    /// Cruces de pista (ADR-068): cuando la Torre lo autoriza, Ground se lo dice ("cross runway 06L, report vacated") y el
    /// piloto colaciona; al librarla avisa "runway vacated". true si encoló algo.
    /// </summary>
    private bool WatchCrossing(DepartureAgent a, AgentState s, RadioFrequencies f)
    {
        if (a.Phase != DeparturePhase.Taxi)
            return false;
        if (s.CrossReports < s.CrossCalls && a.CrossingsDone >= s.CrossCalls)
        {
            Queue(s, a, ExchangeType.CrossVacated, f.GroundKhz);
            return true;
        }
        if (a.CrossingsCleared > a.CrossingsDone && a.CrossingsHeard <= a.CrossingsDone && s.CrossCalls <= a.CrossingsDone)
        {
            Queue(s, a, ExchangeType.CrossClearance, f.GroundKhz);
            return true;
        }
        return false;
    }

    private void Queue(AgentState s, DepartureAgent a, ExchangeType type, int khz)
    {
        s.Queued = true;
        _waiting.Add(new Pending { Agent = a, Type = type, Khz = khz });
    }

    /// <summary>
    /// Empieza los intercambios en espera cuya frecuencia está libre: primero los que mueven la pista y el pushback
    /// (despegue, alinear, aprobar un pushback), después los llamados de rutina, cada grupo en orden de llegada. Como un
    /// controlador: no deja la pista vacía por contestar un "ready for departure".
    /// </summary>
    private void StartWaiting(double now)
    {
        while (true)
        {
            var best = -1;
            for (var i = 0; i < _waiting.Count; i++)
            {
                var c = _waiting[i];
                if (BusyUntil(c.Khz) > now || OnAir(c.Khz))
                    continue;
                if (best < 0 || Priority(c.Type) > Priority(_waiting[best].Type))
                    best = i;
            }
            if (best < 0)
                return;
            var p = _waiting[best];
            _waiting.RemoveAt(best);
            var s = _agents[p.Agent.ObjectId];
            if (Build(p, s, now) is { } ex)
            {
                _active.Add(ex);
                _busyUntil[p.Khz] = ex.Count switch { 1 => ex.T0, 2 => ex.T1, _ => ex.T2 } + DurationS(Last(ex)) + ExchangeGapS;
            }
            else
            {
                s.Queued = false; // ya no corresponde (el control cambió de idea): se revisa en el próximo paso
            }
        }
    }

    private static int Priority(ExchangeType t) => t switch
    {
        ExchangeType.TakeoffCall => 3,
        ExchangeType.LineUpCall => 2,
        ExchangeType.CrossClearance => 2,
        ExchangeType.PushApproval or ExchangeType.UserTraffic or ExchangeType.Vacate or ExchangeType.ArrivalTaxi or ExchangeType.CrossVacated => 1,
        _ => 0,
    };

    /// <summary>Arma el intercambio al momento de empezar (con lo que el control decide ahora).</summary>
    private Active? Build(Pending p, AgentState s, double now)
    {
        var a = p.Agent;
        var ex = new Active { Agent = a, Type = p.Type, Khz = p.Khz };
        switch (p.Type)
        {
            case ExchangeType.PushRequest:
                s.PushRequested = true;
                if ((a.Clearances & ClearanceKind.Pushback) != 0)
                    SetLines(ref ex, now, RadioLineKind.RequestPushback, RadioLineKind.PushbackApproved, RadioLineKind.ReadbackPushback, ClearanceKind.Pushback);
                else
                    SetLines(ref ex, now, RadioLineKind.RequestPushback, RadioLineKind.Standby);
                return ex;
            case ExchangeType.PushApproval when (a.Clearances & ClearanceKind.Pushback) != 0:
                SetLines(ref ex, now, RadioLineKind.PushbackApproved, RadioLineKind.ReadbackPushback, grant: ClearanceKind.Pushback);
                return ex;
            case ExchangeType.TaxiRequest:
                SetLines(ref ex, now, RadioLineKind.RequestTaxi, RadioLineKind.Taxi, RadioLineKind.ReadbackTaxi);
                return ex;
            case ExchangeType.TowerHandoff:
                SetLines(ref ex, now, RadioLineKind.ContactTower, RadioLineKind.ReadbackContactTower);
                return ex;
            case ExchangeType.Ready when a.Phase == DeparturePhase.HoldingShort && (a.Clearances & ClearanceKind.LineUp) != 0:
                SetLines(ref ex, now, RadioLineKind.ReadyForDeparture, RadioLineKind.LineUp, RadioLineKind.ReadbackLineUp, ClearanceKind.LineUp);
                ex.Detail = a.LineUpBehind; // condicional detrás de la que aterriza (ADR-095)
                return ex;
            case ExchangeType.Ready:
                SetLines(ref ex, now, RadioLineKind.ReadyForDeparture, RadioLineKind.HoldShort, RadioLineKind.ReadbackHoldShort);
                return ex;
            case ExchangeType.LineUpCall when a.Phase == DeparturePhase.HoldingShort && (a.Clearances & ClearanceKind.LineUp) != 0:
                SetLines(ref ex, now, RadioLineKind.LineUp, RadioLineKind.ReadbackLineUp, grant: ClearanceKind.LineUp);
                ex.Detail = a.LineUpBehind;
                return ex;
            case ExchangeType.TakeoffCall when a.Phase == DeparturePhase.LinedUp && (a.Clearances & ClearanceKind.Takeoff) != 0:
                SetLines(ref ex, now, RadioLineKind.Takeoff, RadioLineKind.ReadbackTakeoff, grant: ClearanceKind.Takeoff);
                return ex;
            case ExchangeType.DepartureHandoff:
                SetLines(ref ex, now, RadioLineKind.ContactDeparture, RadioLineKind.ReadbackContactDeparture);
                return ex;
            case ExchangeType.Vacate:
                SetLines(ref ex, now, RadioLineKind.VacateContactGround, RadioLineKind.ReadbackVacateContactGround);
                ex.Detail = a.ExitTaxiway;
                return ex;
            case ExchangeType.ArrivalTaxi:
                SetLines(ref ex, now, RadioLineKind.RunwayVacated, RadioLineKind.TaxiToStand, RadioLineKind.ReadbackTaxiToStand, ClearanceKind.Taxi);
                ex.Detail = a.StandSpoken;
                return ex;
            case ExchangeType.CrossClearance when a.PendingCrossing is { } cross && a.CrossingsCleared > a.CrossingsDone:
                SetLines(ref ex, now, RadioLineKind.CrossRunway, RadioLineKind.ReadbackCrossRunway);
                ex.Detail = cross.Ident;
                s.CrossCalls = a.CrossingsDone + 1;
                return ex;
            case ExchangeType.CrossVacated:
                ex.K0 = RadioLineKind.RunwayVacated;
                ex.T0 = now;
                ex.Count = 1;
                return ex;
            case ExchangeType.UserTraffic when a.Phase == DeparturePhase.Taxi && _user is { } u:
                // El piloto adelante en el mismo sentido: "follow"; si no (cruza o viene de frente): "give way", con el lado.
                var heading = a.TaxiPath.HeadingAt(a.TaxiProgress);
                if (Math.Abs(GeoMath.AngleDiffDeg(heading, u.HeadingDeg)) < 45)
                {
                    SetLines(ref ex, now, RadioLineKind.Follow, RadioLineKind.ReadbackFollow);
                    return ex;
                }
                SetLines(ref ex, now, RadioLineKind.GiveWay, RadioLineKind.ReadbackGiveWay);
                ex.Detail = SideOf(a.Position, heading, a.Projection.ToLocal(u.Position)) > 0 ? RadioLine.LeftToRight : RadioLine.RightToLeft;
                return ex;
            default:
                return null;
        }
    }

    private static void SetLines(ref Active ex, double now, RadioLineKind k0, RadioLineKind k1, RadioLineKind? k2 = null,
        ClearanceKind grant = ClearanceKind.None)
    {
        ex.K0 = k0;
        ex.T0 = now;
        ex.K1 = k1;
        ex.T1 = ex.T0 + DurationS(k0) + LineGapS;
        ex.Count = 2;
        if (k2 is { } last)
        {
            ex.K2 = last;
            ex.T2 = ex.T1 + DurationS(k1) + LineGapS;
            ex.Count = 3;
        }
        ex.Grant = grant;
    }

    /// <summary>Positivo si <paramref name="p"/> está a la izquierda de quien va desde <paramref name="from"/> con ese rumbo.</summary>
    private static double SideOf(LocalPoint from, double headingDeg, LocalPoint p)
    {
        var rad = headingDeg * Math.PI / 180;
        var (e, n) = (Math.Sin(rad), Math.Cos(rad));
        return e * (p.NorthM - from.NorthM) - n * (p.EastM - from.EastM);
    }

    private static RadioLineKind Last(in Active ex) => ex.Count switch { 1 => ex.K0, 2 => ex.K1, _ => ex.K2 };

    /// <summary>Publica las líneas que ya tocan; al terminar el intercambio, lo que se autorizó pasa a ser válido.</summary>
    private void Transmit(double now)
    {
        for (var i = 0; i < _active.Count; i++)
        {
            var ex = _active[i];
            while (ex.Emitted < ex.Count)
            {
                var (kind, at) = ex.Emitted switch { 0 => (ex.K0, ex.T0), 1 => (ex.K1, ex.T1), _ => (ex.K2, ex.T2) };
                if (at > now)
                    break;
                Emit(ex, kind, at);
                ex.Emitted++;
            }
            if (ex.Emitted < ex.Count)
            {
                _active[i] = ex;
                continue;
            }
            Finish(ex);
            _active.RemoveAt(i);
            i--;
        }
    }

    private void Emit(in Active ex, RadioLineKind kind, double at)
    {
        var a = ex.Agent;
        var f = Frequencies;
        var next = kind switch
        {
            RadioLineKind.ContactTower or RadioLineKind.ReadbackContactTower => f?.TowerKhz ?? 0,
            RadioLineKind.ContactDeparture or RadioLineKind.ReadbackContactDeparture => f?.DepartureKhz ?? 0,
            RadioLineKind.VacateContactGround or RadioLineKind.ReadbackVacateContactGround => f?.GroundKhz ?? 0,
            _ => 0,
        };
        var station = kind is RadioLineKind.RequestPushback or RadioLineKind.PushbackApproved or RadioLineKind.Standby
            or RadioLineKind.ReadbackPushback or RadioLineKind.RequestTaxi or RadioLineKind.Taxi or RadioLineKind.ReadbackTaxi
            or RadioLineKind.ContactTower or RadioLineKind.ReadbackContactTower
            or RadioLineKind.RunwayVacated or RadioLineKind.TaxiToStand or RadioLineKind.ReadbackTaxiToStand
            or RadioLineKind.CrossRunway or RadioLineKind.ReadbackCrossRunway
            || ex.Type == ExchangeType.UserTraffic && f is { } g && ex.Khz == g.GroundKhz
            ? (f is { } fr && fr.GroundKhz == fr.TowerKhz ? AtcRole.Tower : AtcRole.Ground)
            : AtcRole.Tower;
        Lines.Enqueue(new RadioLine(kind, station, ex.Khz, a.ObjectId, a.Callsign, a.RunwayIdent, a.Taxiways, next, at, ex.Detail));
    }

    private void Finish(in Active ex)
    {
        var s = _agents[ex.Agent.ObjectId];
        s.Queued = false;
        if (ex.Grant != ClearanceKind.None)
            ex.Agent.RadioClearances |= ex.Grant;
        switch (ex.Type)
        {
            case ExchangeType.PushRequest or ExchangeType.PushApproval when ex.Grant == ClearanceKind.Pushback:
                s.PushGranted = true;
                break;
            case ExchangeType.TaxiRequest:
                s.TaxiDone = true;
                break;
            case ExchangeType.TowerHandoff:
                s.HandoffDone = true;
                break;
            case ExchangeType.Ready:
                s.ReadyCalled = true;
                s.LineUpGiven = ex.Grant == ClearanceKind.LineUp;
                break;
            case ExchangeType.LineUpCall:
                s.LineUpGiven = true;
                break;
            case ExchangeType.TakeoffCall:
                s.TakeoffGiven = true;
                break;
            case ExchangeType.Vacate:
                s.VacateDone = true;
                break;
            case ExchangeType.ArrivalTaxi:
                s.ArrivalTaxiDone = true;
                break;
            case ExchangeType.CrossClearance:
                ex.Agent.CrossingsHeard = s.CrossCalls;
                break;
            case ExchangeType.CrossVacated:
                s.CrossReports = s.CrossCalls;
                break;
        }
    }

    private double BusyUntil(int khz) => _busyUntil.TryGetValue(khz, out var t) ? t : double.MinValue;

    private bool OnAir(int khz)
    {
        for (var i = 0; i < _active.Count; i++)
        {
            if (_active[i].Khz == khz)
                return true;
        }
        return false;
    }

    /// <summary>Olvida a los aviones que ya no están (cada 10 s).</summary>
    private void Forget()
    {
        _gone.Clear();
        foreach (var (id, s) in _agents)
        {
            if (s.SeenTick < _tick - 50)
                _gone.Add(id);
        }
        foreach (var id in _gone)
            _agents.Remove(id);
        for (var i = _waiting.Count - 1; i >= 0; i--)
        {
            if (_gone.Contains(_waiting[i].Agent.ObjectId))
                _waiting.RemoveAt(i);
        }
    }

    /// <summary>Olvida todo (al limpiar el tráfico).</summary>
    public void Clear()
    {
        _agents.Clear();
        _waiting.Clear();
        _active.Clear();
        _busyUntil.Clear();
    }
}
