using SimATC.Core.Airports;
using SimATC.Core.Geo;

namespace SimATC.Core.Taxi;

/// <summary>
/// Entrada a una pista desde la red de rodaje: el tramo que va de <see cref="EntryNode"/> (fuera de pista) a
/// <see cref="RunwayNode"/> (en pista). <see cref="HoldNode"/> es el punto de espera para esa entrada: el marcado en los
/// datos del simulador si hay uno cerca antes de la entrada, o la propia <see cref="EntryNode"/> (inferido).
/// </summary>
public sealed record RunwayEntry(
    int EntryNode,
    int RunwayNode,
    int RunwayIndex,
    // Posición a lo largo de la pista desde su centro, en el sentido del rumbo del extremo primario (m).
    double AlongM,
    int HoldNode,
    bool HoldIsMarked,
    string TaxiwayName);

/// <summary>Ruta de rodaje: nodos del grafo, largo total y calles por las que pasa ("via A B").</summary>
public sealed record TaxiRoute(IReadOnlyList<int> Nodes, double LengthM, IReadOnlyList<string> Taxiways);

/// <summary>
/// Estado del grafo de rodaje después de reparar (ADR-093): uniones hechas, puestos unidos, puestos que siguen sin camino,
/// partes sueltas, entradas sin punto de espera pintado y pistas sin entradas.
/// </summary>
public sealed record GraphHealth(int RepairedJoins, int StandsLinked, int UnreachableStands, int LoosePartCount, int EntriesWithoutHold,
    IReadOnlyList<string> RunwaysWithoutEntries)
{
    public bool Healthy => UnreachableStands == 0 && RunwaysWithoutEntries.Count == 0;

    public string Summary =>
        Text.Tr.F("{0} uniones reparadas, {1} puestos unidos, {2} puestos sin camino, {3} partes sueltas, {4} entradas sin punto de espera pintado",
            RepairedJoins, StandsLinked, UnreachableStands, LoosePartCount, EntriesWithoutHold) +
        (RunwaysWithoutEntries.Count > 0 ? Text.Tr.F(", pistas sin entradas: {0}", string.Join(", ", RunwaysWithoutEntries)) : "");
}

/// <summary>
/// Grafo de rodaje de un aeropuerto (RF-08). Nodos: primero los puntos de rodaje (mismo índice que
/// <see cref="AirportData.TaxiPoints"/>) y después los puestos (<see cref="ParkingNode"/>). Aristas no dirigidas
/// desde los tramos TAXI, PATH, PARKING y RUNWAY; los tramos de vehículos, caminos y líneas pintadas se descartan.
/// </summary>
public sealed class TaxiGraph
{
    /// <summary>Margen fuera del ancho de pista que aún se considera "en pista" (m).</summary>
    public const double RunwayMarginM = 10;

    /// <summary>Margen más allá de cada extremo de pista (zonas de parada y de giro) que aún se considera "en pista" (m).</summary>
    public const double RunwayEndMarginM = 60;

    /// <summary>Distancia máxima de recorrido entre una entrada y su punto de espera marcado (m).</summary>
    public const double MaxHoldSearchM = 150;

    /// <summary>Distancia máxima de un punto de espera marcado al eje de su pista (m).</summary>
    public const double MaxHoldCrossTrackM = 250;

    /// <summary>Distancia máxima entre una entrada y un extremo de pista para usarla como cabecera (m).</summary>
    public const double MaxEntryToRunwayEndM = 400;

    /// <summary>
    /// Puntos más cerca que esto se consideran el mismo lugar y se unen con un tramo de largo 0. Corrige "costuras"
    /// del escenario: en SCEL los puestos D1, D3 y D5 quedaban aislados por dos puntos duplicados sin tramo entre ellos.
    /// </summary>
    public const double CoincidentToleranceM = 0.5;

    private readonly AirportData _airport;
    private readonly LocalPoint[] _positions;
    private readonly int[] _runwayAt;
    private readonly List<Edge>[] _adjacency;
    private readonly List<RunwayEntry> _entries = [];

    private readonly record struct Edge(int To, double LengthM, TaxiPathType Type, int NameIndex);

    private TaxiGraph(AirportData airport)
    {
        _airport = airport;
        Projection = new LocalProjection(airport.Reference);
        var pointCount = airport.TaxiPoints.Count;
        NodeCount = pointCount + airport.Parkings.Count;

        _positions = new LocalPoint[NodeCount];
        _runwayAt = new int[NodeCount];
        _adjacency = new List<Edge>[NodeCount];
        for (var i = 0; i < NodeCount; i++)
        {
            var geo = i < pointCount ? airport.TaxiPoints[i].Position : airport.Parkings[i - pointCount].Position;
            _positions[i] = Projection.ToLocal(geo);
            _runwayAt[i] = i < pointCount ? FindRunway(_positions[i]) : -1;
            _adjacency[i] = [];
        }
    }

    public LocalProjection Projection { get; }

    /// <summary>Pistas del aeropuerto (para vigilar incursiones).</summary>
    public IReadOnlyList<Airports.Runway> Runways => _airport.Runways;

    public int NodeCount { get; }

    /// <summary>Tramos del simulador descartados por índices fuera de rango.</summary>
    public int InvalidPathCount { get; private set; }

    /// <summary>Pares de puntos coincidentes que se unieron (ver <see cref="CoincidentToleranceM"/>).</summary>
    public int JoinedCoincidentCount { get; private set; }

    public IReadOnlyList<RunwayEntry> RunwayEntries => _entries;

    public static TaxiGraph Build(AirportData airport)
    {
        var g = new TaxiGraph(airport);
        g.AddEdges();
        g.JoinCoincidentPoints();
        g.RepairConnectivity();
        g.FindRunwayEntries();
        g.CheckHealth();
        return g;
    }

    // ---------- Grafo completo en cualquier aeropuerto (ADR-093) ----------

    /// <summary>Una parte suelta del grafo se une a la principal si hay puntos a menos de esto (m): costuras del escenario.</summary>
    public const double RepairJoinM = 25;

    /// <summary>Un puesto suelto se une al punto de rodaje más cercano de la red principal a menos de esto (m).</summary>
    public const double StandLinkM = 120;

