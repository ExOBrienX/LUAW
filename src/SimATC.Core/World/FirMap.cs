using System.Globalization;
using System.Text.Json;
using SimATC.Core.Comms;
using SimATC.Core.Geo;

namespace SimATC.Core.World;

/// <summary>Una FIR de VATSpy: su código, su nombre ("Santiago"), el prefijo de los indicativos de VATSIM y el sufijo de radio del país.</summary>
public sealed record Fir(string Icao, string Name, string CallsignPrefix, string RadioSuffix)
{
    /// <summary>El indicativo del Centro: "Santiago Radar" (sufijo del país en VATSpy) o, en español, "Santiago Control".</summary>
    public string Callsign(AtcLanguage language) =>
        $"{RadioName(Name)} {(language == AtcLanguage.Spanish ? "Control" : RadioSuffix.Length > 0 ? RadioSuffix : "Control")}";

    /// <summary>
    /// El nombre que se dice por radio: VATSpy describe los sectores ("London TMA South (Up to FL195) - London",
    /// "Kano (West)"); por radio solo va el nombre tras el último " - ", sin lo que va entre paréntesis.
    /// </summary>
    public static string RadioName(string vatspyName)
    {
        var name = vatspyName;
        var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && dash + 3 < name.Length)
            name = name[(dash + 3)..];
        var paren = name.IndexOf('(');
        if (paren > 0)
            name = name[..paren];
        return name.Trim();
    }
}

/// <summary>
/// Las FIR del mundo con sus límites (VATSpy Data Project, CC BY-SA 4.0, ADR-071): en qué FIR está un punto. Se carga una
/// vez; la búsqueda primero descarta por caja y después prueba el polígono (sin asignaciones).
/// </summary>
public sealed class FirMap
{
    private sealed record Area(Fir Fir, bool Oceanic, double MinLat, double MaxLat, double MinLon, double MaxLon, double[][] Rings)
    {
        public double BoxSize => (MaxLat - MinLat) * (MaxLon - MinLon);
    }

    private readonly List<Area> _areas;
    private readonly List<Area> _found = [];

    private FirMap(List<Area> areas) => _areas = areas;

    public int Count => _areas.Count;

    public static string DatPathFor(string dataDir) => Path.Combine(dataDir, "vatspy", "VATSpy.dat");

    public static string BoundariesPathFor(string dataDir) => Path.Combine(dataDir, "vatspy", "Boundaries.geojson");

    public static FirMap Load(string datPath, string boundariesPath)
    {
        using var stream = File.OpenRead(boundariesPath);
        return Parse(File.ReadAllText(datPath), stream);
    }

    public static FirMap Parse(string dat, Stream boundaries)
    {
        // [Countries]: "Chile|SC|Radar"; [FIRs]: "SCEZ|Santiago||SCEZ" (ICAO|nombre|prefijo|límite).
        var suffixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var firs = new Dictionary<string, Fir>(StringComparer.Ordinal);
        var section = "";
        foreach (var raw in dat.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                section = line;
                continue;
            }
            var f = line.Split('|');
            if (section == "[Countries]" && f.Length >= 3 && f[1].Length > 0)
                suffixes[f[1]] = f[2];
            else if (section == "[FIRs]" && f.Length >= 4)
            {
                var boundary = f[3].Length > 0 ? f[3] : f[0];
                // La primera línea de cada límite es la FIR base (las siguientes suelen ser sectores superiores).
                if (!firs.ContainsKey(boundary))
                    firs[boundary] = new Fir(f[0], f[1], f[2], SuffixFor(f[0], suffixes));
            }
        }

