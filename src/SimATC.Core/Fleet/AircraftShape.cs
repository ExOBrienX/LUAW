namespace SimATC.Core.Fleet;

/// <summary>Silueta con la que el radar dibuja un avión a escala (los bocetos de Marco, ADR-173).</summary>
public enum AircraftShape
{
    Light,
    Turboprop,
    Bizjet,
    Narrow,
    Wide,
    Quad,
}

public static class AircraftShapes
{
    /// <summary>Envergadura (m) desde la que un bimotor es de fuselaje ancho.</summary>
    public const double WideSpanM = 45;

    /// <summary>Envergadura (m) bajo la que un avión es una avioneta (C172 11 m, PA-28 11 m; el ATR 72 tiene 27 m).</summary>
    public const double LightSpanM = 15;

    // Prefijos de designador OACI (Doc 8643) de turbohélices de pasajeros y de jets privados.
    private static readonly string[] TurbopropPrefixes =
        ["AT4", "AT7", "ATR", "DH8", "SF34", "SB20", "JS31", "JS32", "JS41", "D328", "F50", "E120", "B190", "SW4", "L410", "DHC6", "DHC7", "C212", "CN35"];

    private static readonly string[] BizjetPrefixes =
        ["LJ", "GLF", "GLEX", "GL5T", "GL7T", "FA", "F2TH", "F900", "C25", "C5", "C6", "C7", "CL30", "CL35", "CL60", "E50P", "E55P",
         "E545", "E550", "PC24", "H25", "HDJT", "BE40", "PRM1", "G150", "G280", "ASTR", "GALX"];

    /// <summary>La silueta de un tipo: por su designador si es conocido; si no, por motores y envergadura.</summary>
    public static AircraftShape Of(string? icaoType, double wingspanM, int engines)
    {
        if (engines >= 4)
            return AircraftShape.Quad;
        var t = icaoType?.Trim().ToUpperInvariant() ?? "";
        if (t.Length > 0)
        {
            foreach (var p in TurbopropPrefixes)
            {
                if (t.StartsWith(p, StringComparison.Ordinal))
                    return AircraftShape.Turboprop;
            }
            foreach (var p in BizjetPrefixes)
            {
                if (t.StartsWith(p, StringComparison.Ordinal))
                    return AircraftShape.Bizjet;
            }
        }
        if (engines == 1 || wingspanM is > 0 and < LightSpanM)
            return AircraftShape.Light;
        return wingspanM > WideSpanM ? AircraftShape.Wide : AircraftShape.Narrow;
    }
}
