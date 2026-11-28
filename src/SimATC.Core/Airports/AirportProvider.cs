using System.Collections.Concurrent;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Text;

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
        // Al reconectar el simulador pudo abrirse con otros escenarios.
        _bridge.ConnectionChanged += connected =>
        {
            if (!connected)
                _read.Clear();
        };
    }

    // Aeropuertos ya leídos del simulador en esta conexión.
    private readonly ConcurrentDictionary<string, byte> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lo que se espera (s) al simulador antes de usar lo guardado.</summary>
    public double VerifyTimeoutS { get; init; } = 25;

    /// <summary>Avisos para el registro.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// El aeropuerto que se va a usar en tierra: una vez por conexión se lee del simulador aunque esté guardado (ADR-169). Un
    /// escenario instalado o quitado cambia puestos y calles y la caché no se entera: no depende de la versión del simulador.
    /// Si el simulador no responde, se usa lo guardado.
    /// </summary>
    public async Task<(AirportData Airport, bool FromCache)> GetFreshAsync(string icao, CancellationToken ct)
    {
        var saved = _bridge.SimVersion is { } version ? _cache.TryLoad(icao, version) : null;
        if (saved is null || _read.ContainsKey(icao))
            return await GetAsync(icao, false, ct).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(VerifyTimeoutS));
        try
        {
            var fresh = await GetAsync(icao, true, limit.Token).ConfigureAwait(false);
            if (Differences(saved, fresh.Airport) is { } what)
                Log?.Invoke(Tr.F("{0}: el simulador trae otro escenario que el guardado ({1}); se usa el del simulador.", fresh.Airport.Icao, what));
            return fresh;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not AirportNotFoundException)
        {
            Log?.Invoke(Tr.F("{0}: no se pudo leer del simulador ({1}); se usa lo guardado.", icao, ex.Message));
            return (saved, true);
        }
    }

    /// <summary>Qué cambió entre lo guardado y lo leído (para el registro), o null si es lo mismo.</summary>
    internal static string? Differences(AirportData a, AirportData b)
    {
        var moved = a.Parkings.Count != b.Parkings.Count;
        for (var i = 0; i < a.Parkings.Count && !moved; i++)
        {
            var (p, q) = (a.Parkings[i], b.Parkings[i]);
            moved = p.Type != q.Type || p.Number != q.Number || Math.Abs(p.RadiusM - q.RadiusM) > 0.5 || GeoMath.DistanceM(p.Position, q.Position) > 1;
        }
        if (!moved && a.TaxiPoints.Count == b.TaxiPoints.Count && a.TaxiPaths.Count == b.TaxiPaths.Count && a.Runways.Count == b.Runways.Count)
            return null;
        return Tr.F("puestos {0} → {1}, puntos de rodaje {2} → {3}, tramos {4} → {5}, pistas {6} → {7}", a.Parkings.Count, b.Parkings.Count,
            a.TaxiPoints.Count, b.TaxiPoints.Count, a.TaxiPaths.Count, b.TaxiPaths.Count, a.Runways.Count, b.Runways.Count);
    }

    /// <param name="forceRefresh">Ignora la caché y vuelve a pedir el aeropuerto al simulador.</param>
    public async Task<(AirportData Airport, bool FromCache)> GetAsync(string icao, bool forceRefresh, CancellationToken ct)
    {
        var simVersion = _bridge.SimVersion
            ?? throw new InvalidOperationException(Tr.T("Sin versión del simulador: el puente no está conectado."));

        if (!forceRefresh && _cache.TryLoad(icao, simVersion) is { } cached)
            return (cached, true);

        var airport = await _bridge.LoadAirportAsync(icao, ct).ConfigureAwait(false);
        _cache.Save(airport, simVersion);
        _read[icao] = 0;
        return (airport, false);
    }
}
