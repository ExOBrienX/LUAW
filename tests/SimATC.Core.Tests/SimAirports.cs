using SimATC.Core.Airports;

namespace SimATC.Core.Tests;

/// <summary>
/// Aeropuertos guardados desde el simulador para los tests (<c>Data/*.json</c>: pistas, calles, puestos y procedimientos).
/// Son datos del simulador: quedan en el repositorio de desarrollo y no se publican (ADR-153). Sin el archivo, el test se
/// salta en vez de fallar.
/// </summary>
public static class SimAirports
{
    public static AirportData Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", file);
        if (!File.Exists(path))
            Assert.Skip($"Falta el aeropuerto de prueba {file} (datos del simulador, no se publican).");
        return AirportCache.Deserialize(File.ReadAllText(path))!.Airport;
    }
}
