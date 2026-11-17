using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Engine;

/// <summary>Un obstáculo en tierra que no es un avión del motor (el avión del piloto).</summary>
/// <summary>
/// El avión del piloto en tierra para la seguridad del tráfico: posición, rumbo, envergadura y, para la caja que barre
/// (ADR-134), su velocidad y su largo (0: se estima por la envergadura).
/// </summary>
public readonly record struct GroundObstacle(GeoPoint Position, double HeadingDeg, double WingspanM, double SpeedKt = 0, double LengthM = 0);

/// <summary>Un cruce donde el piloto tiene el paso (Ground detiene a los IA antes).</summary>
public sealed record PilotPriority(GeoPoint Point);

/// <summary>
/// Seguridad del tráfico en tierra (RF-18, ADR-021): la red que impide choques y bloqueos aunque el ATC se equivoque.
/// Corre en cada tick antes de mover a los aviones y fija hasta dónde puede avanzar cada uno; detecta bloqueos y se los
/// informa a Ground, que decide cómo resolverlos. Pushback y pista los autorizan Ground y Tower (`Atc/`):
/// <list type="bullet">
/// <item>Reservas: cada cruce del rodaje (y el tramo hasta el siguiente, en su sentido) se reserva con anticipación;
/// sin la reserva el avión se detiene antes del cruce. Un tramo no se reserva si alguien lo usa en sentido contrario.
/// Primero en llegar, primero en pasar.</item>
/// <item>Distancia: si otro avión (o el del piloto) está sobre la ruta adelante, se mantiene detrás a distancia.</item>
/// <item>Pista: uno a la vez; para despegar, el anterior en el aire y a 6000 ft por la pista, y 2 min detrás de un
/// pesado (FAA JO 7110.65 §3-9-6 y estela).</item>
/// <item>Pushback: no empieza si hay tráfico rodando cerca de donde termina o si otro pushback usa ese lugar.</item>
/// </list>
/// Sin asignaciones por tick salvo al crecer los diccionarios la primera vez.
/// </summary>
public sealed class GroundCoordinator
{
    // Valores APROXIMADOS de operación de línea.
    /// <summary>Hasta dónde mira adelante para reservar cruces y ver tráfico (m).</summary>
    public const double LookaheadM = 250;
    /// <summary>Margen entre la nariz y el centro de un cruce ajeno al detenerse (m): media envergadura de quien cruza.</summary>
    public const double IntersectionStopM = 30;
    /// <summary>El cruce se libera cuando la cola lo dejó atrás este margen además de media envergadura (m).</summary>
    public const double IntersectionReleaseM = 15;
    /// <summary>Hueco libre entre la cola del de adelante y la nariz del de atrás, en rodaje (m): por el chorro.</summary>
    public const double FollowGapM = 40;
    /// <summary>Separación lateral máxima para considerar que otro avión está sobre la ruta (m).</summary>
    public const double PathLateralM = 12;
    /// <summary>Cruces separados por menos que la distancia de detención más esto (m) forman un solo bloque.</summary>
    public const double ClusterMarginM = 10;

    /// <summary>Margen entre puntas de ala al pasar junto a un avión detenido (m).</summary>
    public const double WingtipMarginM = 5;
    /// <summary>Un desvío deja al obstáculo a media envergadura de cada uno más esto (m).</summary>
    public const double RerouteClearanceM = 15;
    /// <summary>
    /// Cruces ajenos a menos de esto de uno de la ruta cuentan como el mismo (m): en los nudos de calles de SCEL hay nodos a
    /// 20–30 m que no comparten tramo, y dos aviones pasaban a la vez a menos de 45 m. Menor que los 43 m entre calles
    /// paralelas de plataforma, para no acoplarlas.
    /// </summary>
    public const double NearbyNodeM = 40;
    /// <summary>
    /// Hasta dónde, más allá del cruce que va a reservar, mira si viene alguien de frente por su ruta (m). Más lejos no lo
    /// detiene todavía: avanza y espera en el último cruce antes de la parte compartida.
    /// </summary>
    public const double OncomingLookM = 300;
    /// <summary>Distancia mínima entre la referencia del que espera un cruce y la ruta de quien lo cruza (m).</summary>
    public const double CrossingClearanceM = 45;
    /// <summary>Tramo de la ruta del que cruza, alrededor del cruce, que se mira para fijar la línea de espera (m).</summary>
    public const double CrossingWindowM = 60;
    /// <summary>Paso y retroceso máximo de la línea de espera respecto de la de siempre (m).</summary>
    public const double StopBackStepM = 5;
    public const double StopBackMaxM = 120; // 60 no alcanzaba en Boston (ADR-094): la llegada pasaba a 33 m
    /// <summary>Si la ruta del otro pasa a menos de esto de la línea de espera (sin compartir el nodo), también se corre (m).</summary>
    public const double StopNearRouteM = 80;
    /// <summary>Tramo de la ruta del otro antes del cruce que se mira para fijar la línea de espera (m).</summary>
    public const double StopApproachWindowM = 200;


    private readonly Dictionary<int, uint> _nodeHolder = [];
    private readonly Dictionary<int, LocalPoint> _nodePos = []; // posición de cada cruce reservado alguna vez (fija)
    private readonly Dictionary<long, int> _segmentUsers = [];
    private readonly Dictionary<long, uint> _segmentOwner = []; // el último que entró al tramo (para "a quién espera")
    private readonly Dictionary<uint, Reservations> _reservations = [];
    private double _time;

    // Bloqueos (RF-18b): a quién espera cada avión en este tick, cuánto lleva cada uno en un ciclo y orden de llegada
    // (el último en llegar es el que cede).
    private readonly Dictionary<uint, DepartureAgent> _byId = [];
    private readonly Dictionary<uint, double> _inCycleFor = [];
    private readonly Dictionary<uint, double> _cycleTotal = [];

    /// <summary>"Give way" de Ground (hito 4e-2): el avión se detiene en ese punto de su ruta mientras el otro empuja.</summary>
    private readonly Dictionary<long, double> _giveWay = []; // (quien cede, pushback) → punto de su ruta donde se detiene
    private readonly List<long> _giveWayDone = new(8);

    private static long GiveWayKey(uint agent, uint pusher) => ((long)agent << 32) | pusher;

    private readonly Dictionary<uint, long> _arrival = [];
    private readonly List<DepartureAgent> _cycle = new(16);
    private readonly List<DepartureAgent[]> _confirmed = [];
    private long _arrivals;

    /// <summary>Segundos en un ciclo de esperas antes de resolverlo, y cuánto espera sin reservar el que cede.</summary>
    public const double DeadlockConfirmS = 5;
    public const double YieldS = 20;
    /// <summary>Un bloqueo que sigue después de esto cuenta como no resuelto (métrica de TESTING.md §3).</summary>
    public const double DeadlockUnresolvedS = 120;
    /// <summary>Detenido en rodaje sin causa aparente durante esto: se registra y se le limpian las reservas.</summary>
    public const double StuckWithoutCauseS = 60;

    /// <summary>Métricas de bloqueos: detectados, resueltos, y ciclos que siguieron más de 120 s.</summary>
    public int DeadlocksDetected { get; private set; }
    public int DeadlocksResolved { get; internal set; }
    public int DeadlocksUnresolved { get; private set; }

    /// <summary>Registro (por avión) de bloqueos y su resolución.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Paso del piloto (hito 4e-2, prueba de Marco 2026-10-31): Ground le da prioridad en este cruce de su ruta porque
    /// lleva rato cediendo el paso. Los IA que todavía pueden frenar se detienen antes; los que ya están encima, pasan.
    /// La fija la interfaz (una referencia: se lee sin candado y sin asignar en el tick); null = sin prioridad.
    /// </summary>
    public PilotPriority? PilotPriority { get; set; }

    /// <summary>Distancias de la prioridad del piloto (m): se mira hasta aquí adelante, a menos de esto de la ruta, y el IA se detiene a este margen.</summary>
    public const double PriorityLookM = 300;
    public const double PriorityLateralM = 30;
    public const double PriorityClearM = 45;

    /// <summary>Los IA detenidos por la prioridad del piloto (de esa prioridad): siguen detenidos aunque ya estén cerca.</summary>
    private readonly HashSet<uint> _priorityHeld = [];
    private PilotPriority? _priorityOf;

    private sealed class Reservations
    {
        public int Acquired;   // marcas [0, Acquired) reservadas en algún momento
        public int Released;   // marcas [0, Released) ya liberadas
        public int SegmentFrom; // tramos contados: los que salen de las marcas [SegmentFrom, Acquired)
        public bool Departed;  // ya registró su despegue
        public double YieldUntil; // cediendo el paso: no reserva cruces nuevos hasta este momento
        public double StoppedFor; // detenido en rodaje sin causa
        public bool Unresolved;   // ya contado como no resuelto
        public readonly HashSet<long> Planned = []; // tramos que todavía va a recorrer (su intención, para los demás)
        public IReadOnlyList<TaxiMark>? PlannedFor;  // marcas con las que se armó Planned (cambia si se le cambia la ruta)
        // Lo que tomó de verdad (ADR-091): se libera esto, no lo que diga la lista de marcas, que cambia al alinearse o al
        // desviarse. Antes un cruce quedaba tomado por un avión que ya había despegado (Headless de LEMD).
        // Con capacidad de entrada: no crecen (no asignan) en el tick.
        public readonly List<int> HeldNodes = new(64);
        public readonly List<long> HeldSegments = new(128);
        // Tramos de un carril reservados enteros en su sentido (gestor de rodaje): marca donde empieza el tramo y clave.
        public readonly List<(int Start, long Key)> Claims = new(64);
    }

    // Gestor de rodaje (ADR-094): tramos reservados enteros, en un sentido, por quien entró primero.
    private readonly Dictionary<long, int> _claimUsers = [];
    private readonly Dictionary<long, uint> _claimOwner = [];

