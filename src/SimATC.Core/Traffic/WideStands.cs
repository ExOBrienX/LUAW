using System.Runtime.CompilerServices;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

namespace SimATC.Core.Traffic;

/// <summary>
/// Puertas donde cabe un fuselaje ancho aunque los datos digan que son chicas (ADR-167): 42 de los 56 aeropuertos grandes no
/// tienen ninguna puerta para ellos en los datos del simulador (Miami: 132, todas de 18 o 23 m de radio). Se mide el espacio
/// real: fondo libre hasta la calle de rodaje y, en la salida, distancia a los demás puestos. Las vecinas que tocaría quedan sin
/// usar mientras esté, y el avión se estaciona más atrás para que la nariz no pase de donde llega la de uno del tamaño de la puerta.
/// </summary>
public sealed class WideStands
{
    /// <summary>Radio desde el que una puerta es de fuselaje ancho en los datos (m).</summary>
    public const double RealWideRadiusM = 29;

    /// <summary>Puertas para fuselaje ancho que se buscan, como parte de todas las puertas (contando las reales).</summary>
    public const double WideShare = 0.13;

    /// <summary>Radio mínimo de la puerta (m): en una más chica el edificio queda encima.</summary>
    public const double MinGateRadiusM = 17;

    /// <summary>Media envergadura del A380 (m); los cfg traen menos (71,6 m de envergadura en vez de 79,75).</summary>
    public const double SuperHalfSpanM = 40;

    /// <summary>Media envergadura (m) de un 777: hasta ahí las vecinas no cuentan en la salida, porque quedan sin usar.</summary>
    private const double NeighbourHalfSpanM = 33;

    /// <summary>La media envergadura que se usa para buscar espacio.</summary>
    public static double HalfSpanFor(AircraftModel model, double wingspanM) =>
        model.Wake == WakeCategory.Super ? Math.Max(SuperHalfSpanM, wingspanM / 2) : wingspanM / 2;

    /// <summary>Holgura (m) del ala con los puestos vecinos ocupados y en la calle de salida.</summary>
    public const double WingMarginM = 3;

    /// <summary>Espacio (m) entre la cola y el eje de la calle: media ala de un pasillo único más margen.</summary>
    public const double TailToLaneM = 22;

    /// <summary>Tramo de la salida que se revisa (m): donde están las puertas vecinas y las de enfrente.</summary>
    public const double ExitCheckM = 700;

    /// <summary>
    /// Nariz y cola respecto al punto de referencia, a partir de la rueda de nariz de flight_model.cfg. APROXIMADO (B77W: 31 m
    /// hasta la rueda y 36,5 m hasta la nariz; la cola queda unos metros más lejos que la nariz).
    /// </summary>
    private const double NoseOverhangM = 5.5;
    private const double TailExtraM = 4;

    private readonly AirportData _airport;
    private readonly Dictionary<int, (double TailRoomM, double ExitHalfM)> _rooms = [];

    private WideStands(AirportData airport) => _airport = airport;

    /// <summary>Cuántos fuselajes anchos se ponen así a la vez (0: el aeropuerto tiene puertas para ellos).</summary>
    public int Max { get; private init; }

    /// <summary>Puertas con espacio para alguno.</summary>
    public IReadOnlyCollection<int> Candidates => _rooms.Keys;

    private static readonly ConditionalWeakTable<TaxiGraph, WideStands> Cache = new();

    public static WideStands For(AirportData airport, TaxiGraph graph) => Cache.GetValue(graph, g => Build(airport, g));

