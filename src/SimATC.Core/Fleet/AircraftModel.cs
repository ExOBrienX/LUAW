namespace SimATC.Core.Fleet;

public enum ModelSource
{
    Fsltl,
    Aig,
    /// <summary>Modelo del propio simulador (decoración de aviación general, ADR-109).</summary>
    Sim,
}

/// <summary>Categoría de estela OACI (icao_WTC): L, M, H, J.</summary>
public enum WakeCategory
{
    Unknown,
    Light,
    Medium,
    Heavy,
    Super,
}

/// <summary>
/// Un modelo con livery que se puede inyectar, con los datos leídos de su aircraft.cfg y flight_model.cfg.
/// </summary>
/// <param name="Title">Título del contenedor (lo que pide <c>AICreateNonATCAircraft_EX1</c>).</param>
/// <param name="IcaoType">Tipo OACI del modelo (A20N, B738…).</param>
/// <param name="IcaoAirline">Código OACI de la aerolínea de la livery, o vacío si es genérica.</param>
/// <param name="WingspanM">Envergadura en metros, o 0 si no se pudo leer.</param>
/// <param name="ParkingTypes">Tipos de puesto que acepta (GATE, RAMP, CARGO…), de atc_parking_types.</param>
/// <param name="ParkingCodes">Códigos de aerolínea para puestos, de atc_parking_codes.</param>
public sealed record AircraftModel(
    string Title,
    ModelSource Source,
    string IcaoType,
    string IcaoAirline,
    WakeCategory Wake,
    int EngineCount,
    double WingspanM,
    IReadOnlyList<string> ParkingTypes,
    IReadOnlyList<string> ParkingCodes,
    // Rueda de nariz por delante del punto de referencia (m, de flight_model.cfg); 0 si no se pudo leer.
    double NoseGearM = 0)
{
    public bool IsGeneric => IcaoAirline.Length == 0;

    public static WakeCategory ParseWake(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "L" => WakeCategory.Light,
        "M" => WakeCategory.Medium,
        "H" => WakeCategory.Heavy,
        "J" => WakeCategory.Super,
        _ => WakeCategory.Unknown,
    };
}
