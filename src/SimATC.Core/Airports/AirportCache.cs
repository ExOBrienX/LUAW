using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimATC.Core.Airports;

/// <summary>Contenido de un archivo de caché de aeropuerto.</summary>
public sealed record AirportCacheFile(int SchemaVersion, string SimVersion, DateTime CreatedUtc, AirportData Airport);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AirportCacheFile))]
internal sealed partial class AirportJsonContext : JsonSerializerContext;

/// <summary>
/// Caché local de aeropuertos en JSON (RF-09): un archivo por ICAO. Se invalida si cambia la versión del
/// simulador o el esquema, o a mano con <see cref="Invalidate"/>.
/// </summary>
public sealed class AirportCache
{
    /// <summary>Subir cuando cambie la forma o el significado de <see cref="AirportData"/>.</summary>
    /// <remarks>2: sufijo de aproximación vacío en vez de "0".</remarks>
    // 3 (ADR-137): las cabeceras traen si se puede despegar, aterrizar o están cerradas; los aeropuertos se vuelven a leer.
    public const int SchemaVersion = 3;

    private readonly string _directory;

    public AirportCache(string directory)
    {
        _directory = directory;
    }

    public string PathFor(string icao) => Path.Combine(_directory, $"{icao.ToUpperInvariant()}.json");

    /// <summary>Devuelve el aeropuerto si está en caché para esta versión del simulador; si no, null.</summary>
    public AirportData? TryLoad(string icao, string simVersion)
    {
        var path = PathFor(icao);
        if (!File.Exists(path))
            return null;

        try
        {
            var file = Deserialize(File.ReadAllText(path));
            return file is not null && file.SchemaVersion == SchemaVersion && file.SimVersion == simVersion
                ? file.Airport
                : null;
        }
        catch (JsonException)
        {
            return null; // archivo dañado: se vuelve a pedir al simulador
        }
    }

    public void Save(AirportData airport, string simVersion)
    {
        Directory.CreateDirectory(_directory);
        var json = Serialize(new AirportCacheFile(SchemaVersion, simVersion, DateTime.UtcNow, airport));
        var path = PathFor(airport.Icao);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void Invalidate(string icao)
    {
        var path = PathFor(icao);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string Serialize(AirportCacheFile file) =>
        JsonSerializer.Serialize(file, AirportJsonContext.Default.AirportCacheFile);

    public static AirportCacheFile? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AirportJsonContext.Default.AirportCacheFile);
}
