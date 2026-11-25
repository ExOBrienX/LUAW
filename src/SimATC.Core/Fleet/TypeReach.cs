using SimATC.Core.Engine;

namespace SimATC.Core.Fleet;

/// <summary>
/// Qué distancia vuela cada clase de avión (ADR-166): un A321 no cruza a Doha y un CRJ7 no va de San Francisco a Miami.
/// Los valores son APROXIMADOS (órdenes de magnitud por clase, con margen), no el alcance publicado de cada tipo.
/// </summary>
public static class TypeReach
{
    public const double LightNm = 500;
    public const double TurbopropNm = 900;
    public const double RegionalNm = 1900;
    public const double NarrowNm = 3300;

    /// <summary>Más corto que esto, un fuselaje ancho solo si la aerolínea casi no tiene otra cosa (NM).</summary>
    public const double WideShortNm = 1000;

    private static readonly string[] RegionalJets = ["CRJ", "E1", "E7", "E29", "RJ", "B46", "F70", "F100", "SU95", "J328"];

    /// <summary>El tramo más largo (NM) que vuela un modelo.</summary>
    public static double MaxNm(AircraftModel m)
    {
        if (Wide(m))
            return double.MaxValue;
        if (m.Wake == WakeCategory.Light)
            return LightNm;
        if (AircraftPerformance.For(m).Category == PerformanceCategory.Turboprop)
            return TurbopropNm;
        foreach (var r in RegionalJets)
        {
            if (m.IcaoType.StartsWith(r, StringComparison.Ordinal))
                return RegionalNm;
        }
        return NarrowNm;
    }

    public static bool Wide(AircraftModel m) => m.Wake is WakeCategory.Heavy or WakeCategory.Super;
}
