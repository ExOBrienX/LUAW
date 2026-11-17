using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;

namespace SimATC.Core.Taxi;

/// <summary>La ruta de rodaje que Ground le da al piloto: los puntos (para el radar), las calles por nombre y el punto de espera.</summary>
public sealed record PilotTaxiRoute(IReadOnlyList<GeoPoint> Points, IReadOnlyList<string> Taxiways, GeoPoint HoldShort, string Runway)
{
    /// <summary>"K, L, B" en el orden en que se recorren (sin repetir).</summary>
    public string ViaText => string.Join(", ", Taxiways);

    /// <summary>
    /// Desde donde está el piloto hasta el punto de espera de la cabecera <paramref name="runway"/>: el nodo del grafo más
    /// cercano (el puesto si está en uno; si no, la calle más cercana) y la ruta más corta del grafo (A*, sin pisar pistas).
    /// Después de un pushback (<paramref name="fromStand"/> = false) no se parte del puesto, y con el rumbo se prefieren
    /// los nodos que están delante del avión (no rueda hacia atrás).
    /// </summary>
    public static PilotTaxiRoute? From(TaxiGraph graph, AirportData airport, GeoPoint pilot, string runway, bool fromStand = true,
        double? headingDeg = null)
    {
        if (RunwayIndex(airport, runway) is not { } r)
            return null;
        // Como los IA (ADR-093/116): entre las entradas cerca de la cabecera (a ambos lados de la pista), la de rodaje más
        // corto que no cruce la propia pista de despegue. Prueba de Marco en RKSI: a él lo mandaban a la entrada del otro lado
        // de la 33L, cruzándola, mientras los IA entraban por su lado.
        var half = airport.Runways[r.Index].LengthM / 2;
        var endAlong = r.Primary ? -half : half;
        PilotTaxiRoute? best = null;
        var bestM = double.MaxValue;
        foreach (var e in graph.RunwayEntries)
        {
            if (e.RunwayIndex != r.Index || Math.Abs(e.AlongM - endAlong) > TaxiGraph.MaxEntryToRunwayEndM
                || FromEntry(graph, airport, pilot, runway, e, fromStand, headingDeg, out var crossesOwn, r.Index) is not { } candidate
                || crossesOwn)
                continue;
            var m = LengthM(candidate.Points);
            if (m < bestM)
                (best, bestM) = (candidate, m);
        }
        if (best is not null)
            return best;
        // Sin una así: la entrada más cercana a la cabecera con ruta desde el piloto (ADR-085: en SCIE la más cercana a la 20
        // no conecta).
        var tried = new HashSet<int>();
        while (tried.Count < 6 && graph.DepartureEntryOrBacktrack(r.Index, r.Primary, out _, e => !tried.Contains(e.HoldNode)) is { } entry)
        {
            tried.Add(entry.HoldNode);
            if (FromEntry(graph, airport, pilot, runway, entry, fromStand, headingDeg, out _, r.Index) is { } found)
                return found;
        }
        return null;
    }

    private static double LengthM(IReadOnlyList<GeoPoint> points)
    {
        var m = 0.0;
        for (var i = 1; i < points.Count; i++)
            m += GeoMath.DistanceM(points[i - 1], points[i]);
        return m;
    }

    private static PilotTaxiRoute? FromEntry(TaxiGraph graph, AirportData airport, GeoPoint pilot, string runway, RunwayEntry entry,
        bool fromStand, double? headingDeg, out bool crossesOwn, int runwayIndex)
    {
        crossesOwn = false;
        var here = graph.Projection.ToLocal(pilot);
        var start = -1;
        // En un puesto (a menos de 60 m), se sale desde el puesto, como los aviones IA; si no, desde la calle más cercana.
        var bestStand = 60.0;
        for (var i = 0; fromStand && i < airport.Parkings.Count; i++)
        {
            var d = GeoMath.DistanceM(airport.Parkings[i].Position, pilot);
            if (d < bestStand)
            {
                bestStand = d;
                start = graph.ParkingNode(i);
            }
        }
        TaxiRoute? route = null;
        if (start >= 0)
        {
            route = graph.FindRoute(start, entry.HoldNode);
        }
        else
        {
            // Desde la calle: los nodos más cercanos (los de atrás cuentan el triple); si uno no tiene ruta, el siguiente.
            var candidates = new List<(double D, int Node)>();
            for (var n = 0; n < graph.NodeCount; n++)
            {
                if (graph.IsParking(n) || graph.RunwayAt(n) >= 0)
                    continue;
                var d = LocalPoint.Distance(graph.Position(n), here);
                if (headingDeg is { } h && d > 5 && Math.Abs(GeoMath.AngleDiffDeg(h, Polyline.Bearing(here, graph.Position(n)))) > 100)
                    d *= 3;
                candidates.Add((d, n));
            }
            candidates.Sort((x, y) => x.D.CompareTo(y.D));
            for (var i = 0; i < Math.Min(5, candidates.Count) && route is null; i++)
                route = graph.FindRoute(candidates[i].Node, entry.HoldNode);
        }
        if (route is null)
            return null;
        foreach (var n in route.Nodes)
            crossesOwn |= graph.RunwayAt(n) == runwayIndex;
        var points = new List<GeoPoint> { pilot };
        points.AddRange(route.Nodes.Select(n => graph.Projection.ToGeo(graph.Position(n))));
        var names = new List<string>();
        foreach (var t in route.Taxiways)
        {
            if (t.Length > 0 && (names.Count == 0 || names[^1] != t))
                names.Add(t);
        }
        return new PilotTaxiRoute(points, names, graph.Projection.ToGeo(graph.Position(entry.HoldNode)), runway);
    }

    public static (int Index, bool Primary)? RunwayIndex(AirportData airport, string ident)
    {
        for (var i = 0; i < airport.Runways.Count; i++)
        {
            if (airport.Runways[i].Primary.Ident == ident)
                return (i, true);
            if (airport.Runways[i].Secondary.Ident == ident)
                return (i, false);
        }
        return null;
    }
}
