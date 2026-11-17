using SimATC.Core.Geo;

namespace SimATC.Core.Simulation;

/// <summary>
/// Estado del avión del piloto (RF-05), ya en unidades internas. La conversión desde las SimVars
/// ocurre en el adaptador (SimATC.Sim).
/// </summary>
/// <param name="Position">Posición.</param>
/// <param name="AltitudeMslFt">Altitud sobre el nivel del mar, en pies.</param>
/// <param name="AltitudeAglFt">Altitud sobre el terreno (incluye obstáculos), en pies.</param>
/// <param name="HeadingTrueDeg">Rumbo verdadero, en grados [0, 360).</param>
/// <param name="IndicatedAirspeedKt">Velocidad indicada, en nudos.</param>
/// <param name="GroundSpeedKt">Velocidad respecto al suelo, en nudos.</param>
/// <param name="VerticalSpeedFpm">Velocidad vertical, en pies por minuto (convención aeronáutica).</param>
/// <param name="OnGround">Si el avión está en tierra.</param>
/// <param name="Com1ActiveKhz">Frecuencia activa de COM1 en kHz (118.100 MHz → 118100).</param>
/// <param name="PressureAltitudeFt">Altitud de presión, con 1013,25 hPa (PRESSURE ALTITUDE).</param>
/// <param name="QnhAltitudeFt">Altitud indicada con la presión de nivel del mar actual (INDICATED ALTITUDE CALIBRATED).</param>
/// <param name="PushbackAttached">Remolque del simulador enganchado (PUSHBACK ATTACHED, "Services Variables").</param>
/// <param name="ParkingBrakeSet">Freno de estacionamiento puesto (BRAKE PARKING POSITION).</param>
/// <param name="EnginesRunning">Algún motor encendido (GENERAL ENG COMBUSTION:1 o :2); null si no se sabe.</param>
/// <param name="TransponderCode">Código del transpondedor como 4 dígitos (4721), o -1 si el valor no es válido.</param>
public readonly record struct UserAircraftState(
    GeoPoint Position,
    double AltitudeMslFt,
    double AltitudeAglFt,
    double HeadingTrueDeg,
    double IndicatedAirspeedKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    bool OnGround,
    int Com1ActiveKhz,
    int TransponderCode,
    bool ParkingBrakeSet = false,
    bool PushbackAttached = false,
    double? PressureAltitudeFt = null,
    double? QnhAltitudeFt = null,
    bool? EnginesRunning = null)
{
    /// <summary>
    /// La altitud como la lee el piloto en el PFD: sobre la altitud de transición, nivel de vuelo con 1013,25 hPa
    /// (<c>PRESSURE ALTITUDE</c>): "FL270"; por debajo, altitud con QNH (<c>INDICATED ALTITUDE CALIBRATED</c>):
    /// "5000 ft". Sin esos datos, la altitud real sobre el nivel del mar.
    /// </summary>
    public string AltitudeText(double transitionAltitudeFt)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (PressureAltitudeFt is { } p && QnhAltitudeFt is { } q)
            return q > transitionAltitudeFt
                ? $"FL{Math.Round(p / 100).ToString("000", inv)}"
                : $"{Math.Round(q).ToString("0", inv)} ft";
        return $"{Math.Round(AltitudeMslFt).ToString("0", inv)} ft";
    }
}
