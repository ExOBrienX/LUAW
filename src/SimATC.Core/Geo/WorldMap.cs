using System.Text.Json;

using SimATC.Core.Text;

namespace SimATC.Core.Geo;

/// <summary>
/// Mapa base del radar: costas y fronteras de Natural Earth 1:50m (dominio público), convertidas a
/// <c>data/map/world-50m.json</c> con 4 decimales (~11 m). Cada línea es una lista plana lon,lat,lon,lat…
/// No es un dato aeronáutico: solo da contexto geográfico a la ruta del plan.
/// </summary>
public sealed class WorldMap
{
    private sealed record FileFormat(string Source, double[][] Coast, double[][] Borders);

    public IReadOnlyList<double[]> Coast { get; }
    public IReadOnlyList<double[]> Borders { get; }

    private WorldMap(IReadOnlyList<double[]> coast, IReadOnlyList<double[]> borders)
    {
        Coast = coast;
        Borders = borders;
    }

    public static string DefaultPath { get; } = Path.Combine(AppContext.BaseDirectory, "data", "map", "world-50m.json");

    /// <summary>null si el archivo no está o no se puede leer (el radar funciona igual, sin mapa).</summary>
    public static WorldMap? Load(string path, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var f = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return f is null ? null : new WorldMap(f.Coast, f.Borders);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke(Tr.F("Mapa base ilegible ({0}); el radar sigue sin él.", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Tramos de las líneas que caen dentro de <paramref name="radiusM"/> del centro, como listas de puntos (una línea
    /// que sale y vuelve a entrar queda partida). Así se proyecta solo lo cercano: la proyección local del radar es
    /// buena a escala regional, no mundial.
    /// </summary>
    public static List<List<GeoPoint>> Near(IReadOnlyList<double[]> lines, GeoPoint center, double radiusM)
    {
        var result = new List<List<GeoPoint>>();
        // Filtro grueso por caja en grados antes de medir distancias.
        var dLat = radiusM / 111_000.0;
        var dLon = dLat / Math.Max(0.1, Math.Cos(center.LatDeg * Math.PI / 180));
        foreach (var line in lines)
        {
            List<GeoPoint>? current = null;
            for (var i = 0; i + 1 < line.Length; i += 2)
            {
                var p = new GeoPoint(line[i + 1], line[i]);
                var inside = Math.Abs(p.LatDeg - center.LatDeg) <= dLat && Math.Abs(p.LonDeg - center.LonDeg) <= dLon
                    && GeoMath.DistanceM(p, center) <= radiusM;
                if (inside)
                {
                    current ??= [];
                    current.Add(p);
                }
                else if (current is not null)
                {
                    if (current.Count > 1)
                        result.Add(current);
                    current = null;
                }
            }
            if (current is { Count: > 1 })
                result.Add(current);
        }
        return result;
    }
}