    // Aviso de llegada (ADR-094): rodaje previsto de las llegadas en final corta o en la pista (todavía sin avión en tierra).
    private readonly Dictionary<uint, (HashSet<long> Keys, HashSet<int> Nodes, double Near, double Until)> _arrivalPreview = [];
    private readonly List<uint> _previewDone = new(8);

    /// <summary>Vigencia máxima de un aviso de llegada (s): si no llega a tierra (motor y al aire, retirada), se olvida.</summary>
    public const double ArrivalPreviewS = 240;

    /// <summary>
    /// Desde cuántos segundos antes del umbral el aviso frena a los que ya ruedan (tramos y cruces de salida). Antes de eso
    /// solo lo miran los pushbacks: empujar y arrancar toma un par de minutos (ADR-095, Sídney: empujó sobre la ruta de una
    /// llegada que todavía no estaba a 4 km y la llegada quedó con la cola sobre la pista). APROXIMADO.
    /// </summary>
    public const double PreviewNearS = 75;

    /// <summary>
    /// Aviso de llegada (ADR-094, Gatwick): la llegada <paramref name="id"/> va a salir de la pista y rodar por estos tramos.
    /// Ninguna salida entra de frente a ellos mientras tanto (la llegada no tiene dónde esperar fuera de la pista). Se borra
    /// cuando la llegada aparece en tierra o al vencer.
    /// </summary>
    public void SetArrivalPreview(uint id, ArrivalRoute route, double etaS = 0)
    {
        if (!_arrivalPreview.TryGetValue(id, out var p))
            p = ([], [], 0, 0);
        p.Keys.Clear();
        foreach (var k in route.Keys)
            p.Keys.Add(k);
        p.Nodes.Clear();
        foreach (var n in route.ExitNodes)
            p.Nodes.Add(n);
        // Cerca (frena a los que ruedan) desde PreviewNearS antes del umbral; los pushbacks la miran desde que se avisa.
        _arrivalPreview[id] = (p.Keys, p.Nodes, _time + Math.Max(0, etaS - PreviewNearS), _time + etaS + ArrivalPreviewS);
    }

    /// <summary>
    /// ¿El tramo que recorre ahora lo va a recorrer de frente la llegada avisada <paramref name="id"/>? Entonces ya está
    /// dentro de su camino: no se lo detiene ahí por sus cruces de salida, que termine de pasar (ADR-095, Gatwick).
    /// </summary>
    private bool InsidePreview(uint id, long[] current) =>
        _arrivalPreview.TryGetValue(id, out var p) && Opposed(new TaxiMark(0, 0, current), p.Keys);

    /// <summary>¿Una llegada avisada va a pasar por este cruce al salir de la pista? (ninguna salida lo toma antes).</summary>
    private bool PreviewExitNode(int node, out uint id)
    {
        foreach (var (pid, p) in _arrivalPreview)
        {
            if (_time >= p.Near && p.Nodes.Contains(node))
            {
                id = pid;
                return true;
            }
        }
        id = 0;
        return false;
    }

    public void ClearArrivalPreview(uint id) => _arrivalPreview.Remove(id);

    /// <summary>Tramos del comienzo de la ruta de una salida de pista que se miran al elegirla (≈ 400–500 m).</summary>
    public const int ExitRouteKeys = 25;

    /// <summary>
    /// ¿Alguien rueda de frente por el comienzo de esta ruta de llegada (tiene tomado o reservado entero un tramo en
    /// contra)? Para elegir la salida de pista (ADR-096).
    /// </summary>
    public bool ExitRouteOpposed(ArrivalRoute route)
    {
        for (var i = 0; i < route.Keys.Length && i < ExitRouteKeys; i++)
        {
            var rev = TaxiMark.Reverse(route.Keys[i]);
            if (_segmentUsers.GetValueOrDefault(rev) > 0 || _claimUsers.GetValueOrDefault(rev) > 0)
                return true;
        }
        return false;
    }

    private void PruneArrivalPreviews()
    {
        if (_arrivalPreview.Count == 0)
            return;
        _previewDone.Clear();
        foreach (var (id, p) in _arrivalPreview)
        {
            // Hasta que la llegada en tierra tenga su intención registrada (si no, en ese tick nadie la ve).
            var onGround = _reservations.TryGetValue(id, out var r) && r.PlannedFor is not null;
            if (_time > p.Until || onGround)
                _previewDone.Add(id);
        }
        foreach (var id in _previewDone)
            _arrivalPreview.Remove(id);
    }


    /// <summary>Diferencia máxima de rumbo (o con el rumbo opuesto) para considerar que dos aviones van en calles paralelas (°).</summary>
    public const double ParallelMaxDeg = 20;

    /// <summary>
    /// Pérdida de separación en tierra entre dos aviones (métrica del Headless y de los tests, ADR-044): a menos de
    /// <paramref name="minM"/> entre referencias. En calles paralelas (rumbos parecidos u opuestos y ninguno sobre el eje del
    /// otro) lo que cuenta es el margen entre puntas de ala: las calles de plataforma de SCEL están a 43 m entre ejes.
    /// </summary>
    public static bool SeparationLost(DepartureAgent a, DepartureAgent b, double minM) =>
        SeparationLost(a.Position, a.HeadingDeg, a.WingspanM, b.Position, b.HeadingDeg, b.WingspanM, minM);

    public static bool SeparationLost(LocalPoint pa, double headingA, double wingspanA, LocalPoint pb, double headingB,
        double wingspanB, double minM)
    {
        var d = LocalPoint.Distance(pa, pb);
        if (d >= minM)
            return false;
        var diff = Math.Abs(GeoMath.AngleDiffDeg(headingA, headingB));
        // En ángulo (uniones y cruces): lo que cuenta es el espacio real entre los dos aviones, cada uno como una cruz de
        // fuselaje y alas (largo ≈ envergadura en los de línea). ADR-091: en la unión de A en LEMD dos esperaban a 44 m entre
        // centros, la separación OACI entre ejes de calles para clave C, con ~8 m entre ellos; se contaba como pérdida.
        if (Math.Min(diff, 180 - diff) > ParallelMaxDeg)
            return FootprintClearanceM(pa, headingA, wingspanA, pb, headingB, wingspanB) < WingtipMarginM;
        var lateral = d * Math.Abs(Math.Sin(GeoMath.AngleDiffDeg(headingA, Polyline.Bearing(pa, pb)) * GeoMath.DegToRad));
        return lateral <= PathLateralM || lateral - (wingspanA + wingspanB) / 2 < WingtipMarginM;
    }

    /// <summary>Distancia mínima (m) entre las siluetas de dos aviones, cada una como fuselaje + alas (dos segmentos).</summary>
    public static double FootprintClearanceM(LocalPoint pa, double headingA, double spanA, LocalPoint pb, double headingB, double spanB)
    {
        var (a1, a2, a3, a4) = Cross(pa, headingA, spanA);
        var (b1, b2, b3, b4) = Cross(pb, headingB, spanB);
        var m = SegDist(a1, a2, b1, b2);
        m = Math.Min(m, SegDist(a1, a2, b3, b4));
        m = Math.Min(m, SegDist(a3, a4, b1, b2));
        return Math.Min(m, SegDist(a3, a4, b3, b4));
    }

    private static (LocalPoint, LocalPoint, LocalPoint, LocalPoint) Cross(LocalPoint p, double heading, double span)
    {
        var half = span / 2;
        return (Polyline.Offset(p, heading, half), Polyline.Offset(p, heading + 180, half),
            Polyline.Offset(p, heading + 90, half), Polyline.Offset(p, heading - 90, half));
    }

    private static double SegDist(LocalPoint p1, LocalPoint p2, LocalPoint q1, LocalPoint q2)
    {
        if (SegmentsCross(p1, p2, q1, q2))
            return 0;
        return Math.Min(Math.Min(PointSeg(p1, q1, q2), PointSeg(p2, q1, q2)), Math.Min(PointSeg(q1, p1, p2), PointSeg(q2, p1, p2)));
    }

    private static bool SegmentsCross(LocalPoint p1, LocalPoint p2, LocalPoint q1, LocalPoint q2)
    {
        static double C(LocalPoint o, LocalPoint x, LocalPoint y) => (x.EastM - o.EastM) * (y.NorthM - o.NorthM) - (x.NorthM - o.NorthM) * (y.EastM - o.EastM);
        return C(q1, q2, p1) * C(q1, q2, p2) < 0 && C(p1, p2, q1) * C(p1, p2, q2) < 0;
    }

    private static double PointSeg(LocalPoint p, LocalPoint a, LocalPoint b)
    {
        var dx = b.EastM - a.EastM;
        var dy = b.NorthM - a.NorthM;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-9 ? 0 : Math.Clamp(((p.EastM - a.EastM) * dx + (p.NorthM - a.NorthM) * dy) / len2, 0, 1);
        return LocalPoint.Distance(p, new LocalPoint(a.EastM + t * dx, a.NorthM + t * dy));
    }

    /// <summary>Quién tiene reservado un cruce (0 si nadie): para el radar y los tests.</summary>
    public uint HolderOf(int node) => _nodeHolder.GetValueOrDefault(node);

