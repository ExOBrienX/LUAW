namespace SimATC.Core.Atc;

/// <summary>
/// Alcance de una frecuencia desde la posición del piloto (ADR-078, pedido de Marco: "Sintonizar" como el COM del juego,
/// solo lo que está al alcance): el menor entre el volumen de servicio de la dependencia (APROXIMADO por rol) y el
/// horizonte de radio VHF, 1,23·(√h₁ + √h₂) NM con las alturas en pies (antena de tierra de 100 ft, APROXIMADO).
/// </summary>
public static class RadioRange
{
    /// <summary>Altura de la antena de la dependencia sobre el terreno (ft). APROXIMADO.</summary>
    public const double StationAntennaFt = 100;

    /// <summary>Alcance del ATIS (NM). APROXIMADO.</summary>
    public const double AtisNm = 60;

    /// <summary>Volumen de servicio por rol (NM). APROXIMADO: Autorizaciones/Ground 10, Torre 25, Aproximación/Salida 60, Centro sin límite propio.</summary>
    public static double ServiceNm(AtcRole role) => role switch
    {
        AtcRole.Delivery or AtcRole.Ground => 10,
        AtcRole.Tower => 25,
        AtcRole.Departure or AtcRole.Approach => 60,
        _ => 400,
    };

    /// <summary>Horizonte de radio VHF (NM) desde <paramref name="aglFt"/> pies sobre el terreno.</summary>
    public static double HorizonNm(double aglFt) => 1.23 * (Math.Sqrt(Math.Max(aglFt, 10)) + Math.Sqrt(StationAntennaFt));

    /// <summary>¿Se recibe una dependencia de ese volumen de servicio a esa distancia y altura?</summary>
    public static bool InRange(double serviceNm, double distanceNm, double aglFt) =>
        distanceNm <= Math.Min(serviceNm, HorizonNm(aglFt));

    public static bool InRange(AtcRole role, double distanceNm, double aglFt) => InRange(ServiceNm(role), distanceNm, aglFt);
}
