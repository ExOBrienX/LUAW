using System.Text.Json;

namespace SimATC.Core.Fleet;

/// <summary>Un avión de aviación general del propio simulador para decorar plataformas (ADR-109).</summary>
public sealed record GaDecorModel(string Title, string IcaoType, double WingspanM, int Engines, string Kind);

/// <summary>
/// Decoración de aviación general (ADR-109): modelos pasivos de MSFS 2024 listados en <c>data/fleet/ga-decor.json</c>
/// (títulos verificados con la lista oficial del simulador). Solo se usan los que el simulador tiene instalados.
/// </summary>
public static class GaDecor
{
    private sealed record FileModel(string Title, string IcaoType, double WingspanM, int Engines, string Kind);

    private sealed record FileRoot(string? Notes, List<FileModel> Models);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "fleet", "ga-decor.json");

    /// <summary>Los modelos del archivo que el simulador tiene (<paramref name="available"/>; null = todos). Vacío si falta el archivo.</summary>
    public static IReadOnlyList<GaDecorModel> Load(string path, IReadOnlySet<string>? available = null)
    {
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<FileRoot>(File.ReadAllText(path), Json) is not { } root)
                return [];
            return [.. root.Models
                .Where(m => m.WingspanM > 0 && (available is null || available.Contains(m.Title)))
                .Select(m => new GaDecorModel(m.Title, m.IcaoType, m.WingspanM, Math.Max(1, m.Engines), m.Kind))];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>El modelo como los de tráfico (sin aerolínea, estela ligera) para estacionarlo.</summary>
    public static AircraftModel ToModel(GaDecorModel m) =>
        new(m.Title, ModelSource.Sim, m.IcaoType, "", WakeCategory.Light, m.Engines, m.WingspanM, [], []);
}