        var areas = new List<Area>();
        using var doc = JsonDocument.Parse(boundaries);
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            var id = props.GetProperty("id").GetString() ?? "";
            if (!firs.TryGetValue(id, out var fir))
                continue;
            var oceanic = props.TryGetProperty("oceanic", out var o) && o.GetString() == "1";
            var geometry = feature.GetProperty("geometry");
            if (geometry.GetProperty("type").GetString() != "MultiPolygon")
                continue;
            var rings = new List<double[]>();
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            foreach (var polygon in geometry.GetProperty("coordinates").EnumerateArray())
            {
                // Solo el anillo exterior de cada polígono (los huecos no cambian la FIR en la práctica).
                var outer = polygon[0];
                var ring = new double[outer.GetArrayLength() * 2];
                var i = 0;
                foreach (var pt in outer.EnumerateArray())
                {
                    var lon = pt[0].GetDouble();
                    var lat = pt[1].GetDouble();
                    ring[i++] = lon;
                    ring[i++] = lat;
                    minLat = Math.Min(minLat, lat);
                    maxLat = Math.Max(maxLat, lat);
                    minLon = Math.Min(minLon, lon);
                    maxLon = Math.Max(maxLon, lon);
                }
                rings.Add(ring);
            }
            if (rings.Count > 0)
                areas.Add(new Area(fir, oceanic, minLat, maxLat, minLon, maxLon, [.. rings]));
        }
        return new FirMap(areas);
    }

    /// <summary>El sufijo de radio del país por el prefijo OACI más largo que calce ("SC" → "Radar").</summary>
    private static string SuffixFor(string icao, Dictionary<string, string> suffixes)
    {
        for (var n = Math.Min(2, icao.Length); n >= 1; n--)
        {
            if (suffixes.TryGetValue(icao[..n], out var s))
                return s;
        }
        return "";
    }

    /// <summary>La FIR en ese punto (la continental antes que la oceánica; si se solapan, la más chica), o null.</summary>
    public Fir? At(GeoPoint p)
    {
        Area? best = null;
        for (var i = 0; i < _areas.Count; i++)
        {
            var a = _areas[i];
            if (p.LatDeg < a.MinLat || p.LatDeg > a.MaxLat || p.LonDeg < a.MinLon || p.LonDeg > a.MaxLon || !Contains(a.Rings, p))
                continue;
            if (best is null || best.Oceanic && !a.Oceanic || best.Oceanic == a.Oceanic && a.BoxSize < best.BoxSize)
                best = a;
        }
        return best?.Fir;
    }

    /// <summary>
    /// Todas las áreas en ese punto, de la más específica a la más general (TMA o sector antes que la FIR, continental antes
    /// que oceánica): el Centro prueba en ese orden hasta encontrar una frecuencia conocida. Reutiliza la lista.
    /// </summary>
    public void AllAt(GeoPoint p, List<Fir> into)
    {
        into.Clear();
        _found.Clear();
        for (var i = 0; i < _areas.Count; i++)
        {
            var a = _areas[i];
            if (p.LatDeg >= a.MinLat && p.LatDeg <= a.MaxLat && p.LonDeg >= a.MinLon && p.LonDeg <= a.MaxLon && Contains(a.Rings, p))
                _found.Add(a);
        }
        _found.Sort(static (x, y) => x.Oceanic != y.Oceanic ? (x.Oceanic ? 1 : -1) : x.BoxSize.CompareTo(y.BoxSize));
        foreach (var a in _found)
        {
            if (!into.Contains(a.Fir))
                into.Add(a.Fir);
        }
    }

    private static bool Contains(double[][] rings, GeoPoint p)
    {
        foreach (var r in rings)
        {
            var inside = false;
            for (int i = 0, j = r.Length - 2; i < r.Length; j = i, i += 2)
            {
                double xi = r[i], yi = r[i + 1], xj = r[j], yj = r[j + 1];
                if (yi > p.LatDeg != yj > p.LatDeg && p.LonDeg < (xj - xi) * (p.LatDeg - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            if (inside)
                return true;
        }
        return false;
    }

    internal static int ParseKhz(string? mhz) =>
        double.TryParse(mhz, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? (int)Math.Round(f * 1000) : 0;
}
