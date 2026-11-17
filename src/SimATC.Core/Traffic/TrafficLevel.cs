using SimATC.Core.Airports;

namespace SimATC.Core.Traffic;

/// <summary>Nivel de tráfico elegido en Configuración (ADR-109).</summary>
public enum TrafficLevel
{
    Low,
    Medium,
    High,
    /// <summary>Según el aeropuerto: cada tipo de puesto con su propia ocupación típica (por defecto).</summary>
    Average,
    /// <summary>La cantidad de estacionados que pone el usuario (hasta donde quepa).</summary>
    Custom,
}

/// <summary>Tipos de puesto para repartir los estacionados (ADR-109).</summary>
public enum StandCategory
{
    /// <summary>Puertas grandes (GATE_HEAVY, GATE_EXTRA): fuselaje ancho.</summary>
    GateHeavy,
    /// <summary>Puertas medianas: fuselaje estrecho.</summary>
    GateMedium,
    /// <summary>Puertas chicas: regionales.</summary>
    GateSmall,
    /// <summary>Plataformas de carga.</summary>
    Cargo,
    /// <summary>Plataformas grandes remotas (RAMP_GA_LARGE/EXTRA): aviones de línea "de noche", no vuelan.</summary>
    Remote,
    /// <summary>Plataformas de aviación general (RAMP_GA, SMALL, MEDIUM): decoración con aviones del simulador.</summary>
    GeneralAviation,
    /// <summary>Lo demás (combustible, vehículos, militares): no se usa.</summary>
    None,
}

/// <summary>
/// Cuánto se llena cada tipo de puesto y los topes de un nivel (ADR-109). Las ocupaciones son la parte de cada tipo que se
/// llena, eligiendo los puestos al azar; los topes cuidan los FPS y la memoria de video. APROXIMADO: se ajusta con las pruebas.
/// </summary>
public sealed record TrafficPreset(
    double GateHeavy,
    double GateMedium,
    double GateSmall,
    double Cargo,
    double Remote,
    double GeneralAviation,
    int MaxAirline,
    int MaxGeneralAviation,
    int MaxAir,
    int DensityPercent)
{
    /// <summary>Tope absoluto de estacionados de aerolínea (personalizado incluido).</summary>
    public const int MaxParkedEver = 200;

    public static TrafficPreset For(TrafficLevel level, int customCount = 40, int customDensity = 100, int customMaxAir = 15) => level switch
    {
        TrafficLevel.Low => new(0.25, 0.25, 0.25, 0.25, 0.15, 0.10, 30, 3, 8, 50),
        TrafficLevel.Medium => new(0.45, 0.45, 0.40, 0.40, 0.30, 0.20, 60, 6, 15, 100),
        TrafficLevel.High => new(0.70, 0.70, 0.65, 0.60, 0.50, 0.35, 120, 12, 25, 150),
        TrafficLevel.Average => new(0.55, 0.60, 0.50, 0.50, 0.35, 0.30, 90, 10, 15, 100),
        // Personalizado: todos los tipos llenos hasta la cantidad pedida (el reparto entre tipos es proporcional).
        _ => new(1, 1, 1, 1, 1, 0.30, Math.Clamp(customCount, 0, MaxParkedEver), 10, Math.Clamp(customMaxAir, 0, 40), Math.Clamp(customDensity, 0, 300)),
    };

    public double ShareOf(StandCategory c) => c switch
    {
        StandCategory.GateHeavy => GateHeavy,
        StandCategory.GateMedium => GateMedium,
        StandCategory.GateSmall => GateSmall,
        StandCategory.Cargo => Cargo,
        StandCategory.Remote => Remote,
        StandCategory.GeneralAviation => GeneralAviation,
        _ => 0,
    };
}

/// <summary>Cuántos puestos de cada tipo tiene un aeropuerto (ADR-109).</summary>
public static class StandCensus
{
    public static StandCategory CategoryOf(Parking p) => p.Type switch
    {
        ParkingType.GateHeavy or ParkingType.GateExtra => StandCategory.GateHeavy,
        ParkingType.GateMedium => StandCategory.GateMedium,
        ParkingType.GateSmall => StandCategory.GateSmall,
        ParkingType.RampCargo => StandCategory.Cargo,
        ParkingType.RampGaLarge or ParkingType.RampGaExtra => StandCategory.Remote,
        ParkingType.RampGa or ParkingType.RampGaSmall or ParkingType.RampGaMedium => StandCategory.GeneralAviation,
        _ => StandCategory.None,
    };

    public static Dictionary<StandCategory, int> Of(AirportData airport)
    {
        var counts = new Dictionary<StandCategory, int>();
        foreach (var p in airport.Parkings)
        {
            var c = CategoryOf(p);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Cuántos estacionados de aerolínea y de aviación general pide el nivel en este aeropuerto: la ocupación de cada tipo,
    /// con los topes del nivel y dejando <paramref name="keepFree"/> puestos de aerolínea libres para las llegadas.
    /// </summary>
    public static (int Airline, int GeneralAviation) Targets(AirportData airport, TrafficPreset preset, int keepFree)
    {
        var counts = Of(airport);
        var airlineStands = 0;
        var airline = 0.0;
        foreach (var c in new[] { StandCategory.GateHeavy, StandCategory.GateMedium, StandCategory.GateSmall, StandCategory.Cargo, StandCategory.Remote })
        {
            var n = counts.GetValueOrDefault(c);
            airlineStands += n;
            airline += n * preset.ShareOf(c);
        }
        var a = Math.Clamp((int)Math.Round(airline), 0, Math.Min(preset.MaxAirline, Math.Max(0, airlineStands - keepFree)));
        var ga = Math.Clamp((int)Math.Round(counts.GetValueOrDefault(StandCategory.GeneralAviation) * preset.GeneralAviation), 0, preset.MaxGeneralAviation);
        return (a, ga);
    }
}
