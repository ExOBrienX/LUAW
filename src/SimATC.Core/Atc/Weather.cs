using SimATC.Core.Geo;

namespace SimATC.Core.Atc;

/// <summary>Precipitación, tal como la da <c>AMBIENT PRECIP STATE</c> (máscara del SDK: 2 nada, 4 lluvia, 8 nieve).</summary>
public enum Precipitation
{
    None,
    Rain,
    Snow,
}

/// <summary>
/// Clima observado en un aeropuerto (RF-45). Con MSFS 2024 solo se puede leer en la posición del avión del piloto
/// (SimVars ambientales; el METAR por estación ya no responde: V-18, ADR-027), así que vale para el aeropuerto donde
/// está el piloto. Nubes y punto de rocío no se conocen: no están aquí. Viento con dirección <b>verdadera</b>, medio de
/// 2 minutos, con ráfaga si la hay (<see cref="WindAverager"/>). La visibilidad del simulador es solo "de partículas"
/// (SDK: <c>AMBIENT VISIBILITY</c>): no refleja lluvia ni neblina, así que no se usa en el ATIS; queda para diagnóstico.
/// </summary>
public sealed partial record WeatherObservation(
    string Icao,
    DateTime TimeUtc,
    double WindFromTrueDeg,
    double WindKt,
    double? GustKt,
    double ParticleVisibilityM,
    double TemperatureC,
    double QnhHpa,
    Precipitation Precipitation,
    double MagVarDeg)
{
    /// <summary>Dirección del viento en grados magnéticos. MAGVAR del simulador: positivo = Este (verificado, ADR-027).</summary>
    public double WindFromMagneticDeg => GeoMath.NormalizeDeg(WindFromTrueDeg - MagVarDeg);

    /// <summary>
    /// Convierte la lectura de las SimVars en tu posición en la observación del aeropuerto, si estás en él: en tierra o
    /// a menos de <paramref name="maxAglFt"/> y a menos de <paramref name="maxDistanceM"/> del punto de referencia.
    /// null si estás lejos (el clima de ese lugar no representa al aeropuerto).
    /// </summary>
    public static WeatherObservation? AtAirport(string icao, GeoPoint airportRef, AmbientWeather ambient, AveragedWind wind,
        DateTime timeUtc, double maxDistanceM = 10_000, double maxAglFt = 100)
    {
        if (!IsAtAirport(airportRef, ambient, maxDistanceM, maxAglFt))
            return null;
        return new WeatherObservation(icao, timeUtc, wind.FromTrueDeg, wind.MeanKt, wind.GustKt, ambient.VisibilityM,
            ambient.TemperatureC, ambient.SeaLevelPressureHpa, ambient.Precipitation, ambient.MagVarDeg);
    }

    /// <summary>
    /// El piloto está en el aeropuerto: el clima en su posición representa al aeropuerto. En el aire solo hasta 100 ft: más
    /// arriba el viento del simulador ya no es el de superficie (en final puede diferir mucho del METAR).
    /// </summary>
    public static bool IsAtAirport(GeoPoint airportRef, AmbientWeather ambient, double maxDistanceM = 10_000, double maxAglFt = 100) =>
        GeoMath.DistanceM(ambient.Position, airportRef) <= maxDistanceM && (ambient.OnGround || ambient.AglFt <= maxAglFt);
}

/// <summary>Las SimVars ambientales en la posición del avión del piloto (unidades internas: nudos, metros, hPa, °C).</summary>
public readonly record struct AmbientWeather(
    GeoPoint Position,
    bool OnGround,
    double AglFt,
    double WindFromTrueDeg,
    double WindKt,
    double VisibilityM,
    double TemperatureC,
    double SeaLevelPressureHpa,
    Precipitation Precipitation,
    double MagVarDeg)
{
    /// <summary>Máscara de <c>AMBIENT PRECIP STATE</c> (2 nada, 4 lluvia, 8 nieve) a <see cref="Atc.Precipitation"/>.</summary>
    public static Precipitation FromMask(int mask) => (mask & 8) != 0 ? Precipitation.Snow : (mask & 4) != 0 ? Precipitation.Rain : Precipitation.None;
}
