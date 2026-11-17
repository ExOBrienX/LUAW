using System.Text.Json;
using System.Text.Json.Serialization;
using SimATC.Core.Atc;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.World;

/// <summary>Un ACC de fuente oficial (AIP): indicativo y frecuencia, o dos sectores divididos por una línea.</summary>
public sealed record CenterEntry
{
    public required string Fir { get; init; }
    public required string Callsign { get; init; }
    public string? Remark { get; init; }
    public int? Khz { get; init; }
    /// <summary>Línea que divide los sectores: puntos [lat, lon] de oeste a este, interpolada por longitud.</summary>
    public double[][]? Split { get; init; }
    public int? NorthKhz { get; init; }
    public int? SouthKhz { get; init; }
}

public sealed record CenterFile
{
    public IReadOnlyList<AtcSource> Sources { get; init; } = [];
    public bool Reviewed { get; init; }
    public IReadOnlyList<CenterEntry> Firs { get; init; } = [];
}

/// <summary>
/// Centros de fuentes oficiales (<c>data/centers.json</c>, ADR-072): se usan antes que las frecuencias vistas en VATSIM.
/// El indicativo es el del AIP ("Santiago Radar", igual en inglés y español: idioma EN, ES).
/// </summary>
public sealed class CenterData
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, CenterEntry> _byFir;

    private CenterData(IEnumerable<CenterEntry> entries) =>
        _byFir = entries.ToDictionary(e => e.Fir.ToUpperInvariant(), StringComparer.Ordinal);

    public int Count => _byFir.Count;

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "centers.json");

    public static CenterData Empty { get; } = new([]);

    /// <summary>Sin archivo o ilegible: vacío (se avisa por el log) y se usa VATSIM.</summary>
    public static CenterData Load(string path, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path))
                return Empty;
            var file = JsonSerializer.Deserialize<CenterFile>(File.ReadAllText(path), Json);
            return file is null ? Empty : new CenterData(file.Firs);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            log?.Invoke(Tr.F("Centros ilegibles en {0} ({1}); se usan los vistos en VATSIM.", path, ex.Message));
            return Empty;
        }
    }

    /// <summary>El Centro oficial de esa FIR en esa posición (con su sector), o null si no hay dato oficial.</summary>
    public AtcStation? StationFor(string firIcao, GeoPoint p)
    {
        if (!_byFir.TryGetValue(firIcao.ToUpperInvariant(), out var e))
            return null;
        int? khz = e.Khz;
        if (e.Split is { Length: >= 2 } line && e.NorthKhz is { } north && e.SouthKhz is { } south)
            khz = p.LatDeg >= SplitLatAt(line, p.LonDeg) ? north : south;
        return khz is { } k ? new AtcStation(AtcRole.Center, e.Callsign, k) : null;
    }

    /// <summary>Latitud de la línea divisoria en esa longitud (fuera de sus extremos, la del extremo más cercano).</summary>
    public static double SplitLatAt(double[][] line, double lonDeg)
    {
        if (lonDeg <= line[0][1])
            return line[0][0];
        for (var i = 1; i < line.Length; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            if (lonDeg <= b[1])
            {
                var t = b[1] == a[1] ? 0 : (lonDeg - a[1]) / (b[1] - a[1]);
                return a[0] + t * (b[0] - a[0]);
            }
        }
        return line[^1][0];
    }
}