    /// <summary>Un tick: calcula los límites de avance de todos los aviones y detecta bloqueos.</summary>
    public void Update(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user, double dt)
    {
        _time += dt;
        _confirmed.Clear();
        PruneArrivalPreviews();
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            try
            {
                UpdateOne(a, agents, user);
            }
            catch (Exception)
            {
                // Un error de coordinación no detiene el motor (RNF-06): sin límites para este avión en este tick.
                a.TaxiLimitS = double.PositiveInfinity;
            }
        }
        for (var i = 0; i < agents.Count; i++)
            _prevWait[agents[i].ObjectId] = (agents[i].Hold, agents[i].HoldFor);
        ResolveDeadlocks(agents, dt);
    }

    /// <summary>A quién esperaba cada avión en la pasada anterior (ADR-091, para la espera mutua).</summary>
    private readonly Dictionary<uint, (HoldKind Hold, uint For)> _prevWait = new(128);

    // ---------- Bloqueos (RF-18b) ----------

    private static bool IsWait(HoldKind h) => h is HoldKind.Intersection or HoldKind.Traffic or HoldKind.PushbackTraffic;

    /// <summary>
    /// Busca ciclos de espera (A espera a B, B espera a A, o más largos). Si uno dura más de
    /// <see cref="DeadlockConfirmS"/>, cede el último en llegar: si tiene un cruce que otro del ciclo espera, lo devuelve
    /// (si no lo alcanzó) y no reserva durante <see cref="YieldS"/>; si está de frente con otro, busca un desvío. También
    /// detecta aviones detenidos en rodaje sin causa.
    /// </summary>
    private void ResolveDeadlocks(IReadOnlyList<DepartureAgent> agents, double dt)
    {
        _byId.Clear();
        for (var i = 0; i < agents.Count; i++)
        {
            _byId[agents[i].ObjectId] = agents[i];
        }

        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (!_reservations.TryGetValue(a.ObjectId, out var res))
                continue;
            // Detenido rodando sin nada que lo explique.
            if (a.Phase == DeparturePhase.Taxi && a.SpeedKt < 0.1 && a.Hold == HoldKind.None)
            {
                res.StoppedFor += dt;
                if (res.StoppedFor >= StuckWithoutCauseS)
                {
                    Log?.Invoke(Tr.F("{0}: detenido {1:0} s sin causa en rodaje; se limpian sus reservas.", a.Callsign, res.StoppedFor));
                    ReleaseUnreached(a, res);
                    res.StoppedFor = 0;
                }
            }
            else
            {
                res.StoppedFor = 0;
            }

            if (!InCycle(a))
            {
                _inCycleFor.Remove(a.ObjectId);
                _cycleTotal.Remove(a.ObjectId);
                res.Unresolved = false;
                continue;
            }
            _inCycleFor[a.ObjectId] = _inCycleFor.GetValueOrDefault(a.ObjectId) + dt;
            // "Sin resolver" con el tiempo total en el ciclo: el de arriba se reinicia en cada detección para reintentar, y
            // así un bloqueo permanente nunca llegaba a contarse (se vio con la radio del hito 4e-1).
            var t = _cycleTotal.GetValueOrDefault(a.ObjectId) + dt;
            _cycleTotal[a.ObjectId] = t;
            if (t >= DeadlockUnresolvedS && !res.Unresolved)
            {
                res.Unresolved = true;
                DeadlocksUnresolved++;
                Log?.Invoke(Tr.F("{0}: bloqueo sin resolver hace {1:0} s.", a.Callsign, t));
            }
        }

        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (!_inCycleFor.TryGetValue(a.ObjectId, out var t) || t < DeadlockConfirmS)
                continue;
            CollectCycle(a);
            DeadlocksDetected++;
            _confirmed.Add([.. _cycle]); // lo resuelve Ground (ADR-021)
            // Se reintenta dentro de DeadlockConfirmS si sigue (todo el ciclo vuelve a contar desde cero).
            foreach (var m in _cycle)
                _inCycleFor[m.ObjectId] = Math.Min(_inCycleFor.GetValueOrDefault(m.ObjectId), 0.001);
        }
    }

    /// <summary>Para tests: si <paramref name="a"/> está en un ciclo de esperas entre <paramref name="agents"/>.</summary>
    internal bool InWaitCycle(DepartureAgent a, IReadOnlyList<DepartureAgent> agents)
    {
        _byId.Clear();
        foreach (var x in agents)
            _byId[x.ObjectId] = x;
        return InCycle(a);
    }

    /// <summary>true si siguiendo "a quién espera" desde a se vuelve a a.</summary>
    private bool InCycle(DepartureAgent a)
    {
        var current = a;
        for (var steps = 0; steps <= _byId.Count; steps++)
        {
            if (!IsWait(current.Hold) || current.HoldFor == 0 || current.SpeedKt > 0.5 || !_byId.TryGetValue(current.HoldFor, out var next))
                return false;
            if (ReferenceEquals(next, a))
                return true;
            current = next;
        }
        return false;
    }

    private void CollectCycle(DepartureAgent a)
    {
        _cycle.Clear();
        var current = a;
        do
        {
            _cycle.Add(current);
            current = _byId[current.HoldFor];
        }
        while (!ReferenceEquals(current, a) && _cycle.Count <= _byId.Count);
    }

    // ---------- Lo que usa Ground (ADR-021): la seguridad informa, Ground decide ----------

    /// <summary>Bloqueos confirmados en este tick (cada uno, los aviones del ciclo).</summary>
    internal IReadOnlyList<DepartureAgent[]> ConfirmedCycles => _confirmed;

    /// <summary>Orden de prioridad (menor = primero en moverse).</summary>
    internal long PriorityOf(DepartureAgent a) => _arrival.GetValueOrDefault(a.ObjectId, long.MaxValue);

    /// <summary>true si alguien de mayor prioridad va a recorrer al revés parte de la ruta de a (incluido el tramo inicial).</summary>
    internal bool OncomingOnRoute(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, out uint who)
    {
        if (_reservations.TryGetValue(a.ObjectId, out var own))
            UpdatePlanned(a, own);
        if (PlannedOncoming(a, a.Marks, 0, agents, out who, includeStart: true, timed: true))
            return true;
        // Ni si una llegada que ya rueda a su puesto (cualquiera sea su prioridad) va a recorrer de frente el comienzo de su
        // rodaje y no alcanza a salir antes (ADR-096, Dallas: empujó sobre la ruta de una llegada que venía a un puesto vecino).
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ReferenceEquals(o, a) || !o.Inbound || o.Phase != DeparturePhase.Taxi || !_reservations.TryGetValue(o.ObjectId, out var other))
                continue;
            foreach (var key in a.StartEdges)
            {
                if (other.Planned.Contains(TaxiMark.Reverse(key)) && !PassesBefore(a, a.Marks.Count > 0 ? a.Marks[0].S : 0, o, TaxiMark.Reverse(key)))
                {
                    who = o.ObjectId;
                    return true;
                }
            }
            for (var k = 0; k < a.Marks.Count && a.Marks[k].S < PushPreviewLookM; k++)
            {
                foreach (var key in a.Marks[k].Edges)
                {
                    if (other.Planned.Contains(TaxiMark.Reverse(key)) && !PassesBefore(a, a.Marks[k].S, o, TaxiMark.Reverse(key)))
                    {
                        who = o.ObjectId;
                        return true;
                    }
                }
            }
        }
        // Tampoco si una llegada avisada (en final o en la pista) va a rodar de frente por el comienzo de su rodaje: el
        // pushback la dejaría nariz con nariz (ADR-095, México: empujó a la calle B con la llegada viniendo por ella).
        foreach (var (id, p) in _arrivalPreview)
        {
            var hit = Opposed(new TaxiMark(0, 0, a.StartEdges), p.Keys);
            for (var k = 0; k < a.Marks.Count && !hit && a.Marks[k].S < PushPreviewLookM; k++)
                hit = Opposed(a.Marks[k], p.Keys);
            if (hit)
            {
                who = id;
                return true;
            }
        }
        return false;
    }

    /// <summary>Comienzo del rodaje (m) que se compara con las llegadas avisadas antes de autorizar un pushback.</summary>
    public const double PushPreviewLookM = 300;

    /// <summary>
    /// Margen (s) con que el que va a empujar tiene que dejar atrás el tramo compartido antes de que llegue el que viene en
    /// sentido contrario. APROXIMADO.
    /// </summary>
    public const double OncomingMarginS = 60;

    /// <summary>
    /// ¿<paramref name="o"/> llega al tramo <paramref name="reverseKey"/> (en sentido contrario) bastante después de que
    /// <paramref name="a"/> lo haya dejado atrás (a <paramref name="aClearS"/> m de su rodaje)? Entonces a puede salir antes.
    /// Prueba de Marco 2026-11-06: LAN1306 esperó 9 min en el puesto a DAP439, que rodaba lejos por E-K-L-Q-G-P.
    /// </summary>
    private static bool PassesBefore(DepartureAgent a, double aClearS, DepartureAgent o, long reverseKey)
    {
        var oMarks = o.Marks;
        var sO = double.NaN;
        for (var i = 0; i < oMarks.Count; i++)
        {
            if (Array.IndexOf(oMarks[i].Edges, reverseKey) >= 0)
            {
                sO = i > 0 ? oMarks[i - 1].S : 0;
                break;
            }
        }
        if (double.IsNaN(sO))
            return false;
        var taxiing = o.Phase == DeparturePhase.Taxi;
        var tOther = (taxiing ? 0 : o.PushbackRemainingS) + Math.Max(0, sO - (taxiing ? o.TaxiProgress : 0)) / Math.Max(1, o.TaxiSpeedMps);
        // El que empuja (o ya rueda), a 70 % de su velocidad máxima (curvas y arranque): la estimación prudente.
        var mineLeft = Math.Max(0, aClearS - (a.Phase == DeparturePhase.Taxi ? a.TaxiProgress : 0));
        var tMe = (a.Phase == DeparturePhase.Taxi ? 0 : a.PushbackRemainingS) + mineLeft / Math.Max(1, 0.7 * a.TaxiSpeedMps);
        return tOther > tMe + OncomingMarginS;
    }

    /// <summary>Devuelve los cruces que a reservó y no alcanzó, y no reserva otros por <paramref name="seconds"/>.</summary>
    internal int GiveWay(DepartureAgent a, double seconds)
    {
        if (!_reservations.TryGetValue(a.ObjectId, out var res))
            return 0;
        var n = ReleaseUnreached(a, res);
        // Último recurso (ADR-091, unión de A en LEMD): detenido en su línea con el cruce tomado, pero sin haber entrado en
        // él, y el otro del bloqueo lo espera: se lo devuelve igual.
        if (n == 0 && a.SpeedKt < 1)
            n = ReleaseNotEntered(a, res);
        if (n > 0)
            res.YieldUntil = _time + seconds;
        return n;
    }

    /// <summary>Libera los cruces reservados cuyo centro todavía está por delante de la nariz del avión.</summary>
    private int ReleaseNotEntered(DepartureAgent a, Reservations res)
    {
        var marks = a.Marks;
        var progress = a.TaxiProgress;
        var released = 0;
        while (res.Acquired > res.Released && res.Acquired - 1 < marks.Count && marks[res.Acquired - 1].S > progress + a.NoseGearM + 2)
        {
            var k = res.Acquired - 1;
            if (_nodeHolder.GetValueOrDefault(marks[k].Node) == a.ObjectId)
                _nodeHolder.Remove(marks[k].Node);
            res.HeldNodes.Remove(marks[k].Node);
            if (k >= res.SegmentFrom)
                ReleaseEdges(marks[k], res);
            res.Acquired = k;
            released++;
        }
        ReleaseClaimsFrom(res, res.Acquired);
        return released;
    }

    /// <summary>Tras cambiarle la ruta a a: libera lo reservado con la ruta vieja y empieza de cero.</summary>
    internal void RouteChanged(DepartureAgent a, IReadOnlyList<TaxiMark> oldMarks)
    {
        if (_reservations.TryGetValue(a.ObjectId, out var res))
            ReleaseAll(a.ObjectId, oldMarks, res);
        _reservations[a.ObjectId] = new Reservations { YieldUntil = _time + 2 };
    }

    /// <summary>Posición del avión y lo que le queda de rodaje por delante (400 m, cada 20 m): lo que un desvío evita.</summary>
    internal static List<LocalPoint> AheadOfAgent(DepartureAgent o) => AheadOf(o);

    internal void Report(string message) => Log?.Invoke(message);

    /// <summary>
    /// Ground le indica a <paramref name="a"/> ceder el paso a <paramref name="pusher"/>, que va a empujar adelante: se
    /// detiene en <paramref name="stopS"/> (m de avance) hasta que el otro termina el pushback y empieza a rodar.
    /// </summary>
    internal void GiveWay(DepartureAgent a, double stopS, DepartureAgent pusher)
    {
        var key = GiveWayKey(a.ObjectId, pusher.ObjectId);
        if (!_giveWay.TryGetValue(key, out var old))
            Log?.Invoke(Tr.F("{0}: Ground le indica ceder el paso a {1}, que empuja adelante.", a.Callsign, pusher.Callsign));
        else
            stopS = Math.Min(stopS, old);
        _giveWay[key] = stopS;
    }

    private static List<LocalPoint> AheadOf(DepartureAgent o)
    {
        var points = new List<LocalPoint> { o.Position };
        if (o.OnTaxiPath)
        {
            for (var s = o.TaxiProgress; s < Math.Min(o.TaxiPath.Length, o.TaxiProgress + 400); s += 20)
                points.Add(o.TaxiPath.PointAt(s));
        }
        return points;
    }


    /// <summary>Devuelve los cruces reservados que el avión todavía no alcanzó. Cuántos devolvió.</summary>
    private int ReleaseUnreached(DepartureAgent a, Reservations res)
    {
        var marks = a.Marks;
        var stopOffset = a.NoseGearM + 4 + IntersectionStopM;
        var progress = a.TaxiProgress;
        var released = 0;
        while (res.Acquired > res.Released && marks[res.Acquired - 1].S - stopOffset > progress)
        {
            var k = res.Acquired - 1;
            if (_nodeHolder.GetValueOrDefault(marks[k].Node) == a.ObjectId)
                _nodeHolder.Remove(marks[k].Node);
            if (k >= res.SegmentFrom)
                ReleaseEdges(marks[k], res);
            res.Acquired = k;
            released++;
        }
        ReleaseClaimsFrom(res, res.Acquired);
        return released;
    }

    private void UpdateOne(DepartureAgent a, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        if (!_reservations.TryGetValue(a.ObjectId, out var res))
            _reservations[a.ObjectId] = res = new Reservations();

        // Registro del despegue (para la separación de la salida siguiente) y liberación de todo lo de tierra.
        if (a.Phase is DeparturePhase.Climb or DeparturePhase.Departed)
        {
            if (!res.Departed)
            {
                res.Departed = true;
                ReleaseAll(a.ObjectId, a, res);
            }
            return;
        }

        // Una llegada ya en su puesto suelta lo que le quede: el tramo del último cruce al puesto, que tuvo tomado hasta
        // entrar (ADR-128). Si no, esa calle quedaría cerrada en sentido contrario mientras esté estacionada.
        if (a.Inbound && a.Phase == DeparturePhase.AtStand && (res.HeldSegments.Count > 0 || res.HeldNodes.Count > 0))
            ReleaseAll(a.ObjectId, a, res);

        // Prioridad en tierra: la del primero al que Ground le autorizó el pushback (ya está en movimiento).
        if (a.Phase != DeparturePhase.DoorsClosing && (a.Phase != DeparturePhase.BeaconOn || a.IsCleared(ClearanceKind.Pushback))
            && !_arrival.ContainsKey(a.ObjectId))
            _arrival[a.ObjectId] = ++_arrivals;
        a.TaxiLimitS = a.OnTaxiPath ? TaxiLimit(a, res, agents, user) : double.PositiveInfinity;
    }

    // ---------- Rodaje: reservas y distancia ----------

    private double TaxiLimit(DepartureAgent a, Reservations res, IReadOnlyList<DepartureAgent> agents, GroundObstacle? user)
    {
        var progress = a.TaxiProgress;
        var marks = a.Marks;
        var limit = double.PositiveInfinity;
        UpdatePlanned(a, res);

        // Liberar lo que ya quedó atrás: el cruce cuando la cola lo pasó; el tramo anterior, a la vez.
        var release = a.WingspanM / 2 + IntersectionReleaseM;
        while (res.Released < res.Acquired && marks[res.Released].S + release < progress)
        {
            ReleaseMark(a.ObjectId, marks, res.Released, res);
            res.Released++;
        }

        // Primero el tráfico que está sobre la ruta adelante (otros aviones del motor y el del piloto).
        var path = a.TaxiPath;
        var here = a.Position;
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ReferenceEquals(o, a) || !o.OnGround || o.Phase == DeparturePhase.Departed)
                continue;
            // Todavía en su puesto (sin pushback): es un estacionado más, y el puesto está diseñado para que se ruede por
            // detrás. Antes lo esperaba como tráfico y el del puesto no podía empujar porque el otro estaba detrás: bloqueo
            // permanente (HDL040/HDL041 en scel-busy-morning). Si empuja, lo maneja el "ceder el paso al pushback" de abajo.
            if (o.Phase is DeparturePhase.DoorsClosing or DeparturePhase.BeaconOn or DeparturePhase.AtStand)
                continue;
            // Detenido al costado de la ruta: lo que importa es la punta del ala.
            var lateral = o.SpeedKt < 1 ? Math.Max(PathLateralM, (a.WingspanM + o.WingspanM) / 2 + WingtipMarginM) : PathLateralM;
            var l = FollowLimit(path, here, progress, o.Position, (a.WingspanM + o.WingspanM) / 2, lateral);
            // En una calle paralela demasiado cerca: no se pone a la par del de adelante.
            if (o.Phase == DeparturePhase.Taxi)
                l = Math.Min(l, AbeamLimit(a, o, progress));
            // Espera mutua (ADR-091, Headless de LEMD: dos que llegaban por calles vecinas al mismo punto de espera se veían
            // "adelante" uno al otro y no se movía ninguno): si el otro está detenido esperándome a mí y yo llegué antes a la
            // fila, sigo (despacio, por la seguridad de siempre con los demás); él me sigue después.
            // (Con la espera de la pasada anterior: AirportControl la borra al empezar cada una.)
            if (!double.IsPositiveInfinity(l) && o.SpeedKt < 1 && _prevWait.TryGetValue(o.ObjectId, out var ow)
                && ow.Hold == HoldKind.Traffic && ow.For == a.ObjectId && PriorityOf(a) < PriorityOf(o))
                l = double.PositiveInfinity;

            if (l < limit)
            {
                limit = l;
                a.Hold = HoldKind.Traffic;
                a.HoldFor = o.ObjectId;
            }
        }
        // Cediendo el paso a un pushback (Ground): hasta que el otro empiece a rodar.
        // Puede ceder a varios a la vez (ADR-044: con uno solo, el segundo pushback reemplazaba al primero y el que cedía
        // pasaba a 20 kt junto al que ya empujaba).
        _giveWayDone.Clear();
        foreach (var (key, stopS) in _giveWay)
        {
            if ((uint)(key >> 32) != a.ObjectId)
                continue;
            var pusherId = (uint)key;
            _byId.TryGetValue(pusherId, out var pusher);
            // Mientras el pushback siga autorizado (Ground puede revisarlo hasta decirlo por radio) o en curso.
            if (pusher is { Phase: DeparturePhase.Pushback or DeparturePhase.PushbackComplete }
                || pusher is { Phase: DeparturePhase.BeaconOn } && (pusher.Clearances & ClearanceKind.Pushback) != 0)
            {
                if (stopS < limit)
                {
                    limit = stopS;
                    a.Hold = HoldKind.Traffic;
                    a.HoldFor = pusherId;
                }
            }
            else
            {
                _giveWayDone.Add(key);
            }
        }
        foreach (var key in _giveWayDone)
            _giveWay.Remove(key);
        if (user is { } u)
        {
            var l = FollowLimit(path, here, progress, a.Projection.ToLocal(u.Position), (a.WingspanM + u.WingspanM) / 2, PathLateralM);
            if (l < limit)
            {
                limit = l;
                a.Hold = HoldKind.UserAircraft;
                a.HoldFor = 0;
            }
        }
        // La caja del piloto (ADR-134): envergadura y largo de los dos, y la zona que el piloto barre en los próximos
        // segundos si se mueve. El IA se detiene antes de que su caja toque esa zona, con margen. Antes solo frenaba cuando
        // el piloto ya estaba sobre su calle, y se detenía en la unión, delante de él (vuelo KMIA-KRSW de Marco).
        if (user is { } uh && a.Phase == DeparturePhase.Taxi
            && HitboxStop(path, progress, a.WingspanM, a.Projection.ToLocal(uh.Position), uh) is { } hit && hit < limit)
        {
            limit = hit;
            a.Hold = HoldKind.UserAircraft;
            a.HoldFor = 0;
        }
        // El cruce donde Ground le dio el paso al piloto: se detiene antes, si todavía puede frenar con suavidad.
        if (PilotPriority is { } pr && a.Phase == DeparturePhase.Taxi)
        {
            if (!ReferenceEquals(pr, _priorityOf))
            {
                _priorityOf = pr;
                _priorityHeld.Clear();
            }
            var at = a.Projection.ToLocal(pr.Point);
            var sp = path.ProjectWithin(at, progress, Math.Min(path.Length, progress + PriorityLookM), out var lat);
            var stop = sp - a.WingspanM / 2 - PriorityClearM;
            var speedMps = a.SpeedKt * 0.514444;
            var brake = speedMps * speedMps / (2 * 0.5);
            // Uno que ya está encima (no alcanza a frenar) pasa; el que se detuvo, sigue detenido.
            var held = _priorityHeld.Contains(a.ObjectId);
            if (lat < PriorityLateralM && sp > progress && (held || stop - progress > brake) && stop < limit)
            {
                if (!held)
                    _priorityHeld.Add(a.ObjectId);
                limit = Math.Max(progress, stop);
                a.Hold = HoldKind.UserAircraft;
                a.HoldFor = 0;
            }
        }

        // Todavía en el comienzo de su rodaje (antes de su primer cruce): si alguien ya tiene tomado de frente ese trecho
        // (una llegada entrando a un puesto vecino, ADR-128), se queda donde está hasta que pase. Sin esto avanzaba hasta
        // quedar junto al punto donde el otro dobla.
        if (res.Released == 0 && !a.Inbound && OncomingOn(new TaxiMark(0, 0, a.StartEdges), out var ahead) && ahead != a.ObjectId
            && progress < limit)
        {
            limit = progress;
            a.Hold = HoldKind.Intersection;
            a.HoldFor = ahead;
        }

        // Después reservar los cruces hacia adelante, en orden, pero solo hasta donde puede llegar: un cruce detrás del
        // tráfico que tiene adelante se reserva cuando ese tráfico lo pase (si no, el de adelante quedaría esperando
        // al de atrás). El primero que no se puede reservar fija el límite.
        var stopOffset = a.NoseGearM + 4 + IntersectionStopM; // de la referencia del avión al centro del cruce

        // Si se le puso tráfico delante, devuelve los cruces reservados más allá de ese tráfico que todavía no alcanzó:
        // los necesita primero el de adelante (si no, se esperan mutuamente).
        while (res.Acquired > res.Released && marks[res.Acquired - 1].S - stopOffset > limit
               && marks[res.Acquired - 1].S - stopOffset > progress)
        {
            var k = res.Acquired - 1;
            if (_nodeHolder.GetValueOrDefault(marks[k].Node) == a.ObjectId)
                _nodeHolder.Remove(marks[k].Node);
            if (k >= res.SegmentFrom)
                ReleaseEdges(marks[k], res);
            res.Acquired = k;
        }
        ReleaseClaimsFrom(res, res.Acquired);

        for (var i = res.Acquired; i < marks.Count && marks[i].S <= progress + LookaheadM;)
        {
            var m = marks[i];
            if (m.S - stopOffset > limit)
                break;
            // Cruces más cerca entre sí que la distancia para detenerse se reservan juntos o ninguno: un avión nunca
            // queda detenido entre dos cruces pegados (se vio en SCEL: cada uno tomaba uno y esperaba el otro).
            var last = i;
            while (last + 1 < marks.Count && marks[last + 1].S - marks[last].S < stopOffset + ClusterMarginM)
                last++;
            uint holder = 0;
            var heldNode = -1; // el cruce del otro que bloquea (para la línea de espera)
            // Cediendo el paso (bloqueo resuelto): no reserva cruces nuevos por un rato.
            var blocked = _time < res.YieldUntil;
            for (var j = i; j <= last && !blocked; j++)
            {
                var h = _nodeHolder.GetValueOrDefault(marks[j].Node);
                if (h != 0 && h != a.ObjectId)
                {
                    blocked = true;
                    holder = h;
                    heldNode = marks[j].Node;
                }
                else if (NearbyHeld(a, marks, path.PointAt(marks[j].S), out var nearHolder, out var nearNode))
                {
                    blocked = true;
                    holder = nearHolder; // otro pasa por un cruce pegado a este
                    heldNode = nearNode;
                }
                else if (OncomingOn(marks[j], out var oncoming) && oncoming != a.ObjectId)
                {
                    blocked = true;
                    holder = oncoming; // viene alguien de frente por esa calle
                }
                else if (!a.Inbound && PreviewExitNode(marks[j].Node, out var arriving)
                         && !InsidePreview(arriving, i > 0 ? marks[i - 1].Edges : a.StartEdges))
                {
                    blocked = true;
                    holder = arriving; // una llegada está por salir de la pista por este cruce
                }
            }
            // Prioridad del que salió primero: no entra a un tramo que uno de mayor prioridad todavía tiene que recorrer en
            // sentido contrario (espera antes de la parte compartida, como lo ordenaría Ground).
            // (Salvo una llegada que todavía no libró la pista: esperaba con la cola sobre ella a una salida que recién
            // empujaba a 700 m, y las siguientes hacían motor y al aire. ADR-095, Sídney.)
            // Solo si el otro llega antes a la parte compartida (ADR-096, Dallas: todos esperaban en cadena a uno detenido
            // tras el pushback a 3 km, que nunca llegaba).
            if (!blocked && !a.OnRunwayInbound && PlannedOncoming(a, marks, i, agents, out var first, timed: true))
            {
                blocked = true;
                holder = first;
            }
            // Tampoco entra si más adelante en su ruta viene alguien de frente que ya entró a esa calle: sin cruces para
            // apartarse quedarían nariz con nariz. Así lo hace un controlador: espera en el último cruce a que pase.
            // Solo cerca (prueba de Marco, 2026-11-01): si el de frente está lejos, avanza y espera en un cruce más adelante.
            for (var k = last + 1; k < marks.Count && !blocked && marks[k].S <= marks[last].S + OncomingLookM; k++)
            {
                if (OncomingOn(marks[k], out var oncoming) && oncoming != a.ObjectId)
                {
                    blocked = true;
                    holder = oncoming;
                }
            }
            // Gestor de rodaje (ADR-094, Headless de Gatwick: una salida y una llegada entraron a la vez por los dos extremos
            // de P y quedaron nariz con nariz): si alguien en movimiento va a recorrer de frente el tramo que empieza aquí,
            // solo se entra si nadie lo tiene tomado ni reservado en contra; al entrar se reserva entero (abajo).
            // Ya dentro de un tramo que reservó entero: lo termina (Montreal: lo frenaba a mitad el aviso de otra llegada).
            if (!blocked && !InOwnCorridor(res, marks, i, last) && !CorridorFree(a, marks, i, last, agents, out var against))
            {
                blocked = true;
                holder = against;
            }
            // Esperando a uno de frente (sin cruce tomado): la línea se corre igual fuera de su paso por este cruce, si pasa
            // por él (ADR-094, Gatwick: la salida esperaba a 35 m de la curva por donde la llegada doblaba y la trababa).
            if (blocked && heldNode < 0)
                heldNode = m.Node;
            // Una llegada que todavía no libró la pista no espera un cruce reservado por otro que no está ahí: lo pasa para
            // dejar la pista libre (ADR-091, Headless de LEMD: las llegadas esperaban sobre la pista un cruce que tenía la
            // anterior más adelante y la siguiente hacía motor y al aire). Si el otro está ahí, lo frena el tráfico de siempre.
            // Solo si su línea de espera queda sobre la pista (ADR-094, Gatwick: con 150 m de margen la llegada entraba de
            // frente a P con una salida adentro; puede esperar en la calle de salida con la cola fuera del punto de espera).
            if (blocked && a.Inbound && progress < a.VacatedAt && StopLine(a, m.S - stopOffset, holder, heldNode) < a.VacatedAt
                && (!_byId.TryGetValue(holder, out var hb) || LocalPoint.Distance(hb.Position, path.PointAt(m.S)) > CrossingClearanceM))
                blocked = false;
            if (blocked)
            {
                limit = Math.Max(progress, StopLine(a, m.S - stopOffset, holder, heldNode));
                a.Hold = HoldKind.Intersection;
                a.HoldFor = holder;
                break;
            }
            for (var j = i; j <= last; j++)
            {
                _nodeHolder[marks[j].Node] = a.ObjectId;
                _nodePos[marks[j].Node] = path.PointAt(marks[j].S);
                res.HeldNodes.Add(marks[j].Node);
                foreach (var key in marks[j].Edges)
                {
                    _segmentUsers[key] = _segmentUsers.GetValueOrDefault(key) + 1;
                    _segmentOwner[key] = a.ObjectId;
                    res.HeldSegments.Add(key);
                }
            }
            ClaimCorridor(a, res, marks, i, last, agents);
            res.Acquired = last + 1;
            i = last + 1;
        }
        return limit;
    }

    private static bool Moving(DepartureAgent o) =>
        o.OnGround && (o.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete or DeparturePhase.Taxi
                       || (o.Phase == DeparturePhase.BeaconOn && o.IsCleared(ClearanceKind.Pushback)));

    private static bool Opposed(TaxiMark m, HashSet<long> planned)
    {
        foreach (var key in m.Edges)
        {
            if (planned.Contains(TaxiMark.Reverse(key)))
                return true;
        }
        return false;
    }

    /// <summary>¿Alguien en movimiento (o, para una salida, una llegada avisada) va a recorrer de frente un tramo de la marca?</summary>
    private bool OpposedByAny(DepartureAgent a, TaxiMark m, IReadOnlyList<DepartureAgent> agents) =>
        OpposedByAny(a, m.Edges, agents);

    private bool OpposedByAny(DepartureAgent a, long[] edges, IReadOnlyList<DepartureAgent> agents)
    {
        var m = new TaxiMark(0, 0, edges);
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (!ReferenceEquals(o, a) && Moving(o) && _reservations.TryGetValue(o.ObjectId, out var other) && !other.Departed
                && Opposed(m, other.Planned))
                return true;
        }
        if (!a.Inbound)
        {
            foreach (var (_, p) in _arrivalPreview)
            {
                if (_time >= p.Near && Opposed(m, p.Keys))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tramo compartido de frente que empieza en las marcas [from, last]: su primera y última marca (hasta donde ya nadie
    /// viene de frente), o (-1, -1).
    /// </summary>
    private (int Start, int End) Corridor(DepartureAgent a, IReadOnlyList<TaxiMark> marks, int from, int last,
        IReadOnlyList<DepartureAgent> agents)
    {
        var start = -1;
        for (var k = from; k <= last && k < marks.Count && start < 0; k++)
        {
            if (OpposedByAny(a, marks[k], agents))
                start = k;
        }
        if (start < 0)
            return (-1, -1);
        var end = start;
        while (end + 1 < marks.Count && OpposedByAny(a, marks[end + 1], agents))
            end++;
        return (start, end);
    }

    /// <summary>
    /// Gestor de rodaje (ADR-094): true si <paramref name="a"/> puede entrar a los tramos de las marcas [from, last]. Si ahí
    /// empieza un tramo que otros van a recorrer de frente, se mira el tramo entero (hasta donde ya nadie viene de frente)
    /// contra todos a la vez: nadie puede tenerlo tomado ni reservado en contra, y una salida no entra si en él hay una
    /// llegada que todavía no libró la pista o una avisada en final (Montreal: con el chequeo avión por avión, una salida
    /// entraba contra una llegada y se detenía a mitad del tramo por el aviso de la siguiente).
    /// </summary>
    private bool CorridorFree(DepartureAgent a, IReadOnlyList<TaxiMark> marks, int from, int last,
        IReadOnlyList<DepartureAgent> agents, out uint who)
    {
        who = 0;
        var (start, end) = Corridor(a, marks, from, last, agents);
        if (start < 0)
            return true;
        // Ya dentro del tramo (el que recorre ahora también lo comparten de frente, p. ej. terminó el pushback en él): la
        // llegada no lo saca de ahí; él lo reserva y ella espera antes (Boston, calle K: la salida esperaba sobre la ruta
        // de la llegada a su puesto).
        var inside = start == from && OpposedByAny(a, from > 0 ? marks[from - 1].Edges : a.StartEdges, agents);
        for (var k = start; k <= end; k++)
        {
            var m = marks[k];
            if (!a.Inbound && !inside)
            {
                foreach (var (id, p) in _arrivalPreview)
                {
                    if (_time >= p.Near && Opposed(m, p.Keys))
                    {
                        who = id;
                        return false;
                    }
                }
                for (var j = 0; j < agents.Count; j++)
                {
                    var o = agents[j];
                    if (!ReferenceEquals(o, a) && o.OnRunwayInbound && _reservations.TryGetValue(o.ObjectId, out var other)
                        && Opposed(m, other.Planned))
                    {
                        who = o.ObjectId;
                        return false;
                    }
                }
            }
            foreach (var key in m.Edges)
            {
                var rev = TaxiMark.Reverse(key);
                if (_claimUsers.GetValueOrDefault(rev) > 0 && _claimOwner.GetValueOrDefault(rev) != a.ObjectId
                    && !DropStaleClaim(_claimOwner.GetValueOrDefault(rev)))
                {
                    who = _claimOwner.GetValueOrDefault(rev);
                    return false;
                }
                if (_segmentUsers.GetValueOrDefault(rev) > 0 && _segmentOwner.GetValueOrDefault(rev) != a.ObjectId)
                {
                    who = _segmentOwner.GetValueOrDefault(rev);
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Reserva en su sentido el tramo compartido de frente que empieza en [from, last] (ver <see cref="CorridorFree"/>).</summary>
    private void ClaimCorridor(DepartureAgent a, Reservations res, IReadOnlyList<TaxiMark> marks, int from, int last,
        IReadOnlyList<DepartureAgent> agents)
    {
        var (start, end) = Corridor(a, marks, from, last, agents);
        for (var k = start; k >= 0 && k <= end; k++)
        {
            foreach (var key in marks[k].Edges)
            {
                if (HasClaim(res, key))
                    continue;
                res.Claims.Add((start, key));
                _claimUsers[key] = _claimUsers.GetValueOrDefault(key) + 1;
                _claimOwner[key] = a.ObjectId;
            }
        }
    }

    /// <summary>¿Todos los tramos de las marcas [from, last] son de un tramo que <paramref name="res"/> ya reservó entero?</summary>
    private static bool InOwnCorridor(Reservations res, IReadOnlyList<TaxiMark> marks, int from, int last)
    {
        if (res.Claims.Count == 0)
            return false;
        var any = false;
        for (var k = from; k <= last && k < marks.Count; k++)
        {
            foreach (var key in marks[k].Edges)
            {
                if (!HasClaim(res, key))
                    return false;
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// Reserva vieja (ADR-096, Dallas): el dueño todavía no rueda (terminó el pushback y está detenido esperando), pero tenía
    /// reservado un tramo de 3 km y una llegada lo esperaba al otro lado. Se le quitan las reservas de tramo y los cruces que
    /// todavía no pisó; las vuelve a pedir al rodar, y entonces ve al otro. true si se quitaron.
    /// </summary>
    private bool DropStaleClaim(uint owner)
    {
        if (!_byId.TryGetValue(owner, out var x) || x.Phase != DeparturePhase.PushbackComplete || x.SpeedKt >= 1
            || !_reservations.TryGetValue(owner, out var rx) || rx.Claims.Count == 0)
            return false;
        ReleaseUnreached(x, rx);
        ReleaseClaimsFrom(rx, int.MinValue);
        Log?.Invoke(Tr.F("{0}: todavía no rueda; se le quita la reserva del tramo.", x.Callsign));
        return true;
    }

    private static bool HasClaim(Reservations res, long key)
    {
        foreach (var c in res.Claims)
        {
            if (c.Key == key)
                return true;
        }
        return false;
    }

    /// <summary>Suelta las reservas de tramo de los tramos que salen de la marca <paramref name="m"/> (ya recorridos).</summary>
    private void ReleaseClaims(TaxiMark m, Reservations res)
    {
        if (res.Claims.Count == 0)
            return;
        foreach (var key in m.Edges)
        {
            for (var c = res.Claims.Count - 1; c >= 0; c--)
            {
                if (res.Claims[c].Key == key)
                {
                    DropClaim(res, c);
                    break;
                }
            }
        }
    }

    /// <summary>Suelta los tramos reservados enteros a los que todavía no entró (devolvió las marcas desde su inicio).</summary>
    private void ReleaseClaimsFrom(Reservations res, int fromMark)
    {
        for (var c = res.Claims.Count - 1; c >= 0; c--)
        {
            if (res.Claims[c].Start >= fromMark)
                DropClaim(res, c);
        }
    }

    private void DropClaim(Reservations res, int index)
    {
        var key = res.Claims[index].Key;
        res.Claims.RemoveAt(index);
        var n = _claimUsers.GetValueOrDefault(key) - 1;
        if (n > 0)
        {
            _claimUsers[key] = n;
        }
        else
        {
            _claimUsers.Remove(key);
            _claimOwner.Remove(key);
        }
    }

    /// <summary>
    /// true si otro avión tiene reservado un cruce a menos de <see cref="NearbyNodeM"/> de <paramref name="at"/> que no está
    /// en la ruta de <paramref name="a"/> (los de su ruta los reserva él en orden).
    /// </summary>
    private bool NearbyHeld(DepartureAgent a, IReadOnlyList<TaxiMark> marks, LocalPoint at, out uint holder, out int node)
    {
        foreach (var (n, h) in _nodeHolder)
        {
            if (h == a.ObjectId || !_nodePos.TryGetValue(n, out var p) || LocalPoint.Distance(p, at) > NearbyNodeM)
                continue;
            var own = false;
            for (var k = 0; k < marks.Count && !own; k++)
                own = marks[k].Node == n;
            if (own)
                continue;
            holder = h;
            node = n;
            return true;
        }
        holder = 0;
        node = -1;
        return false;
    }

    /// <summary>
    /// Línea de espera antes de un cruce que tiene otro: la de siempre (<paramref name="stopS"/>), o más atrás si la ruta del
    /// otro cerca de ese cruce pasa a menos de <see cref="CrossingClearanceM"/> de ella (uniones en curva, cruces pegados).
    /// </summary>
    private double StopLine(DepartureAgent a, double stopS, uint holder, int node)
    {
        if (node < 0 || !_byId.TryGetValue(holder, out var h))
            return stopS;
        var hs = -1.0;
        var hmarks = h.Marks;
        for (var k = 0; k < hmarks.Count && hs < 0; k++) // con índice: un foreach sobre la interfaz asigna en cada tick
        {
            if (hmarks[k].Node == node)
                hs = hmarks[k].S;
        }
        var path = a.TaxiPath;
        if (hs < 0)
        {
            // No pasa por ese nodo, pero sí cerca (empalme con nodos vecinos, ADR-094, Boston): el punto de su ruta más cercano.
            if (!h.OnTaxiPath)
                return stopS;
            hs = h.TaxiPath.ProjectWithin(path.PointAt(stopS), h.TaxiProgress, h.TaxiPath.Length, out var near);
            if (near > StopNearRouteM)
                return stopS;
        }
        // Todo lo que le queda hasta el cruce, no solo los 60 m previos: en Boston la llegada pasaba a 33 m de la línea
        // antes de llegar al empalme (ADR-094).
        var from = Math.Max(h.TaxiProgress, hs - StopApproachWindowM);
        var to = hs + CrossingWindowM;
        var nodeAt = h.TaxiPath.PointAt(hs);
        // El margen con que el otro me ve como obstáculo detenido (FollowLimit), no menos de 45 m: con dos de fuselaje ancho
        // es más (ADR-096, Doha: la llegada esperaba a 45 m de la ruta de la salida y la salida se detenía por ella).
        var clearance = Math.Max(CrossingClearanceM, (a.WingspanM + h.WingspanM) / 2 + WingtipMarginM + StopBackStepM);
        for (var back = 0.0; back < StopBackMaxM && stopS - back > a.TaxiProgress; back += StopBackStepM)
        {
            var p = path.PointAt(stopS - back);
            h.TaxiPath.ProjectWithin(p, from, to, out var lateral);
            if (lateral >= clearance && ClearOfOtherWaiters(a, h, p, nodeAt) && ClearOfOtherRoutes(a, h, p, node, stopS - back))
                return stopS - back;
        }
        return stopS - StopBackMaxM;
    }

    /// <summary>
    /// ¿El punto de espera <paramref name="p"/> queda fuera del paso de todos los que todavía van a pasar por ese cruce, no
    /// solo del que lo tiene ahora? ADR-091 (Headless de LEMD, unión de A): uno esperaba a otro, pero dentro de la ruta de
    /// un tercero, que después quedó detenido detrás de él (bloqueo y llegadas sin poder dejar la pista).
    /// </summary>
    private bool ClearOfOtherRoutes(DepartureAgent a, DepartureAgent holder, LocalPoint p, int node, double atS)
    {
        foreach (var o in _byId.Values)
        {
            if (ReferenceEquals(o, a) || ReferenceEquals(o, holder) || !o.OnTaxiPath)
                continue;
            var om = o.Marks;
            var sameNode = false;
            for (var k = 0; k < om.Count; k++)
            {
                if (om[k].Node != node)
                    continue;
                sameNode = true;
                if (om[k].S < o.TaxiProgress - CrossingWindowM)
                    break; // ya pasó
                o.TaxiPath.ProjectWithin(p, Math.Max(o.TaxiProgress, om[k].S - CrossingWindowM), om[k].S + CrossingWindowM, out var lat);
                if (lat < CrossingClearanceM)
                    return false;
                break;
            }
            // Uno que no pasa por ese cruce pero sí por delante de la línea, por un cruce vecino (ADR-128, Headless de LEMD,
            // unión de J con A: el que esperaba al dueño del cruce quedó a 37 m del eje de A y el siguiente de la fila de A le
            // pasó la punta del ala a 3 m de la nariz). Solo los que están cerca y cruzan: el que viene por mi misma calle
            // (adelante o atrás) lo maneja la distancia de seguimiento.
            if (!sameNode && o.Phase == DeparturePhase.Taxi && LocalPoint.Distance(o.Position, p) < PassingLookM)
            {
                var s = o.TaxiPath.ProjectWithin(p, o.TaxiProgress, Math.Min(o.TaxiPath.Length, o.TaxiProgress + PassingLookM), out var lat);
                if (lat < CrossingClearanceM && lat > PathLateralM
                    && Math.Abs(GeoMath.AngleDiffDeg(o.TaxiPath.HeadingAt(s), a.TaxiPath.HeadingAt(atS))) is > PassingMinDeg and < 180 - PassingMinDeg)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Hasta dónde (m, de distancia y de ruta por delante) se mira a los que pasan frente a una línea de espera por otro cruce.</summary>
    public const double PassingLookM = 150;

    /// <summary>Ángulo mínimo (°) entre las dos rutas para contar como cruce y no como la misma calle o una paralela.</summary>
    public const double PassingMinDeg = 25;

    /// <summary>
    /// ¿El punto de espera <paramref name="p"/> queda a <see cref="CrossingClearanceM"/> de los demás que esperan junto al
    /// mismo cruce? En la unión de A en LEMD (Headless, ADR-091) dos que esperaban el mismo cruce llegando por calles
    /// distintas quedaban a 43 m.
    /// </summary>
    private bool ClearOfOtherWaiters(DepartureAgent a, DepartureAgent holder, LocalPoint p, LocalPoint node)
    {
        foreach (var o in _byId.Values)
        {
            if (ReferenceEquals(o, a) || ReferenceEquals(o, holder) || !o.OnGround || o.Phase != DeparturePhase.Taxi
                || LocalPoint.Distance(o.Position, node) > WaiterRadiusM + 60)
                continue;
            // Donde está y donde se va a detener (su límite de avance): los dos pueden llegar a la vez a sus líneas.
            if (LocalPoint.Distance(o.Position, p) < CrossingClearanceM + 1)
                return false;
            // Solo el de menor prioridad retrocede por la línea del otro (si no, se correrían los dos hacia atrás).
            if (PriorityOf(o) < PriorityOf(a) && !double.IsPositiveInfinity(o.TaxiLimitS)
                && LocalPoint.Distance(o.TaxiPath.PointAt(Math.Min(o.TaxiLimitS, o.TaxiPath.Length)), p) < CrossingClearanceM + 1)
                return false;
        }
        return true;
    }

    /// <summary>Tramo (m) después del punto de espera de la salida en el que una llegada sobre la pista no espera cruces.</summary>
    public const double ExitClearM = 150;

    /// <summary>Radio (m) alrededor de un cruce en el que otro avión cuenta como "esperando ese cruce".</summary>
    public const double WaiterRadiusM = 90;

    /// <summary>
    /// Límite para no quedar a la par de <paramref name="o"/>, que rueda adelante en el mismo sentido por una calle paralela
    /// tan cercana que quedarían menos de <see cref="WingtipMarginM"/> entre puntas de ala: se queda atrás hasta que haya
    /// <see cref="CrossingClearanceM"/> entre referencias (en SCEL hay calles de plataforma a 40 m entre ejes).
    /// </summary>
    private static double AbeamLimit(DepartureAgent a, DepartureAgent o, double progress)
    {
        var d = LocalPoint.Distance(a.Position, o.Position);
        if (d > CrossingClearanceM + LookaheadM / 4
            || Math.Abs(GeoMath.AngleDiffDeg(a.HeadingDeg, o.HeadingDeg)) > ParallelMaxDeg)
            return double.PositiveInfinity;
        var rel = GeoMath.AngleDiffDeg(a.HeadingDeg, Polyline.Bearing(a.Position, o.Position)) * GeoMath.DegToRad;
        var along = d * Math.Cos(rel);
        var lateral = d * Math.Abs(Math.Sin(rel));
        if (along <= 0 || lateral <= PathLateralM || lateral - (a.WingspanM + o.WingspanM) / 2 >= WingtipMarginM)
            return double.PositiveInfinity;
        var needed = Math.Sqrt(Math.Max(0, CrossingClearanceM * CrossingClearanceM - lateral * lateral)) + StopBackStepM;
        return Math.Max(progress, progress + along - needed);
    }

    /// <summary>Límite de avance para quedar detrás de <paramref name="other"/> si está sobre la ruta adelante.</summary>
    /// <summary>¿Pasa <paramref name="a"/> junto a <paramref name="o"/> (detenido al costado de su ruta) con las puntas de ala libres?</summary>
    private static bool WingtipsClear(Polyline path, double progress, DepartureAgent a, DepartureAgent o)
    {
        path.ProjectWithin(o.Position, progress + 1, progress + LookaheadM, out var lateral);
        return lateral >= (a.WingspanM + o.WingspanM) / 2 + 2;
    }

    /// <summary>Margen (m) entre las cajas de dos aviones: lo que queda libre para pasar. APROXIMADO.</summary>
    public const double HitboxMarginM = 7;

    /// <summary>Segundos de movimiento del piloto que cuenta la zona que barre (y como mucho <see cref="HitboxSweepMaxM"/>).</summary>
    public const double HitboxSweepS = 20;
    public const double HitboxSweepMaxM = 110;

    /// <summary>Paso (m) con que se recorre la ruta del IA buscando el primer choque de cajas.</summary>
    public const double HitboxStepM = 4;

    /// <summary>Largo de un avión por su envergadura (un reactor comercial es un poco más largo que ancho). APROXIMADO.</summary>
    public static double LengthFor(double wingspanM) => wingspanM * 1.1;

    /// <summary>
    /// Hasta dónde puede avanzar (m de su ruta) un IA sin que su caja (largo × envergadura, sobre su ruta) toque la del
    /// piloto, agrandada con <see cref="HitboxMarginM"/> y, si el piloto se mueve, estirada hacia adelante con lo que recorre
    /// en <see cref="HitboxSweepS"/>. null si no se tocan en lo que mira. Si ya se tocan donde está, se mira la caja quieta
    /// del piloto (el que viene detrás del IA no lo detiene: lo sigue); si también, null (lo manejan las otras reglas).
    /// Sin asignaciones (corre en el tick).
    /// </summary>
    public static double? HitboxStop(Polyline path, double progress, double aiWingspanM, LocalPoint user, GroundObstacle u)
    {
        var look = Math.Min(path.Length, progress + LookaheadM + 60);
        if (LocalPoint.Distance(path.PointAt(progress), user) > LookaheadM + 60 + HitboxSweepMaxM + 60)
            return null;
        var uLen = (u.LengthM > 0 ? u.LengthM : LengthFor(u.WingspanM)) / 2;
        var uWid = u.WingspanM / 2;
        var sweep = u.SpeedKt > 2 ? Math.Min(u.SpeedKt * 0.514444 * HitboxSweepS, HitboxSweepMaxM) : 0;
        var aLen = LengthFor(aiWingspanM) / 2;
        var aWid = aiWingspanM / 2;
        var swept = Touches(path.PointAt(progress), path.HeadingAt(progress), aLen, aWid, user, u.HeadingDeg, uLen, uWid, sweep);
        if (swept)
        {
            // Ya dentro de la zona que barre el piloto: que la deje (si estamos de frente, lo resuelven las demás reglas).
            if (Touches(path.PointAt(progress), path.HeadingAt(progress), aLen, aWid, user, u.HeadingDeg, uLen, uWid, 0))
                return null;
            sweep = 0;
        }
        for (var s = progress + HitboxStepM; s <= look; s += HitboxStepM)
        {
            if (Touches(path.PointAt(s), path.HeadingAt(s), aLen, aWid, user, u.HeadingDeg, uLen, uWid, sweep))
                return Math.Max(progress, s - HitboxStepM);
        }
        return null;
    }

    /// <summary>
    /// ¿Se tocan la caja del IA (centro, rumbo, medio largo y media envergadura) y la del piloto agrandada con el margen y
    /// estirada <paramref name="sweepM"/> hacia adelante? Ejes separadores de dos rectángulos orientados.
    /// </summary>
    private static bool Touches(LocalPoint a, double aHeadingDeg, double aLen, double aWid, LocalPoint u, double uHeadingDeg,
        double uLen, double uWid, double sweepM)
    {
        var uh = uHeadingDeg * GeoMath.DegToRad;
        var ufx = Math.Sin(uh);
        var ufy = Math.Cos(uh);
        // Estirada hacia adelante: el centro avanza la mitad y el largo crece la mitad.
        var ucx = u.EastM + ufx * sweepM / 2;
        var ucy = u.NorthM + ufy * sweepM / 2;
        var uL = uLen + sweepM / 2 + HitboxMarginM;
        var uW = uWid + HitboxMarginM;
        var ah = aHeadingDeg * GeoMath.DegToRad;
        var afx = Math.Sin(ah);
        var afy = Math.Cos(ah);
        var dx = ucx - a.EastM;
        var dy = ucy - a.NorthM;
        // Ejes: adelante y costado de cada uno.
        return Overlap(afx, afy, dx, dy, aLen, aWid, afx, afy, ufx, ufy, uL, uW)
               && Overlap(-afy, afx, dx, dy, aLen, aWid, afx, afy, ufx, ufy, uL, uW)
               && Overlap(ufx, ufy, dx, dy, aLen, aWid, afx, afy, ufx, ufy, uL, uW)
               && Overlap(-ufy, ufx, dx, dy, aLen, aWid, afx, afy, ufx, ufy, uL, uW);
    }

    private static bool Overlap(double ax, double ay, double dx, double dy, double aLen, double aWid, double afx, double afy,
        double ufx, double ufy, double uL, double uW)
    {
        var ra = aLen * Math.Abs(afx * ax + afy * ay) + aWid * Math.Abs(-afy * ax + afx * ay);
        var ru = uL * Math.Abs(ufx * ax + ufy * ay) + uW * Math.Abs(-ufy * ax + ufx * ay);
        return Math.Abs(dx * ax + dy * ay) <= ra + ru;
    }

    private static double FollowLimit(Polyline path, LocalPoint here, double progress, LocalPoint other, double halfLengths,
        double maxLateralM)
    {
        if (LocalPoint.Distance(here, other) > LookaheadM + 50)
            return double.PositiveInfinity;
        var s = path.ProjectWithin(other, progress + 1, progress + LookaheadM, out var lateral);
        if (lateral > maxLateralM || s <= progress + 1)
            return double.PositiveInfinity;
        return Math.Max(progress, s - FollowGapM - halfLengths);
    }

    /// <summary>Libera el cruce <paramref name="index"/> y el tramo que llegaba a él desde el cruce anterior.</summary>
    private void ReleaseMark(uint id, IReadOnlyList<TaxiMark> marks, int index, Reservations res)
    {
        var m = marks[index];
        if (_nodeHolder.GetValueOrDefault(m.Node) == id)
            _nodeHolder.Remove(m.Node);
        for (; res.SegmentFrom < index; res.SegmentFrom++)
        {
            ReleaseEdges(marks[res.SegmentFrom], res);
            ReleaseClaims(marks[res.SegmentFrom], res);
        }
    }

    /// <summary>Tramos que el avión todavía va a recorrer: desde el último cruce que pasó hasta el punto de espera.</summary>
    private static void UpdatePlanned(DepartureAgent a, Reservations res)
    {
        var marks = a.Marks;
        if (!ReferenceEquals(res.PlannedFor, marks))
        {
            res.PlannedFor = marks;
            res.Planned.Clear();
            if (res.Released == 0 && a.Reroutes == 0)
            {
                foreach (var key in a.StartEdges)
                    res.Planned.Add(key);
            }
            foreach (var m in marks)
            {
                foreach (var key in m.Edges)
                    res.Planned.Add(key);
            }
        }
        // Lo ya recorrido deja de ser intención.
        if (res.Released > 0)
        {
            foreach (var key in a.StartEdges)
                res.Planned.Remove(key);
        }
        for (var i = 0; i < res.Released && i < marks.Count; i++)
        {
            foreach (var key in marks[i].Edges)
                res.Planned.Remove(key);
        }
    }

    /// <summary>
    /// true si algún avión de mayor prioridad (salió antes) y ya en movimiento va a recorrer, en sentido contrario, un tramo
    /// de los que este avión recorre desde el cruce <paramref name="from"/> en adelante.
    /// </summary>
    private bool PlannedOncoming(DepartureAgent a, IReadOnlyList<TaxiMark> marks, int from, IReadOnlyList<DepartureAgent> agents, out uint who,
        bool includeStart = false, bool timed = false)
    {
        who = 0;
        var mine = _arrival.GetValueOrDefault(a.ObjectId, long.MaxValue);
        for (var j = 0; j < agents.Count; j++)
        {
            var o = agents[j];
            if (ReferenceEquals(o, a) || _arrival.GetValueOrDefault(o.ObjectId, long.MaxValue) >= mine
                || !(o.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete or DeparturePhase.Taxi
                     || (o.Phase == DeparturePhase.BeaconOn && o.IsCleared(ClearanceKind.Pushback)))
                // Al rodar: no se espera al que está detenido esperándome a mí como obstáculo (ADR-096, Doha: la llegada
                // esperaba por prioridad a la salida, y la salida no avanzaba porque la llegada le tapaba el paso).
                || !includeStart && o.SpeedKt < 1 && o.Hold == HoldKind.Traffic && o.HoldFor == a.ObjectId
                || !_reservations.TryGetValue(o.ObjectId, out var other))
                continue;
            if (includeStart)
            {
                foreach (var key in a.StartEdges)
                {
                    if (other.Planned.Contains(TaxiMark.Reverse(key))
                        && !(timed && PassesBefore(a, marks.Count > 0 ? marks[0].S : 0, o, TaxiMark.Reverse(key))))
                    {
                        who = o.ObjectId;
                        return true;
                    }
                }
            }
            for (var k = from; k < marks.Count; k++)
            {
                foreach (var key in marks[k].Edges)
                {
                    if (other.Planned.Contains(TaxiMark.Reverse(key)) && !(timed && PassesBefore(a, marks[k].S, o, TaxiMark.Reverse(key))))
                    {
                        who = o.ObjectId;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>true si alguien usa en sentido contrario algún tramo entre este cruce y el siguiente.</summary>
    private bool OncomingOn(TaxiMark m, out uint owner)
    {
        foreach (var key in m.Edges)
        {
            var reverse = TaxiMark.Reverse(key);
            if (_segmentUsers.GetValueOrDefault(reverse) > 0)
            {
                owner = _segmentOwner.GetValueOrDefault(reverse);
                return true;
            }
        }
        owner = 0;
        return false;
    }

    private void ReleaseEdges(TaxiMark m, Reservations res)
    {
        foreach (var key in m.Edges)
        {
            // Solo lo que este avión tomó (no descuenta tramos de otro con la misma clave).
            if (res.HeldSegments.Remove(key))
                DecrementSegment(key);
        }
    }

    private void DecrementSegment(long key)
    {
        var n = _segmentUsers.GetValueOrDefault(key) - 1;
        if (n > 0)
        {
            _segmentUsers[key] = n;
        }
        else
        {
            _segmentUsers.Remove(key);
            _segmentOwner.Remove(key);
        }
    }

    private void ReleaseAll(uint id, DepartureAgent a, Reservations res) => ReleaseAll(id, a.Marks, res);

    private void ReleaseAll(uint id, IReadOnlyList<TaxiMark> marks, Reservations res)
    {
        for (var i = res.Released; i < Math.Min(res.Acquired, marks.Count); i++)
            ReleaseMark(id, marks, i, res);
        for (; res.SegmentFrom < Math.Min(res.Acquired, marks.Count); res.SegmentFrom++)
        {
            ReleaseEdges(marks[res.SegmentFrom], res);
        }
        res.Released = res.Acquired;
        // Y todo lo que quedó tomado con marcas anteriores.
        foreach (var node in res.HeldNodes)
        {
            if (_nodeHolder.GetValueOrDefault(node) == id)
                _nodeHolder.Remove(node);
        }
        res.HeldNodes.Clear();
        foreach (var key in res.HeldSegments)
            DecrementSegment(key);
        res.HeldSegments.Clear();
        ReleaseClaimsFrom(res, int.MinValue);
    }

    /// <summary>Un avión dejó el motor (retirado): se liberan sus reservas.</summary>
    public void Remove(DepartureAgent a)
    {
        if (_reservations.Remove(a.ObjectId, out var res) && !res.Departed)
            ReleaseAll(a.ObjectId, a, res);
        _arrival.Remove(a.ObjectId);
        _prevWait.Remove(a.ObjectId);
        _inCycleFor.Remove(a.ObjectId);
        _cycleTotal.Remove(a.ObjectId);
        _giveWayDone.Clear();
        foreach (var key in _giveWay.Keys)
        {
            if ((uint)(key >> 32) == a.ObjectId || (uint)key == a.ObjectId)
                _giveWayDone.Add(key);
        }
        foreach (var key in _giveWayDone)
            _giveWay.Remove(key);
    }

    public void Clear()
    {
        _nodeHolder.Clear();
        _nodePos.Clear();
        _segmentUsers.Clear();
        _segmentOwner.Clear();
        _claimUsers.Clear();
        _claimOwner.Clear();
        _arrivalPreview.Clear();
        _reservations.Clear();
        _arrival.Clear();
        _inCycleFor.Clear();
        _priorityHeld.Clear();
        _priorityOf = null;
        _cycleTotal.Clear();
        _giveWay.Clear();
    }
}
