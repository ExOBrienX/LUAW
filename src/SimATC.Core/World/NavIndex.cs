using System.IO.Compression;
using SimATC.Core.Geo;

namespace SimATC.Core.World;

/// <summary>Tipo de fijo de navegación.</summary>
public enum NavKind : byte
{
    Waypoint,
    Vor,
    Ndb,
}

/// <summary>Un fijo del simulador (waypoint, VOR o NDB) con su región OACI.</summary>
public readonly record struct NavFix(string Ident, string Region, GeoPoint Position, NavKind Kind);

/// <summary>
/// Todos los fijos del mundo del simulador (<c>SimConnect_RequestAllFacilities</c>, que devuelve la lista entera sin importar
/// la distancia; SDK 1.8.1, verificado en la doc, ADR-062). Sirve para resolver los nombres de una ruta escrita (el fijo con
/// ese nombre más cercano al anterior, sin adivinar regiones) y para el radar global. Se guarda en disco: el simulador
/// solo se consulta una vez por semana.
/// </summary>
public sealed class NavIndex
{
    private const int FileVersion = 1;

    private readonly NavFix[] _all;
    private readonly Dictionary<string, int[]> _byIdent;
    // Celdas de 1° (clave: (lat + 90) * 360 + (lon + 180)) con los índices de sus fijos.
    private readonly Dictionary<int, int[]> _cells;

    public NavIndex(IEnumerable<NavFix> fixes)
    {
        _all = [.. fixes.Where(f => f.Ident.Length > 0)];
        _byIdent = _all.Select((f, i) => (f.Ident, i)).GroupBy(x => x.Ident, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray(), StringComparer.Ordinal);
        _cells = _all.Select((f, i) => (Cell: CellOf(f.Position.LatDeg, f.Position.LonDeg), i)).GroupBy(x => x.Cell)
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
    }

    public int Count => _all.Length;

    private static int CellOf(double lat, double lon) =>
        ((int)Math.Floor(Math.Clamp(lat, -90, 89.999)) + 90) * 360 + ((int)Math.Floor(GeoMath.NormalizeLonDeg(lon)) + 180);

    /// <summary>Los fijos con ese nombre (puede haber varios en el mundo).</summary>
    public IEnumerable<NavFix> Named(string ident)
    {
        if (!_byIdent.TryGetValue(ident, out var list))
            yield break;
        foreach (var i in list)
            yield return _all[i];
    }

    /// <summary>El fijo con ese nombre más cercano a <paramref name="near"/>, si está a menos de <paramref name="maxNm"/>.</summary>
    public NavFix? Nearest(string ident, GeoPoint near, double maxNm = 1200)
    {
        if (!_byIdent.TryGetValue(ident, out var list))
            return null;
        NavFix? best = null;
        var bestD = maxNm * 1852;
        foreach (var i in list)
        {
            var d = GeoMath.DistanceM(_all[i].Position, near);
            if (d < bestD)
            {
                bestD = d;
                best = _all[i];
            }
        }
        return best;
    }

    /// <summary>
    /// Fijos dentro del recuadro (grados), hasta <paramref name="max"/>: solo de <paramref name="only"/> si viene, o solo
    /// radioayudas (VOR y NDB) con <paramref name="navaids"/>. Para el radar: sobre una lista que se reutiliza.
    /// </summary>
    public void InBox(double minLat, double maxLat, double minLon, double maxLon, List<NavFix> into, int max, NavKind? only = null,
        bool navaids = false)
    {
        into.Clear();
        var lat0 = (int)Math.Floor(Math.Max(-90, minLat));
        var lat1 = (int)Math.Floor(Math.Min(89.999, maxLat));
        var lonSpan = Math.Min(360, maxLon - minLon);
        var lon0 = (int)Math.Floor(minLon);
        var lon1 = lon0 + (int)Math.Ceiling(lonSpan);
        for (var la = lat0; la <= lat1; la++)
        {
            for (var lo = lon0; lo <= lon1; lo++)
            {
                if (!_cells.TryGetValue(CellOf(la, lo), out var cell))
                    continue;
                foreach (var i in cell)
                {
                    var f = _all[i];
                    if (only is { } k && f.Kind != k || navaids && f.Kind == NavKind.Waypoint)
                        continue;
                    if (f.Position.LatDeg < minLat || f.Position.LatDeg > maxLat)
                        continue;
                    into.Add(f);
                    if (into.Count >= max)
                        return;
                }
            }
        }
    }

    public static string PathFor(string dir) => Path.Combine(dir, "nav-index.bin.gz");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(FileVersion);
            w.Write(_all.Length);
            foreach (var f in _all)
            {
                w.Write(f.Ident);
                w.Write(f.Region);
                w.Write(f.Position.LatDeg);
                w.Write(f.Position.LonDeg);
                w.Write((byte)f.Kind);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Lo guardado en disco, o null si no hay o está dañado.</summary>
    public static NavIndex? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            if (r.ReadInt32() != FileVersion)
                return null;
            var n = r.ReadInt32();
            var list = new List<NavFix>(n);
            for (var i = 0; i < n; i++)
                list.Add(new NavFix(r.ReadString(), r.ReadString(), new GeoPoint(r.ReadDouble(), r.ReadDouble()), (NavKind)r.ReadByte()));
            return new NavIndex(list);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
