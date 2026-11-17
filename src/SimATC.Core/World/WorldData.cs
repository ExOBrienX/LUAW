using System.IO.Compression;
using System.Text.RegularExpressions;
using SimATC.Core.Geo;
using SimATC.Core.Units;

namespace SimATC.Core.World;

/// <summary>Aeropuerto de la base mundial (standing-data, CC0): solo los que aparecen en alguna ruta de aerolínea.</summary>
public readonly record struct WorldAirport(string Icao, GeoPoint Position, double ElevationFt);

/// <summary>Un tramo de un vuelo real por indicativo (RF-12d capa 1): LAN205 SCEL→SCIE. Sin hora (se genera).</summary>
public sealed record WorldLeg(string Callsign, string Airline, string Origin, string Destination);

/// <summary>Indicativos ADS-B y de la base en una misma forma: OACI de 3 letras + número sin ceros a la izquierda.</summary>
public static partial class Callsigns
{
    [GeneratedRegex("^([A-Z]{3})0*([0-9]{1,4}[A-Z]{0,2})$")]
    private static partial Regex AirlineCallsign();

    /// <summary>"LAN002 " → "LAN2"; null si no es un indicativo de aerolínea (matrículas, militares…).</summary>
    public static string? Normalize(string? raw)
    {
        if (raw is null)
            return null;
        var m = AirlineCallsign().Match(raw.Trim().ToUpperInvariant());
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : null;
    }
}

/// <summary>Aeropuertos de <c>data/world/airports.csv</c> (~3.800, se cargan enteros: son pocos).</summary>
public sealed class WorldAirports
{
    private readonly Dictionary<string, WorldAirport> _byIcao;

    private WorldAirports(Dictionary<string, WorldAirport> byIcao) => _byIcao = byIcao;

    public int Count => _byIcao.Count;

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "world", "airports.csv");

    public static WorldAirports Load(string path) => Parse(File.ReadLines(path));

    public static WorldAirports Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, WorldAirport>(StringComparer.Ordinal);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 4)
                continue;
            map[f[0]] = new WorldAirport(f[0], new GeoPoint(double.Parse(f[1], inv), double.Parse(f[2], inv)), double.Parse(f[3], inv));
        }
        return new WorldAirports(map);
    }

    public WorldAirport? Find(string icao) => _byIcao.TryGetValue(icao, out var a) ? a : null;

    /// <summary>Aeropuertos a menos de <paramref name="radiusM"/> de un punto.</summary>
    public List<WorldAirport> Within(GeoPoint center, double radiusM)
    {
        var list = new List<WorldAirport>();
        foreach (var a in _byIcao.Values)
        {
            if (GeoMath.DistanceM(center, a.Position) <= radiusM)
                list.Add(a);
        }
        return list;
    }

    /// <summary>
    /// Destino probable de un avión sin ruta conocida (semilla en vivo): el aeropuerto más cercano en la dirección de su
    /// rumbo (±25°), entre 60 y 1500 NM. null si no hay ninguno.
    /// </summary>
    public WorldAirport? AheadOf(GeoPoint position, double trackDeg)
    {
        WorldAirport? best = null;
        var bestScore = double.MaxValue;
        foreach (var a in _byIcao.Values)
        {
            var d = GeoMath.DistanceM(position, a.Position) / UnitConversions.MetersPerNauticalMile;
            if (d is < 60 or > 1500)
                continue;
            var off = Math.Abs(GeoMath.AngleDiffDeg(trackDeg, GeoMath.InitialBearingDeg(position, a.Position)));
            if (off > 25)
                continue;
            // Más cerca y mejor alineado; la distancia pesa más que el ángulo.
            var score = d * (1 + off / 25);
            if (score < bestScore)
            {
                bestScore = score;
                best = a;
            }
        }
        return best;
    }
}

/// <summary>
/// Rutas reales por indicativo de una zona (RF-12d): de <c>data/world/routes.csv.gz</c> (626 mil rutas) se guardan solo los
/// tramos que tocan un aeropuerto dentro de la zona. Nunca se carga el mundo entero (§6.4).
/// </summary>
public sealed class WorldRoutes
{
    private readonly Dictionary<string, List<WorldLeg>> _byOrigin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorldLeg>> _byDestination = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorldLeg>> _byCallsign = new(StringComparer.Ordinal);

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "world", "routes.csv.gz");

    /// <summary>Centro de la zona cargada y su radio.</summary>
    public GeoPoint Center { get; private init; }
    public double RadiusM { get; private init; }
    public int LegCount { get; private set; }

    /// <summary>Lee el archivo en flujo y guarda los tramos con origen o destino en <paramref name="zoneAirports"/>.</summary>
    public static WorldRoutes LoadZone(string path, IReadOnlySet<string> zoneAirports, GeoPoint center, double radiusM)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return ParseZone(reader, zoneAirports, center, radiusM);
    }

    public static WorldRoutes ParseZone(TextReader reader, IReadOnlySet<string> zoneAirports, GeoPoint center, double radiusM)
    {
        var routes = new WorldRoutes { Center = center, RadiusM = radiusM };
        reader.ReadLine(); // encabezado
        while (reader.ReadLine() is { } line)
        {
            var comma = line.IndexOf(',');
            if (comma <= 0)
                continue;
            var codes = line.AsSpan(comma + 1);
            // Filtro rápido sin asignar: ¿alguno de sus aeropuertos está en la zona?
            var touches = false;
            for (var i = 0; i + 4 <= codes.Length; i += 5)
            {
                if (zoneAirports.Contains(codes.Slice(i, 4).ToString()))
                {
                    touches = true;
                    break;
                }
            }
            if (!touches)
                continue;
            var callsign = line[..comma];
            var list = line[(comma + 1)..].Split('-');
            for (var i = 0; i + 1 < list.Length; i++)
            {
                if (zoneAirports.Contains(list[i]) || zoneAirports.Contains(list[i + 1]))
                    routes.Add(new WorldLeg(callsign, callsign[..3], list[i], list[i + 1]));
            }
        }
        return routes;
    }

    private void Add(WorldLeg leg)
    {
        Append(_byOrigin, leg.Origin, leg);
        Append(_byDestination, leg.Destination, leg);
        Append(_byCallsign, leg.Callsign, leg);
        LegCount++;
    }

    private static void Append(Dictionary<string, List<WorldLeg>> map, string key, WorldLeg leg)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(leg);
    }

    private static readonly List<WorldLeg> None = [];

    public IReadOnlyList<WorldLeg> DeparturesFrom(string icao) => _byOrigin.GetValueOrDefault(icao, None);

    public IReadOnlyList<WorldLeg> ArrivalsTo(string icao) => _byDestination.GetValueOrDefault(icao, None);

    public IReadOnlyList<WorldLeg> ForCallsign(string callsign) => _byCallsign.GetValueOrDefault(callsign, None);

    /// <summary>Todos los tramos de la zona (para el tráfico de paso y de otros aeropuertos).</summary>
    public IEnumerable<WorldLeg> All => _byOrigin.Values.SelectMany(l => l);
}
