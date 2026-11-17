using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Sim;

/// <summary>
/// Prueba de ADR-030: pide al simulador los aeropuertos cercanos y muestra en cuál está el piloto.
/// Uso: <c>dotnet run --project tools/SimATC.Probe -- --nearby</c>, con MSFS abierto.
/// </summary>
internal static class NearbyCommand
{
    public static async Task<int> RunAsync()
    {
        var inv = CultureInfo.InvariantCulture;
        using var bridge = new SimConnectBridge(_ => { });
        UserAircraftState? user = null;
        bridge.UserStateUpdated += s => user = s;
        bridge.Start();
        for (var i = 0; i < 40 && (!bridge.IsConnected || user is null); i++)
            await Task.Delay(250);
        if (!bridge.IsConnected || user is not { } u)
        {
            Console.WriteLine("Sin conexión con MSFS o sin datos del avión en 10 s.");
            return 1;
        }
        var list = await bridge.RequestNearbyAirportsAsync(CancellationToken.None);
        Console.WriteLine($"Avión en {u.Position.LatDeg.ToString("F4", inv)}, {u.Position.LonDeg.ToString("F4", inv)} (en tierra: {u.OnGround}).");
        Console.WriteLine($"Aeropuertos en el caché del simulador: {list.Count}. Los 10 más cercanos:");
        foreach (var a in list.OrderBy(a => GeoMath.DistanceM(a.Position, u.Position)).Take(10))
            Console.WriteLine($"  {a.Icao,-6} a {(GeoMath.DistanceM(a.Position, u.Position) / 1000).ToString("F1", inv)} km");
        var here = AirportLocator.WhereIs(u.Position, u.OnGround, u.AltitudeAglFt, list);
        Console.WriteLine(here is null ? "No estás en ningún aeropuerto (o estás en vuelo)." : $"Estás en {here.Icao}.");
        return 0;
    }
}