    /// <summary>Partes sueltas unidas a la red principal al construir.</summary>
    public int RepairedJoins { get; private set; }

    /// <summary>Puestos sueltos que se unieron a la red principal.</summary>
    public int StandsLinked { get; private set; }

    private bool[] _standReachable = [];

    /// <summary>
    /// ¿El puesto tiene camino a la red de rodaje principal (a las pistas)? Los que no, no se asignan: una llegada no podría
    /// rodar hasta él y desaparecería (ADR-093).
    /// </summary>
    public bool StandReachable(int parkingIndex) =>
        parkingIndex >= 0 && parkingIndex < _standReachable.Length && _standReachable[parkingIndex];

    /// <summary>Resumen del grafo para Diagnóstico y el Headless (ADR-093).</summary>
    public GraphHealth Health { get; private set; } = new(0, 0, 0, 0, 0, []);

    /// <summary>
    /// Une al grafo principal (el que tiene más puntos sobre pistas) las partes sueltas que quedan a menos de
    /// <see cref="RepairJoinM"/>, y los puestos sin camino al punto de rodaje más cercano a menos de <see cref="StandLinkM"/>.
    /// Los escenarios traen costuras: puntos a pocos metros sin tramo entre ellos, o puestos sin su tramo de entrada.
    /// </summary>
    private void RepairConnectivity()
    {
        var comp = Components(out var main);
        var pointCount = _airport.TaxiPoints.Count;
        var others = new Dictionary<int, List<int>>();
        for (var n = 0; n < pointCount; n++)
        {
            if (comp[n] != main && _adjacency[n].Count > 0)
            {
                if (!others.TryGetValue(comp[n], out var list))
                    others[comp[n]] = list = [];
                list.Add(n);
            }
        }
        var mainNodes = Enumerable.Range(0, pointCount).Where(n => comp[n] == main && _runwayAt[n] < 0).ToList();
        foreach (var part in others.Values)
        {
            int bestA = -1, bestB = -1;
            var best = RepairJoinM;
            foreach (var a in part)
            {
                if (_runwayAt[a] >= 0)
                    continue;
                foreach (var b in mainNodes)
                {
                    var d = LocalPoint.Distance(_positions[a], _positions[b]);
                    if (d < best)
                    {
                        best = d;
                        bestA = a;
                        bestB = b;
                    }
                }
            }
            if (bestA < 0)
                continue;
            AddRepairEdge(bestA, bestB, TaxiPathType.Path);
            RepairedJoins++;
        }
        if (RepairedJoins > 0)
            comp = Components(out main);
        mainNodes = [.. Enumerable.Range(0, pointCount).Where(n => comp[n] == main && _runwayAt[n] < 0)];
        for (var s = pointCount; s < NodeCount; s++)
        {
            if (comp[s] == main)
                continue;
            var bestB = -1;
            var best = StandLinkM;
            foreach (var b in mainNodes)
            {
                var d = LocalPoint.Distance(_positions[s], _positions[b]);
                if (d < best)
                {
                    best = d;
                    bestB = b;
                }
            }
            if (bestB < 0)
                continue;
            AddRepairEdge(s, bestB, TaxiPathType.Parking);
            StandsLinked++;
        }
    }

    private void AddRepairEdge(int a, int b, TaxiPathType type)
    {
        var length = LocalPoint.Distance(_positions[a], _positions[b]);
        _adjacency[a].Add(new Edge(b, length, type, 0));
        _adjacency[b].Add(new Edge(a, length, type, 0));
    }

