using SimATC.Core.Geo;

namespace SimATC.Core.World;

/// <summary>Un tramo de aerovía entre dos fijos (una vez, aunque aparezca en los dos extremos).</summary>
public readonly record struct AirwaySegment(GeoPoint From, GeoPoint To, string Airway, int Level);

/// <summary>
/// Los tramos de aerovía ya descargados, sin repetir y en una grilla de 2°: el radar pide los de lo que se ve sin recorrer
/// toda la caché (ADR-064). Se arma fuera del hilo de la interfaz y se reemplaza entero.
/// </summary>
public sealed class AirwaySegments
{
    private const double CellDeg = 2;

    private readonly AirwaySegment[] _segments;
    private readonly Dictionary<(int, int), int[]> _cells;
    private readonly int[] _stamp;
    private int _query;

    private AirwaySegments(AirwaySegment[] segments, Dictionary<(int, int), int[]> cells)
    {
        _segments = segments;
        _cells = cells;
        _stamp = new int[segments.Length];
    }

    public int Count => _segments.Length;

    /// <summary>Arma el índice con los fijos recibidos (cada tramo hacia adelante, una vez por par de fijos y aerovía).</summary>
    public static AirwaySegments Build(IReadOnlyList<AirwayFix> fixes)
    {
        var seen = new HashSet<(string, string, string)>();
        var list = new List<AirwaySegment>();
        foreach (var f in fixes)
        {
            foreach (var l in f.Links)
            {
                if (!l.Forward || l.Position is not { } to || l.Icao.Length == 0)
                    continue;
                var key = string.CompareOrdinal(f.Icao, l.Icao) < 0 ? (f.Icao, l.Icao, l.Airway) : (l.Icao, f.Icao, l.Airway);
                if (seen.Add(key))
                    list.Add(new AirwaySegment(f.Position, to, l.Airway, l.Level));
            }
        }
        var cells = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            // Un tramo que cruza el antimeridiano se salta (no se dibuja bien en un mapa plano).
            if (Math.Abs(s.From.LonDeg - s.To.LonDeg) > 180)
                continue;
            var (la0, la1) = (Cell(Math.Min(s.From.LatDeg, s.To.LatDeg)), Cell(Math.Max(s.From.LatDeg, s.To.LatDeg)));
            var (lo0, lo1) = (Cell(Math.Min(s.From.LonDeg, s.To.LonDeg)), Cell(Math.Max(s.From.LonDeg, s.To.LonDeg)));
            for (var la = la0; la <= la1; la++)
            {
                for (var lo = lo0; lo <= lo1; lo++)
                {
                    if (!cells.TryGetValue((la, lo), out var c))
                        cells[(la, lo)] = c = [];
                    c.Add(i);
                }
            }
        }
        return new AirwaySegments([.. list], cells.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private static int Cell(double deg) => (int)Math.Floor(deg / CellDeg);

    /// <summary>Los tramos que tocan el recuadro (grados), sobre una lista que se reutiliza. Un hilo a la vez.</summary>
    public void InBox(double minLat, double maxLat, double minLon, double maxLon, List<AirwaySegment> into)
    {
        into.Clear();
        _query++;
        for (var la = Cell(minLat); la <= Cell(maxLat); la++)
        {
            for (var lo = Cell(minLon); lo <= Cell(maxLon); lo++)
            {
                if (!_cells.TryGetValue((la, lo), out var cell))
                    continue;
                foreach (var i in cell)
                {
                    if (_stamp[i] == _query)
                        continue;
                    _stamp[i] = _query;
                    into.Add(_segments[i]);
                }
            }
        }
    }
}
