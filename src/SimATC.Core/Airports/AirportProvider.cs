using SimATC.Core.Simulation;

namespace SimATC.Core.Airports;

/// <summary>Entrega un aeropuerto desde la caché (RF-09) o, si no está o cambió el simulador, desde el simulador.</summary>
public sealed class AirportProvider
{
    private readonly ISimBridge _bridge;
    private readonly AirportCache _cache;

    public AirportProvider(ISimBridge bridge, AirportCache cache)
    {
        _bridge = bridge;
        _cache = cache;
    }

    /// <param name="forceRefresh">Ignora la caché y vuelve a pedir el aeropuerto al simulador.</param>
    public async Task<(AirportData Airport, bool FromCache)> GetAsync(string icao, bool forceRefresh, CancellationToken ct)
    {
        var simVersion = _bridge.SimVersion
            ?? throw new InvalidOperationException("Sin versión del simulador: el puente no está conectado.");

        if (!forceRefresh && _cache.TryLoad(icao, simVersion) is { } cached)
            return (cached, true);

        var airport = await _bridge.LoadAirportAsync(icao, ct).ConfigureAwait(false);
        _cache.Save(airport, simVersion);
        return (airport, false);
    }
}
