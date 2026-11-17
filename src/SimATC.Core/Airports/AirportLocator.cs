using SimATC.Core.Geo;
using SimATC.Core.Simulation;

namespace SimATC.Core.Airports;

/// <summary>
/// En qué aeropuerto está el piloto (ADR-030): el más cercano de la lista del simulador cuyo punto de referencia esté a
/// menos de <see cref="MaxDistanceM"/>, estando en tierra o casi (bajo <see cref="MaxAglFt"/>). En vuelo no hay
/// aeropuerto: la app se queda con el último. Los aeropuertos grandes tienen pistas de más de 4 km, así que el radio
/// cubre todo el aeródromo sin confundirse con el vecino.
/// </summary>
public static class AirportLocator
{
    public const double MaxDistanceM = 6000;
    public const double MaxAglFt = 200;

    /// <summary>
    /// Aeropuerto para trabajar cuando el piloto no está en ninguno (abrió LUAW en vuelo, en cualquier parte del mundo):
    /// con plan, el más cercano entre su origen y su destino, a cualquier distancia (prueba de Marco 2026-11-09: a mitad de
    /// RJTT → RJBB, a más de 150 NM de ambos, se cargó RJU4F, un aeródromo chico al lado, y el radar quedó con un zoom
    /// enorme); sin plan, el más cercano de la lista del simulador a menos de <paramref name="maxNm"/>. Nunca el "último
    /// usado" lejos de él (antes: abría en SCEL estando en KMIA).
    /// </summary>
    public static string? Fallback(GeoPoint pilot, IReadOnlyList<(string Icao, GeoPoint Position)> planned,
        IReadOnlyList<NearbyAirport> nearby, double maxNm = 150)
    {
        string? best = null;
        var bestD = double.MaxValue;
        foreach (var (icao, p) in planned)
        {
            var d = GeoMath.DistanceM(pilot, p);
            if (d < bestD)
                (best, bestD) = (icao, d);
        }
        if (best is not null)
            return best;
        bestD = maxNm * 1852;
        foreach (var a in nearby)
        {
            var d = GeoMath.DistanceM(pilot, a.Position);
            if (d < bestD)
                (best, bestD) = (a.Icao, d);
        }
        return best;
    }

    /// <summary>Más cerca que esto (m) de un puesto, punto de rodaje o pista del aeropuerto cargado: sigues en él.</summary>
    public const double InsideM = 600;

    /// <summary>
    /// El piloto sigue dentro del aeropuerto cargado (cerca de sus puestos, rodajes o pistas). En aeropuertos enormes el
    /// punto de referencia de un vecino chico puede quedar más cerca que el propio (prueba de Marco 2026-11-09: rodando en
    /// RJTT pasó a RJ3KM, un parque sin pistas, y se borró el tráfico); dentro del cargado no se cambia.
    /// </summary>
    public static bool Inside(GeoPoint pilot, AirportData airport)
    {
        foreach (var p in airport.Parkings)
            if (GeoMath.DistanceM(pilot, p.Position) < InsideM)
                return true;
        foreach (var t in airport.TaxiPoints)
            if (GeoMath.DistanceM(pilot, t.Position) < InsideM)
                return true;
        foreach (var r in airport.Runways)
            if (GeoMath.DistanceM(pilot, r.Center) < InsideM + r.LengthM / 2)
                return true;
        return false;
    }

    public static NearbyAirport? WhereIs(GeoPoint pilot, bool onGround, double aglFt, IReadOnlyList<NearbyAirport> nearby)
    {
        if (!onGround && aglFt > MaxAglFt)
            return null;
        NearbyAirport? best = null;
        var bestD = MaxDistanceM;
        foreach (var a in nearby)
        {
            var d = GeoMath.DistanceM(pilot, a.Position);
            if (d <= bestD)
            {
                best = a;
                bestD = d;
            }
        }
        return best;
    }
}