    private static WideStands Build(AirportData airport, TaxiGraph graph)
    {
        var gates = new List<int>();
        var others = new List<int>();
        var real = 0;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var p = airport.Parkings[i];
            if (ParkedFleetPlanner.AirlineStand(p))
                others.Add(i);
            if (!ParkedFleetPlanner.Gate(p))
                continue;
            gates.Add(i);
            if (p.RadiusM >= RealWideRadiusM)
                real++;
        }
        var result = new WideStands(airport) { Max = Math.Max(0, (int)Math.Round(gates.Count * WideShare) - real) };
        if (result.Max == 0)
            return result;
        var pos = new LocalPoint[airport.Parkings.Count];
        for (var i = 0; i < pos.Length; i++)
            pos[i] = graph.Projection.ToLocal(airport.Parkings[i].Position);
        var holds = graph.RunwayEntries.Select(e => e.HoldNode).Distinct().ToList();
        var step = Math.Max(1, holds.Count / 6);
        foreach (var i in gates)
        {
            var p = airport.Parkings[i];
            if (p.RadiusM < MinGateRadiusM || !graph.StandReachable(i))
                continue;
            var room = TailRoom(graph, pos[i], p.HeadingTrueDeg);
            if (room < 25)
                continue;
            var exit = 0.0;
            for (var h = 0; h < holds.Count; h += step)
            {
                if (graph.FindRoute(graph.ParkingNode(i), holds[h]) is { } r)
                    exit = Math.Max(exit, ExitHalf(airport, graph, r, i, pos, others));
            }
            if (exit > 22)
                result._rooms[i] = (room, exit);
        }
        return result;
    }

    /// <summary>Hasta dónde (m detrás del puesto) puede llegar la cola sin acercarse a una calle de paso.</summary>
    private static double TailRoom(TaxiGraph graph, LocalPoint stand, double headingDeg)
    {
        var edges = new List<(LocalPoint A, LocalPoint B)>();
        foreach (var (a, b, spur) in graph.TaxiEdgesNear(stand, 300))
        {
            if (spur < 0)
                edges.Add((graph.Position(a), graph.Position(b)));
        }
        for (var t = 0.0; t <= 80; t += 2)
        {
            var q = Polyline.Offset(stand, headingDeg + 180, t);
            foreach (var (a, b) in edges)
            {
                if (SegmentDistance(q, a, b) < TailToLaneM)
                    return t;
            }
        }
        return 80;
    }

    /// <summary>Lo más cerca que pasa la salida de otro puesto (su avión incluido): la media envergadura que cabe.</summary>
    private static double ExitHalf(AirportData airport, TaxiGraph graph, TaxiRoute r, int stand, LocalPoint[] pos, List<int> others)
    {
        var best = double.MaxValue;
        var along = 0.0;
        for (var k = 0; k + 1 < r.Nodes.Count && along < ExitCheckM; k++)
        {
            var a = graph.Position(r.Nodes[k]);
            var b = graph.Position(r.Nodes[k + 1]);
            along += LocalPoint.Distance(a, b);
            foreach (var j in others)
            {
                // Las vecinas que tocaría estacionado quedan sin usar: no estorban.
                if (j == stand || LocalPoint.Distance(pos[stand], pos[j]) < NeighbourHalfSpanM + airport.Parkings[j].RadiusM)
                    continue;
                best = Math.Min(best, SegmentDistance(pos[j], a, b) - Math.Min(22, airport.Parkings[j].RadiusM));
            }
        }
        return best;
    }

    private static double SegmentDistance(LocalPoint p, LocalPoint a, LocalPoint b)
    {
        var (dx, dy) = (b.EastM - a.EastM, b.NorthM - a.NorthM);
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-6 ? 0 : Math.Clamp(((p.EastM - a.EastM) * dx + (p.NorthM - a.NorthM) * dy) / len2, 0, 1);
        return LocalPoint.Distance(p, new LocalPoint(a.EastM + dx * t, a.NorthM + dy * t));
    }

    /// <summary>
    /// Cuánto más atrás (m) se estaciona ese avión en esa puerta, o null si no cabe. No mira si las vecinas están ocupadas.
    /// </summary>
    public double? BackFor(int stand, AircraftModel model, double halfSpanM)
    {
        if (Max == 0 || model.NoseGearM <= 0 || !_rooms.TryGetValue(stand, out var room))
            return null;
        var nose = model.NoseGearM + NoseOverhangM;
        var back = Math.Max(0, nose - _airport.Parkings[stand].RadiusM - 2);
        return room.ExitHalfM >= halfSpanM + WingMarginM && back + nose + TailExtraM <= room.TailRoomM ? back : null;
    }

    /// <summary>Holgura de la salida (m): para preferir las puertas de salida fácil.</summary>
    public double ExitHalfM(int stand) => _rooms.TryGetValue(stand, out var r) ? r.ExitHalfM : 0;
}