    /// <summary>Componente de cada nodo; <paramref name="main"/> = la que tiene más puntos sobre pistas (o la más grande).</summary>
    private int[] Components(out int main)
    {
        var comp = new int[NodeCount];
        Array.Fill(comp, -1);
        var count = 0;
        var bestScore = -1L;
        main = 0;
        var stack = new Stack<int>();
        for (var s = 0; s < NodeCount; s++)
        {
            if (comp[s] >= 0)
                continue;
            long runwayNodes = 0, size = 0;
            comp[s] = count;
            stack.Push(s);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                size++;
                if (_runwayAt[n] >= 0)
                    runwayNodes++;
                foreach (var e in _adjacency[n])
                {
                    if (comp[e.To] < 0)
                    {
                        comp[e.To] = count;
                        stack.Push(e.To);
                    }
                }
            }
            var score = runwayNodes * 1_000_000 + size;
            if (score > bestScore)
            {
                bestScore = score;
                main = count;
            }
            count++;
        }
        return comp;
    }

    /// <summary>Lo que queda roto después de reparar: puestos sin camino, pistas sin entradas, partes sueltas.</summary>
    private void CheckHealth()
    {
        var comp = Components(out var main);
        var pointCount = _airport.TaxiPoints.Count;
        var unreachable = 0;
        _standReachable = new bool[_airport.Parkings.Count];
        for (var s = pointCount; s < NodeCount; s++)
        {
            _standReachable[s - pointCount] = comp[s] == main;
            if (comp[s] != main)
                unreachable++;
        }
        var parts = new HashSet<int>();
        for (var n = 0; n < pointCount; n++)
        {
            if (_adjacency[n].Count > 0 && comp[n] != main)
                parts.Add(comp[n]);
        }
        var noEntries = new List<string>();
        for (var r = 0; r < _airport.Runways.Count; r++)
        {
            if (!_entries.Any(e => e.RunwayIndex == r))
                noEntries.Add(_airport.Runways[r].Primary.Ident + "/" + _airport.Runways[r].Secondary.Ident);
        }
        Health = new GraphHealth(RepairedJoins, StandsLinked, unreachable, parts.Count, _entries.Count(e => !e.HoldIsMarked), noEntries);
    }

    public int ParkingNode(int parkingIndex) => _airport.TaxiPoints.Count + parkingIndex;

    // ---------- Sentidos de circulación (ADR-067) ----------

    /// <summary>Recargo por rodar contra el sentido de una calle paralela a la pista en uso.</summary>
    public const double AgainstFlowFactor = 2;

    /// <summary>Calles paralelas a la pista: a menos de este ángulo (°) y entre estas distancias laterales del eje (m).</summary>
    public const double FlowParallelDeg = 20;
    public const double FlowMinLateralM = 60;
    public const double FlowMaxLateralM = 1200;

    /// <summary>
    /// Si la segunda calle paralela lleva el sentido contrario (de vuelta). Apagado: con salidas por pistas paralelas, las que
    /// salían de puestos junto a esa calle quedaban de frente con las que la usaban (scel-busy-morning, 2026-11-03).
    /// </summary>
    public const bool OuterLaneFlow = false;

    // Rumbo preferido de cada tramo con sentido (clave: nodo menor << 32 | mayor). Se reemplaza entero (lo leen otros hilos).
    private Dictionary<long, double> _flow = [];

    /// <summary>Tramos con sentido de circulación (para el radar y los tests).</summary>
    public int FlowEdgeCount => _flow.Count;

    /// <summary>Rodar hacia una pista de llegadas por una de sus calles de salida cuesta esto veces más (ADR-092).</summary>
    public const double AgainstExitFactor = 6;

    /// <summary>Largo (m) de cada calle de salida, desde la pista, al que se le da sentido de salida.</summary>
    public const double ExitFlowM = 200;

    private Dictionary<long, double> _exitFlow = [];

    public int ExitFlowEdgeCount => _exitFlow.Count;

    /// <summary>
    /// Sentido de salida en las calles que salen de las pistas de llegada en uso (ADR-092, prueba de Marco en LEMD: una
    /// llegada a 32L rodaba a su puesto por la calle de salida de 32R al revés y quedó de frente con las que salían de 32R).
    /// En los primeros <see cref="ExitFlowM"/> m de cada salida (desde la zona de salidas de la pista, no las entradas
    /// cerca del umbral), el sentido es alejándose de la pista. Rodar al revés no se prohíbe: cuesta
    /// <see cref="AgainstExitFactor"/> veces más. Lista vacía: se quitan.
    /// </summary>
    public void SetArrivalExitFlows(IReadOnlyList<(int Runway, bool Primary)> arrivalEnds)
    {
        var flows = new Dictionary<long, double>();
        foreach (var (runway, primary) in arrivalEnds)
        {
            if (runway < 0 || runway >= _airport.Runways.Count)
                continue;
            var len = _airport.Runways[runway].LengthM;
            var threshold = primary ? -len / 2 : len / 2;
            var dir = primary ? 1 : -1;
            foreach (var entry in _entries)
            {
                if (entry.RunwayIndex != runway || (entry.AlongM - threshold) * dir < 600)
                    continue;
                // Desde la entrada, hacia afuera: cada tramo que se aleja de la pista, hasta ExitFlowM.
                var stack = new Stack<int>();
                var seen = new HashSet<int> { entry.EntryNode };
                stack.Push(entry.EntryNode);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    var (_, cn) = RunwayFrame(runway, _positions[n]);
                    foreach (var e in _adjacency[n])
                    {
                        if (Rwy(e.To) >= 0 || AlongRunway(n, e) || !seen.Add(e.To))
                            continue;
                        var (_, ct) = RunwayFrame(runway, _positions[e.To]);
                        if (Math.Abs(ct) <= Math.Abs(cn) || Math.Abs(ct) > ExitFlowM)
                            continue;
                        var pa = _positions[n];
                        var pb = _positions[e.To];
                        flows[EdgeKey(n, e.To)] = Math.Atan2(pb.EastM - pa.EastM, pb.NorthM - pa.NorthM) * GeoMath.RadToDeg;
                        stack.Push(e.To);
                    }
                }
            }
        }
        _exitFlow = flows;
    }

    /// <summary>
    /// Sentidos de circulación para la pista de salida en uso (<paramref name="runwayIndex"/>, extremo
    /// <paramref name="primaryEnd"/>), para cualquier aeropuerto: no hay una base mundial de los sentidos del AIP (V-37), así
    /// que se deducen de la geometría, como en los aeropuertos con calles dobles. A cada lado de la pista, la calle paralela
    /// más cercana va hacia la cabecera de despegue (las salidas ruedan hacia el inicio de la pista) y la siguiente, si hay,
    /// en sentido contrario (las llegadas vuelven). Rodar al revés no se prohíbe: cuesta <see cref="AgainstFlowFactor"/>
    /// veces más. Con <paramref name="runwayIndex"/> negativo se quitan.
    /// </summary>
    public void SetFlow(int runwayIndex, bool primaryEnd) =>
        SetFlows(runwayIndex < 0 ? [] : [(runwayIndex, primaryEnd)]);

    /// <summary>
    /// Sentidos para varias pistas de salida a la vez (paralelas en uso): la unión; una calle a la que dos pistas le piden
    /// sentidos opuestos queda sin sentido.
    /// </summary>
    public void SetFlows(IReadOnlyList<(int Runway, bool Primary)> ends)
    {
        var merged = new Dictionary<long, double>();
        var conflicted = new HashSet<long>();
        foreach (var (runway, primary) in ends)
        {
            if (runway < 0 || runway >= _airport.Runways.Count)
                continue;
            foreach (var (key, want) in FlowFor(runway, primary))
            {
                if (merged.TryGetValue(key, out var have) && Math.Abs(GeoMath.AngleDiffDeg(have, want)) > 90)
                    conflicted.Add(key);
                else
                    merged[key] = want;
            }
        }
        foreach (var k in conflicted)
            merged.Remove(k);
        _flow = merged;
    }

    private Dictionary<long, double> FlowFor(int runwayIndex, bool primaryEnd)
    {
        var r = _airport.Runways[runwayIndex];
        var takeoffHeading = primaryEnd ? r.Primary.HeadingTrueDeg : r.Secondary.HeadingTrueDeg;
        var toStart = GeoMath.NormalizeDeg(takeoffHeading + 180);
        var halfLength = r.LengthM / 2 + 500;
        // Tramos paralelos con su distancia lateral (con signo: el lado).
        var parallel = new List<(int A, int B, double Cross, double Length, bool Standby)>();
        for (var a = 0; a < _airport.TaxiPoints.Count; a++)
        {
            foreach (var e in _adjacency[a])
            {
                // Calle de rodaje: tipo Taxi, o Path con nombre de calle (ADR-160: en Gatwick las calles J, Z, W… vienen como
                // Path y no se encontraba ninguna paralela; salidas y llegadas se cruzaban de frente en la misma calle).
                var taxiway = e.Type == TaxiPathType.Taxi || e.Type == TaxiPathType.Path && TaxiwayName(e.NameIndex).Length > 0
                              || e.Type == TaxiPathType.Runway && Standby(a) && Standby(e.To);
                if (e.To <= a || !taxiway || e.LengthM < 5 || Rwy(a) >= 0 || Rwy(e.To) >= 0)
                    continue;
                var pa = _positions[a];
                var pb = _positions[e.To];
                var bearing = GeoMath.NormalizeDeg(Math.Atan2(pb.EastM - pa.EastM, pb.NorthM - pa.NorthM) * GeoMath.RadToDeg);
                var off = Math.Abs(GeoMath.AngleDiffDeg(takeoffHeading, bearing));
                if (off > FlowParallelDeg && off < 180 - FlowParallelDeg)
                    continue;
                var mid = new LocalPoint((pa.EastM + pb.EastM) / 2, (pa.NorthM + pb.NorthM) / 2);
                var (along, cross) = RunwayFrame(runwayIndex, mid);
                if (Math.Abs(along) > halfLength || Math.Abs(cross) < FlowMinLateralM || Math.Abs(cross) > FlowMaxLateralM)
                    continue;
                parallel.Add((a, e.To, cross, e.LengthM, e.Type == TaxiPathType.Runway));
            }
        }
        // Calles (por distancia lateral, en franjas de 30 m) con al menos 300 m de largo: la más cercana y la siguiente.
        var flow = new Dictionary<long, double>();
        foreach (var side in new[] { 1, -1 })
        {
            // Una pista de reserva usada como calle (ADR-160) es la de vuelta (sentido del despegue), como en Gatwick; la
            // calle más cercana que no es pista va hacia la cabecera.
            var hasStandby = parallel.Any(x => Math.Sign(x.Cross) == side && x.Standby);
            var lanes = parallel.Where(x => Math.Sign(x.Cross) == side && !x.Standby)
                .GroupBy(x => (int)Math.Round(Math.Abs(x.Cross) / 30))
                .Where(g => g.Sum(x => x.Length) >= 300)
                .OrderBy(g => g.Key)
                .ToList();
            if (lanes.Count == 0 && !hasStandby)
                continue;
            int? inner = lanes.Count > 0 ? lanes[0].Key : null;
            var outer = hasStandby || inner is null ? null : lanes.FirstOrDefault(g => g.Key - inner.Value >= 2)?.Key;
            foreach (var x in parallel.Where(x => Math.Sign(x.Cross) == side))
            {
                var band = (int)Math.Round(Math.Abs(x.Cross) / 30);
                double? want = x.Standby ? takeoffHeading
                    : inner is { } i && Math.Abs(band - i) <= 1 ? toStart
                    : OuterLaneFlow && outer is { } o && Math.Abs(band - o) <= 1 ? takeoffHeading : null;
                if (want is { } w)
                    flow[EdgeKey(x.A, x.B)] = w;
            }
        }
        return flow;
    }

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Flechas de sentido para el radar (ADR-102, bloque D): una por tramo con sentido (calles paralelas a la pista de
    /// salida y salidas de las pistas de llegada), en su punto medio y con su rumbo verdadero; los tramos de menos de
    /// <paramref name="minEdgeM"/> no llevan, ni los que quedan a menos de <paramref name="spacingM"/> de otra flecha con el
    /// mismo rumbo (no se amontonan).
    /// </summary>
    public IReadOnlyList<(GeoPoint Position, double HeadingDeg)> FlowArrows(double minEdgeM = 25, double spacingM = 90)
    {
        var picked = new List<(LocalPoint P, double H)>();
        void Add(Dictionary<long, double> flows)
        {
            foreach (var (key, heading) in flows)
            {
                var a = (int)(key >> 32);
                var b = (int)(uint)key;
                var pa = _positions[a];
                var pb = _positions[b];
                if (LocalPoint.Distance(pa, pb) < minEdgeM)
                    continue;
                var mid = new LocalPoint((pa.EastM + pb.EastM) / 2, (pa.NorthM + pb.NorthM) / 2);
                var crowded = false;
                foreach (var (p, h) in picked)
                {
                    if (LocalPoint.Distance(p, mid) < spacingM && Math.Abs(GeoMath.AngleDiffDeg(h, heading)) < 45)
                    {
                        crowded = true;
                        break;
                    }
                }
                if (!crowded)
                    picked.Add((mid, heading));
            }
        }
        Add(_flow);
        Add(_exitFlow);
        return [.. picked.Select(x => (Projection.ToGeo(x.P), x.H))];
    }

    public bool IsParking(int node) => node >= _airport.TaxiPoints.Count;

    public LocalPoint Position(int node) => _positions[node];

    /// <summary>Índice de la pista sobre la que está el nodo, o -1.</summary>
    /// <summary>La pista sobre la que está el nodo, o -1 (también -1 en una pista de reserva usada como calle, ADR-160).</summary>
    public int RunwayAt(int node) => Rwy(node);

    private bool[] _standbyRunway = [];

    /// <summary>
    /// Pistas de reserva que, mientras no se usan, sirven de calle de rodaje (ADR-160; Gatwick: la 08L/26R es la calle
    /// principal de la 08R/26L). Sus nodos dejan de ser "de pista" (sin cruces ni esperas) y se puede rodar a lo largo.
    /// Vacía, todas las pistas son pistas.
    /// </summary>
    public void SetStandbyRunways(IReadOnlyCollection<int> runways)
    {
        var standby = new bool[_airport.Runways.Count];
        foreach (var r in runways)
        {
            if (r >= 0 && r < standby.Length)
                standby[r] = true;
        }
        _standbyRunway = standby;
    }

    /// <summary>¿El nodo está sobre una pista de reserva usada como calle?</summary>
    private bool Standby(int node)
    {
        var r = _runwayAt[node];
        var standby = _standbyRunway;
        return r >= 0 && r < standby.Length && standby[r];
    }

    /// <summary>Pista del nodo para rodar (-1 en una pista de reserva usada como calle).</summary>
    private int Rwy(int node) => Standby(node) ? -1 : _runwayAt[node];

    /// <summary>¿El tramo va a lo largo de una pista (que no sea de reserva usada como calle)?</summary>
    private bool AlongRunway(int from, Edge e) => e.Type == TaxiPathType.Runway && !(Standby(from) && Standby(e.To));

    /// <summary>Vecinos de un nodo (para dibujar o recorrer).</summary>
    public IEnumerable<(int To, TaxiPathType Type, string Name)> Neighbors(int node)
    {
        foreach (var e in _adjacency[node])
            yield return (e.To, e.Type, TaxiwayName(e.NameIndex));
    }

    /// <summary>
    /// Tramos de calles de rodaje "de paso" (sin pista ni puestos) con algún extremo a menos de
    /// <paramref name="radiusM"/> de p. Se excluyen los ramales sin salida: los que llevan a un puesto o terminan en
    /// un nodo sin otra calle (no son lugar para dejar un avión tras el pushback).
    /// </summary>
    /// <returns>Cada tramo con el puesto al que lleva si es un ramal de entrada (-1 si es una calle de paso).</returns>
    public IEnumerable<(int A, int B, int SpurStand)> TaxiEdgesNear(LocalPoint p, double radiusM)
    {
        for (var n = 0; n < NodeCount; n++)
        {
            if (_runwayAt[n] >= 0 || IsParking(n))
                continue;
            foreach (var e in _adjacency[n])
            {
                if (e.To < n || _runwayAt[e.To] >= 0 || IsParking(e.To) || e.Type is TaxiPathType.Parking or TaxiPathType.Runway)
                    continue;
                if (LocalPoint.Distance(_positions[n], p) > radiusM && LocalPoint.Distance(_positions[e.To], p) > radiusM)
                    continue;
                // Un tramo es parte de un ramal si alguno de sus extremos lo es (el otro puede ser el cruce).
                var spur = SpurStand(n) >= 0 ? SpurStand(n) : SpurStand(e.To);
                yield return (n, e.To, spur);
            }
        }
    }

    /// <summary>
    /// Línea de entrada al puesto: el nodo del puesto y los nodos que siguen mientras no haya cruces (grado ≤ 2), hasta
    /// el primer cruce incluido. Es la línea pintada por la que el avión entró y por la que retrocede.
    /// </summary>
    public List<int> LeadIn(int parkingIndex, int maxNodes = 8)
    {
        var start = ParkingNode(parkingIndex);
        var chain = new List<int> { start };
        var prev = -1;
        var n = start;
        while (chain.Count < maxNodes)
        {
            var next = -1;
            foreach (var e in _adjacency[n])
            {
                if (e.To != prev && !IsParking(e.To))
                {
                    next = e.To;
                    break;
                }
            }
            if (next < 0)
                break;
            chain.Add(next);
            var degree = 0;
            foreach (var e in _adjacency[next])
            {
                if (!IsParking(e.To))
                    degree++;
            }
            if (degree > 2)
                break;
            prev = n;
            n = next;
        }
        return chain;
    }

    /// <summary>Cantidad de calles de rodaje que salen del nodo (sin contar puestos).</summary>
    public int TaxiDegree(int node)
    {
        var count = 0;
        foreach (var e in _adjacency[node])
        {
            if (!IsParking(e.To) && e.Type is not TaxiPathType.Parking)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Nodo de un ramal sin salida: menos de dos calles de rodaje (sin contar puestos), o un nodo que conecta a un
    /// puesto y tiene una sola calle. Se recorre el ramal: un nodo intermedio también cuenta si lleva solo a eso.
    /// </summary>
    public bool IsDeadEnd(int node) => SpurStand(node) >= 0;

    /// <summary>Índice del puesto al que lleva el ramal de entrada que pasa por el nodo, o -1.</summary>
    public int SpurStand(int node)
    {
        _spurStand ??= ComputeSpurs();
        return _spurStand[node];
    }

    private int[]? _spurStand;

    /// <summary>
    /// Ramales de entrada a puestos: desde cada nodo que conecta un puesto y no tiene otra salida, se recorre la línea
    /// hacia atrás mientras no haya cruces (grado 2). Las calles de plataforma con fondo no se tocan: allí sí se puede
    /// terminar un pushback.
    /// </summary>
    private int[] ComputeSpurs()
    {
        var degree = new int[NodeCount];
        var stand = new int[NodeCount];
        Array.Fill(stand, -1);
        for (var n = 0; n < NodeCount; n++)
        {
            if (IsParking(n))
                continue;
            foreach (var e in _adjacency[n])
            {
                if (IsParking(e.To) || e.Type is TaxiPathType.Parking)
                    stand[n] = IsParking(e.To) ? e.To - _airport.TaxiPoints.Count : stand[n];
                else
                    degree[n]++;
            }
        }
        var spur = new int[NodeCount];
        Array.Fill(spur, -1);
        for (var start = 0; start < NodeCount; start++)
        {
            if (stand[start] < 0 || degree[start] > 1)
                continue;
            var prev = -1;
            var n = start;
            while (n >= 0 && spur[n] < 0 && degree[n] <= 2)
            {
                spur[n] = stand[start];
                var next = -1;
                foreach (var e in _adjacency[n])
                {
                    if (e.To != prev && !IsParking(e.To) && e.Type is not TaxiPathType.Parking)
                    {
                        next = e.To;
                        break;
                    }
                }
                prev = n;
                n = next;
            }
        }
        return spur;
    }

    /// <summary>Distancia (m) desde p al tramo de rodaje más cercano (para saber si un punto está sobre pavimento).</summary>
    public double DistanceToNearestEdge(LocalPoint p)
    {
        var best = double.MaxValue;
        for (var n = 0; n < NodeCount; n++)
        {
            foreach (var e in _adjacency[n])
            {
                if (e.To < n || e.Type is TaxiPathType.Parking)
                    continue;
                var a = _positions[n];
                var b = _positions[e.To];
                var abE = b.EastM - a.EastM;
                var abN = b.NorthM - a.NorthM;
                var len2 = abE * abE + abN * abN;
                var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * abE + (p.NorthM - a.NorthM) * abN) / len2, 0, 1) : 0;
                var dE = p.EastM - (a.EastM + abE * t);
                var dN = p.NorthM - (a.NorthM + abN * t);
                best = Math.Min(best, Math.Sqrt(dE * dE + dN * dN));
            }
        }
        return best;
    }

    /// <summary>Tramo de calle (no pista ni puesto) más cercano a p, a menos de <paramref name="maxM"/>. false si no hay.</summary>
    public bool NearestEdge(LocalPoint p, out int from, out int to, double maxM = 12)
    {
        var best = maxM * maxM;
        from = to = -1;
        for (var n = 0; n < NodeCount; n++)
        {
            foreach (var e in _adjacency[n])
            {
                if (e.To < n || e.Type is TaxiPathType.Parking or TaxiPathType.Runway)
                    continue;
                var a = _positions[n];
                var b = _positions[e.To];
                var abE = b.EastM - a.EastM;
                var abN = b.NorthM - a.NorthM;
                var len2 = abE * abE + abN * abN;
                var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * abE + (p.NorthM - a.NorthM) * abN) / len2, 0, 1) : 0;
                var dE = p.EastM - (a.EastM + abE * t);
                var dN = p.NorthM - (a.NorthM + abN * t);
                var d = dE * dE + dN * dN;
                if (d < best)
                {
                    best = d;
                    from = n;
                    to = e.To;
                }
            }
        }
        return from >= 0;
    }

    /// <summary>Nombre de la calle de rodaje más cercana a p ("" si no tiene nombre o está a más de <paramref name="maxM"/>).</summary>
    public string NearestTaxiwayName(LocalPoint p, double maxM = 15)
    {
        var best = maxM * maxM;
        var name = "";
        for (var n = 0; n < NodeCount; n++)
        {
            foreach (var e in _adjacency[n])
            {
                if (e.To < n || e.Type is TaxiPathType.Parking or TaxiPathType.Runway)
                    continue;
                var a = _positions[n];
                var b = _positions[e.To];
                var abE = b.EastM - a.EastM;
                var abN = b.NorthM - a.NorthM;
                var len2 = abE * abE + abN * abN;
                var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * abE + (p.NorthM - a.NorthM) * abN) / len2, 0, 1) : 0;
                var dE = p.EastM - (a.EastM + abE * t);
                var dN = p.NorthM - (a.NorthM + abN * t);
                var d = dE * dE + dN * dN;
                if (d < best)
                {
                    best = d;
                    name = TaxiwayName(e.NameIndex);
                }
            }
        }
        return name;
    }

    /// <summary>
    /// ADR-085: entrada para despegar desde ese extremo; si ninguna llega cerca de la cabecera (aeropuertos sin calle
    /// paralela, como SCIE), la más cercana a ella y <paramref name="backtrack"/> = true: se rueda por la pista hasta la
    /// cabecera y se da la vuelta ("backtrack runway", CAP 413 cap. 4). null si la pista no tiene entradas.
    /// </summary>
    /// <param name="usable">Si se da, solo entradas que lo cumplen (p. ej. con ruta desde el puesto: en SCIE la entrada más
    /// cercana a la cabecera 20 es una calle sin conexión con las puertas, y la buena es A, a 262 m).</param>
    public RunwayEntry? DepartureEntryOrBacktrack(int runwayIndex, bool primaryEnd, out bool backtrack, Func<RunwayEntry, bool>? usable = null)
    {
        var halfLength = _airport.Runways[runwayIndex].LengthM / 2;
        var endAlong = primaryEnd ? -halfLength : halfLength;
        RunwayEntry? best = null;
        foreach (var e in _entries)
        {
            if (e.RunwayIndex == runwayIndex && (best is null || Math.Abs(e.AlongM - endAlong) < Math.Abs(best.AlongM - endAlong))
                && (usable is null || usable(e)))
                best = e;
        }
        backtrack = best is not null && Math.Abs(best.AlongM - endAlong) > MaxEntryToRunwayEndM;
        return best;
    }

    /// <summary>Entrada más cercana a un extremo de pista (la "cabecera" para despegar desde ese extremo), o null.</summary>
    public RunwayEntry? DepartureEntry(int runwayIndex, bool primaryEnd)
    {
        var halfLength = _airport.Runways[runwayIndex].LengthM / 2;
        var endAlong = primaryEnd ? -halfLength : halfLength;
        RunwayEntry? best = null;
        foreach (var e in _entries)
        {
            if (e.RunwayIndex != runwayIndex)
                continue;
            var d = Math.Abs(e.AlongM - endAlong);
            if (d <= MaxEntryToRunwayEndM && (best is null || d < Math.Abs(best.AlongM - endAlong)))
                best = e;
        }
        return best;
    }

    /// <summary>
    /// Ruta de rodaje (A*) entre dos nodos sin pisar ninguna pista (ADR-065, ADR-066): por calles troncales, sin usar la
    /// plataforma como atajo y sin virajes cerrados (ver <see cref="EdgeCost"/>). Devuelve null si no hay ruta.
    /// </summary>
    public TaxiRoute? FindRoute(int from, int to) => FindRoute(from, to, null);

    /// <summary>Recargo (m) por cruzar una pista: solo se cruza si no hay rodaje razonable sin hacerlo.</summary>
    public const double CrossingPenaltyM = 3000;

    /// <summary>
    /// Igual que <see cref="FindRoute(int, int)"/>, sin usar los tramos para los que <paramref name="blocked"/> devuelve
    /// true (desvíos para resolver bloqueos, RF-18b). Tampoco vuelve a <paramref name="avoidNode"/> si se indica.
    /// </summary>
    public TaxiRoute? FindRoute(int from, int to, Func<int, int, bool>? blocked, int avoidNode = -1) =>
        FindRoute(from, to, blocked, avoidNode, crossings: false, throughStands: false)
        ?? FindRoute(from, to, blocked, avoidNode, crossings: true, throughStands: false)
        // Último recurso: una calle que solo se une a la red por un puesto con dos enlaces.
        ?? FindRoute(from, to, blocked, avoidNode, crossings: false, throughStands: true)
        ?? FindRoute(from, to, blocked, avoidNode, crossings: true, throughStands: true);

    /// <summary>
    /// Con <paramref name="crossings"/>, puede cruzar pistas (ADR-068): entrar a un nodo de pista desde fuera cuesta
    /// <see cref="CrossingPenaltyM"/> y nunca rueda a lo largo de una pista (tramos de tipo pista). El avión se detiene en el
    /// punto de espera y cruza con autorización (<see cref="Engine.RunwayCrossing"/>).
    /// Sin <paramref name="throughStands"/>, un puesto solo puede ser el origen o el destino (ADR-128): hay puestos con dos
    /// enlaces a la red (LEMG: 54) y la ruta los usaba de atajo, en línea recta por la plataforma y por encima del puesto.
    /// </summary>
    private TaxiRoute? FindRoute(int from, int to, Func<int, int, bool>? blocked, int avoidNode, bool crossings, bool throughStands)
    {
        if (Rwy(from) >= 0 || Rwy(to) >= 0)
            return null;

        var g = new double[NodeCount];
        Array.Fill(g, double.PositiveInfinity);
        var cameFrom = new int[NodeCount];
        var cameBy = new Edge[NodeCount];
        var closed = new bool[NodeCount];
        var open = new PriorityQueue<int, double>();
        g[from] = 0;
        cameFrom[from] = -1;
        open.Enqueue(from, Heuristic(from, to));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == to)
                return BuildRoute(to, g[to], cameFrom, cameBy);
            if (closed[current])
                continue;
            closed[current] = true;

            foreach (var e in _adjacency[current])
            {
                if (closed[e.To] || e.To == avoidNode || blocked?.Invoke(current, e.To) == true)
                    continue;
                if (Rwy(e.To) >= 0 && (!crossings || AlongRunway(current, e)))
                    continue;
                if (!throughStands && e.To != to && IsParking(e.To))
                    continue;
                var tentative = g[current] + EdgeCost(current, e, cameFrom[current], cameBy[current], from, to);
                if (Rwy(e.To) >= 0 && Rwy(current) < 0)
                    tentative += CrossingPenaltyM;
                if (tentative < g[e.To])
                {
                    g[e.To] = tentative;
                    cameFrom[e.To] = current;
                    cameBy[e.To] = e;
                    open.Enqueue(e.To, tentative + Heuristic(e.To, to));
                }
            }
        }
        return null;
    }

    private double Heuristic(int a, int b) => LocalPoint.Distance(_positions[a], _positions[b]);

    /// <summary>
    /// Recargo de una calle de plataforma ("Path": calles de servicio entre puestos) usada como atajo, frente a una troncal.
    /// </summary>
    public const double ApronFactor = 1.35;

    /// <summary>Recargo de una calle de rodaje sin nombre.</summary>
    public const double UnnamedFactor = 1.1;

    /// <summary>
    /// Metros equivalentes por cambiar de calle con nombre. En 0: sin los sentidos de circulación del AIP (V-37), hacía que
    /// dos salidas llegaran al mismo punto de espera de la 35L de SCEL desde lados opuestos y se trabaran de frente.
    /// </summary>
    public const double NameChangeM = 0;

    /// <summary>Cerca del origen o del destino (m) no hay recargo por plataforma ni por calle sin nombre.</summary>
    public const double EndsFreeM = 250;

    /// <summary>Metros equivalentes por un viraje de más de 100° en un nodo (los aviones no hacen "zetas").</summary>
    public const double SharpTurnM = 250;

    /// <summary>
    /// Costo de recorrer <paramref name="e"/> llegando a <paramref name="at"/> desde <paramref name="prev"/> por
    /// <paramref name="prevEdge"/>: largo × recargo por tipo, más los cambios de calle y los virajes cerrados. APROXIMADO.
    /// </summary>
    private double EdgeCost(int at, Edge e, int prev, Edge prevEdge, int start, int goal)
    {
        // Salir de un puesto o llegar a él pasa por la plataforma: el recargo es solo para usarla de atajo (lejos de los
        // extremos). Sin esto, el puesto 22 de SCEL salía por otro lado (test de pushback de Marco).
        var nearEnds = Math.Min(Math.Min(LocalPoint.Distance(_positions[at], _positions[start]), LocalPoint.Distance(_positions[at], _positions[goal])),
            Math.Min(LocalPoint.Distance(_positions[e.To], _positions[start]), LocalPoint.Distance(_positions[e.To], _positions[goal]))) < EndsFreeM;
        var cost = e.LengthM * (nearEnds ? 1.0 : e.Type switch
        {
            TaxiPathType.Path => ApronFactor,
            TaxiPathType.Taxi when TaxiwayName(e.NameIndex).Length == 0 => UnnamedFactor,
            _ => 1.0,
        });
        // Hacia una pista de llegadas por una de sus calles de salida (ADR-092): esas calles son para dejar la pista.
        var exits = _exitFlow;
        if (exits.Count > 0 && e.LengthM > 0 && exits.TryGetValue(EdgeKey(at, e.To), out var outward))
        {
            var pa = _positions[at];
            var pb = _positions[e.To];
            var bearing = Math.Atan2(pb.EastM - pa.EastM, pb.NorthM - pa.NorthM) * GeoMath.RadToDeg;
            if (Math.Abs(GeoMath.AngleDiffDeg(outward, bearing)) > 90)
                cost *= AgainstExitFactor;
        }
        // Contra el sentido de una calle paralela a la pista en uso (ADR-067).
        var flow = _flow;
        if (flow.Count > 0 && e.LengthM > 0 && flow.TryGetValue(EdgeKey(at, e.To), out var want))
        {
            var pa = _positions[at];
            var pb = _positions[e.To];
            var bearing = Math.Atan2(pb.EastM - pa.EastM, pb.NorthM - pa.NorthM) * GeoMath.RadToDeg;
            if (Math.Abs(GeoMath.AngleDiffDeg(want, bearing)) > 90)
                cost *= AgainstFlowFactor;
        }
        if (prev < 0 || e.LengthM <= 0)
            return cost;
        var a = TaxiwayName(prevEdge.NameIndex);
        var b = TaxiwayName(e.NameIndex);
        if (a.Length > 0 && b.Length > 0 && a != b)
            cost += NameChangeM;
        var p0 = _positions[prev];
        var p1 = _positions[at];
        var p2 = _positions[e.To];
        if (LocalPoint.Distance(p0, p1) > 1)
        {
            var h1 = Math.Atan2(p1.EastM - p0.EastM, p1.NorthM - p0.NorthM) * GeoMath.RadToDeg;
            var h2 = Math.Atan2(p2.EastM - p1.EastM, p2.NorthM - p1.NorthM) * GeoMath.RadToDeg;
            if (Math.Abs(GeoMath.AngleDiffDeg(h1, h2)) > 100)
                cost += SharpTurnM;
        }
        return cost;
    }

    private TaxiRoute BuildRoute(int to, double length, int[] cameFrom, Edge[] cameBy)
    {
        var nodes = new List<int>();
        var names = new List<string>();
        for (var n = to; n >= 0; n = cameFrom[n])
        {
            nodes.Add(n);
            if (cameFrom[n] >= 0)
            {
                var name = TaxiwayName(cameBy[n].NameIndex);
                if (name.Length > 0 && (names.Count == 0 || names[^1] != name))
                    names.Add(name);
            }
        }
        nodes.Reverse();
        names.Reverse();
        return new TaxiRoute(nodes, length, names);
    }

    private string TaxiwayName(int nameIndex) =>
        nameIndex > 0 && nameIndex < _airport.TaxiNames.Count ? _airport.TaxiNames[nameIndex] : "";

    private void AddEdges()
    {
        var pointCount = _airport.TaxiPoints.Count;
        foreach (var p in _airport.TaxiPaths)
        {
            if (p.Type is not (TaxiPathType.Taxi or TaxiPathType.Path or TaxiPathType.Parking or TaxiPathType.Runway))
                continue;

            var a = p.Start;
            var b = p.Type == TaxiPathType.Parking ? pointCount + p.End : p.End;
            if (a < 0 || a >= pointCount || b < 0 || b >= NodeCount || a == b)
            {
                InvalidPathCount++;
                continue;
            }

            var length = LocalPoint.Distance(_positions[a], _positions[b]);
            _adjacency[a].Add(new Edge(b, length, p.Type, p.NameIndex));
            _adjacency[b].Add(new Edge(a, length, p.Type, p.NameIndex));
        }
    }

    private void JoinCoincidentPoints()
    {
        // Grilla de 1 m: basta revisar la celda propia y las vecinas.
        var cells = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < _airport.TaxiPoints.Count; i++)
        {
            var key = ((long)Math.Floor(_positions[i].EastM), (long)Math.Floor(_positions[i].NorthM));
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = [];
            list.Add(i);
        }

        for (var i = 0; i < _airport.TaxiPoints.Count; i++)
        {
            var cx = (long)Math.Floor(_positions[i].EastM);
            var cy = (long)Math.Floor(_positions[i].NorthM);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!cells.TryGetValue((cx + dx, cy + dy), out var list))
                    continue;
                foreach (var j in list)
                {
                    if (j <= i || LocalPoint.Distance(_positions[i], _positions[j]) > CoincidentToleranceM)
                        continue;
                    if (_adjacency[i].Exists(e => e.To == j))
                        continue;
                    _adjacency[i].Add(new Edge(j, 0, TaxiPathType.Path, 0));
                    _adjacency[j].Add(new Edge(i, 0, TaxiPathType.Path, 0));
                    JoinedCoincidentCount++;
                }
            }
        }
    }

    private void FindRunwayEntries()
    {
        for (var x = 0; x < NodeCount; x++)
        {
            if (_runwayAt[x] >= 0)
                continue;
            foreach (var e in _adjacency[x])
            {
                var rwy = _runwayAt[e.To];
                if (rwy < 0 || e.Type == TaxiPathType.Runway)
                    continue;
                var (along, _) = RunwayFrame(rwy, _positions[e.To]);
                var hold = FindMarkedHold(x, rwy);
                _entries.Add(new RunwayEntry(x, e.To, rwy, along, hold ?? x, hold is not null, TaxiwayName(e.NameIndex)));
            }
        }
    }

    /// <summary>Punto de espera marcado más cercano antes de la entrada, sin pisar pista, o null.</summary>
    private int? FindMarkedHold(int entryNode, int runwayIndex)
    {
        var dist = new Dictionary<int, double> { [entryNode] = 0 };
        var open = new PriorityQueue<int, double>();
        open.Enqueue(entryNode, 0);
        while (open.TryDequeue(out var n, out var d))
        {
            if (d > dist[n])
                continue;
            if (n < _airport.TaxiPoints.Count && _airport.TaxiPoints[n].IsHoldShort &&
                Math.Abs(RunwayFrame(runwayIndex, _positions[n]).Cross) <= MaxHoldCrossTrackM)
                return n;
            foreach (var e in _adjacency[n])
            {
                var nd = d + e.LengthM;
                if (_runwayAt[e.To] >= 0 || nd > MaxHoldSearchM || IsParking(e.To))
                    continue;
                if (!dist.TryGetValue(e.To, out var old) || nd < old)
                {
                    dist[e.To] = nd;
                    open.Enqueue(e.To, nd);
                }
            }
        }
        return null;
    }

    /// <summary>Índice de la pista sobre la que está el punto (con sus márgenes), o −1.</summary>
    public int RunwayAtPoint(LocalPoint p) => FindRunway(p);

    private int FindRunway(LocalPoint p)
    {
        for (var i = 0; i < _airport.Runways.Count; i++)
        {
            var r = _airport.Runways[i];
            var (along, cross) = RunwayFrame(i, p);
            if (Math.Abs(cross) <= r.WidthM / 2 + RunwayMarginM && Math.Abs(along) <= r.LengthM / 2 + RunwayEndMarginM)
                return i;
        }
        return -1;
    }

    /// <summary>Coordenadas respecto de la pista: a lo largo (sentido del rumbo primario) y transversal (+ a la derecha).</summary>
    private (double Along, double Cross) RunwayFrame(int runwayIndex, LocalPoint p)
    {
        var r = _airport.Runways[runwayIndex];
        var c = Projection.ToLocal(r.Center);
        var h = r.HeadingTrueDeg * GeoMath.DegToRad;
        var ux = Math.Sin(h);
        var uy = Math.Cos(h);
        var dx = p.EastM - c.EastM;
        var dy = p.NorthM - c.NorthM;
        return (dx * ux + dy * uy, dx * uy - dy * ux);
    }
}
