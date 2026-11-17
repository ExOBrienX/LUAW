namespace SimATC.Core.Units;

/// <summary>Conversiones en los bordes (SimConnect, UI, voz). Internamente: metros, pies, nudos, grados, segundos.</summary>
public static class UnitConversions
{
    /// <summary>Pie internacional: 0,3048 m exactos.</summary>
    public const double MetersPerFoot = 0.3048;

    public const double MetersPerNauticalMile = 1852.0;

    public static double MetersToFeet(double meters) => meters / MetersPerFoot;

    public static double FeetToMeters(double feet) => feet * MetersPerFoot;
}
