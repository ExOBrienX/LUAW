using SimATC.Core.Geo;

namespace SimATC.Core.Simulation;

/// <summary>
/// Pose de un avión inyectado para un cuadro (ADR-017). En tierra el simulador lo apoya en el terreno y la altura
/// solo cuenta en el modo alternativo; <see cref="AirspeedKt"/> hace girar las ruedas.
/// </summary>
public readonly record struct AircraftPose(
    GeoPoint Position,
    double AltitudeFt,
    double HeadingTrueDeg,
    // Grados, positivo = nariz arriba (el puente convierte al signo de SimConnect).
    double PitchDeg,
    double BankDeg,
    bool OnGround,
    double AirspeedKt,
    // Para las animaciones de los modelos (V-09, probado en FSLTL y AIG): velocidad longitudinal con signo (negativa
    // marcha atrás: las ruedas ruedan hacia atrás), tren (1 abajo, 0 arriba), flaps (%) y velocidad vertical.
    double LongitudinalSpeedKt = 0,
    double GearDown = 1,
    double FlapsPercent = 0,
    double VerticalSpeedFpm = 0);

/// <summary>Luces exteriores de un avión IA (V-09: se manejan por eventos).</summary>
[Flags]
public enum LightFlags
{
    None = 0,
    Nav = 1,
    Beacon = 2,
    Strobe = 4,
    Landing = 8,
    Taxi = 16,
    Logo = 32,
    Wing = 64,
}

/// <summary>Un cuadro del simulador: tiempo real transcurrido, velocidad de simulación y pausa (V-06).</summary>
public readonly record struct FrameInfo(double RealSeconds, double SimRate, bool Paused);
