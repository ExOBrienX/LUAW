using SimATC.Core.Fleet;

namespace SimATC.Core.Engine;

/// <summary>Categoría de rendimiento simplificada (RF-19).</summary>
public enum PerformanceCategory
{
    Turboprop,
    MediumJet,
    HeavyJet,
}

/// <summary>
/// Rendimiento simplificado por categoría (RF-19). TODOS los valores son APROXIMADOS: órdenes de magnitud típicos
/// de operación de línea, sin fuente única; se afinan comparando con lo que se ve en el simulador.
/// </summary>
public sealed record AircraftPerformance(
    PerformanceCategory Category,
    double TaxiSpeedKt,
    GroundLimits Ground,
    double PushbackSpeedKt,
    double PushbackDistanceM,
    double TakeoffAccelKtPerS,
    double RotateSpeedKt,
    double LiftoffPitchDeg,
    double ClimbPitchDeg,
    double InitialClimbFpm,
    double ClimbSpeedKt,
    double ClimbAccelKtPerS,
    double ReferenceAglFt,
    // Flaps de despegue (% de recorrido de la superficie): A320 CONF 1+F ≈ 10° de 40°, 777 ≈ 15° de 30°, ATR 15° de 33°.
    double TakeoffFlapsPercent = 25)
{
    // Aproximado: 0,06 g de aceleración lateral en curva (≈10 kt en una curva de 45 m), giro de 12°/s como máximo, frenado suave.
    private static readonly GroundLimits JetGround = new(
        AccelKtPerS: 0.8, DecelKtPerS: 1.5, LateralAccelMs2: 0.6, MaxYawRateDegPerS: 12, CornerRadiusM: 45);

    private static readonly GroundLimits HeavyGround = JetGround with { AccelKtPerS = 0.6, LateralAccelMs2 = 0.5, MaxYawRateDegPerS = 9, CornerRadiusM = 55 };

    public static AircraftPerformance Turboprop { get; } = new(PerformanceCategory.Turboprop, 18, JetGround with { CornerRadiusM = 35 },
        PushbackSpeedKt: 3, PushbackDistanceM: 35, TakeoffAccelKtPerS: 4.0, RotateSpeedKt: 110, LiftoffPitchDeg: 6, ClimbPitchDeg: 9,
        InitialClimbFpm: 1800, ClimbSpeedKt: 170, ClimbAccelKtPerS: 1.0, ReferenceAglFt: 8,
        TakeoffFlapsPercent: 45);

    public static AircraftPerformance MediumJet { get; } = new(PerformanceCategory.MediumJet, 20, JetGround,
        PushbackSpeedKt: 3, PushbackDistanceM: 45, TakeoffAccelKtPerS: 4.0, RotateSpeedKt: 145, LiftoffPitchDeg: 8, ClimbPitchDeg: 15,
        InitialClimbFpm: 2500, ClimbSpeedKt: 250, ClimbAccelKtPerS: 1.5, ReferenceAglFt: 11);

    public static AircraftPerformance HeavyJet { get; } = new(PerformanceCategory.HeavyJet, 18, HeavyGround,
        PushbackSpeedKt: 2.5, PushbackDistanceM: 60, TakeoffAccelKtPerS: 3.0, RotateSpeedKt: 160, LiftoffPitchDeg: 7, ClimbPitchDeg: 12,
        InitialClimbFpm: 2000, ClimbSpeedKt: 250, ClimbAccelKtPerS: 1.2, ReferenceAglFt: 18,
        TakeoffFlapsPercent: 30);

    /// <summary>Elige la categoría por estela y tipo: H/J = pesado; ATR/Dash = turbohélice; el resto, jet mediano.</summary>
    public static AircraftPerformance For(AircraftModel model)
    {
        if (model.Wake is WakeCategory.Heavy or WakeCategory.Super)
            return HeavyJet;
        return model.IcaoType.StartsWith("AT", StringComparison.Ordinal) || model.IcaoType.StartsWith("DH8", StringComparison.Ordinal)
            ? Turboprop
            : MediumJet;
    }
}
