using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimATC.Core.Traffic;

/// <summary>Fuente pública citada de una lista de rutas (RF-11, ADR-015).</summary>
public sealed record RouteSource(string Title, string Url, long Revision, string Retrieved);

/// <summary>
/// Una aerolínea que opera en el aeropuerto, con sus destinos tal como aparecen en la fuente.
/// <see cref="Types"/> vacío = se usan los tipos de las liveries instaladas de la aerolínea (aproximado).
/// </summary>
public sealed record RouteAirline(
    string Name,
    string Icao,
    bool IcaoVerified,
    bool Cargo,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Destinations,
    IReadOnlyList<string> Seasonal,
    IReadOnlyList<string>? Future = null);

/// <summary>Contenido de <c>data/routes/{ICAO}.json</c> (formato documentado en REQUIREMENTS §9).</summary>
public sealed record RouteList(
    string Airport,
    RouteSource Source,
    bool Reviewed,
    string? Notes,
    IReadOnlyList<RouteAirline> Airlines)
{
    public static RouteList? Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    public static RouteList? Parse(string json) =>
        JsonSerializer.Deserialize(json, RouteJsonContext.Default.RouteList);

    /// <summary>Ruta del archivo de un aeropuerto dentro de la carpeta <c>data</c>.</summary>
    public static string PathFor(string dataDir, string icao) =>
        Path.Combine(dataDir, "routes", $"{icao.ToUpperInvariant()}.json");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RouteList))]
internal sealed partial class RouteJsonContext : JsonSerializerContext;
