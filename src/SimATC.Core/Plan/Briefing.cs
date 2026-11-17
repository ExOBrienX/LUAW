namespace SimATC.Core.Plan;

/// <summary>Rendimiento de despegue de SimBrief (TLR) para la pista planificada.</summary>
public sealed record TakeoffPerformance(string Runway, string? Flaps, int? V1, int? Vr, int? V2, string? Thrust, int? FlexTempC);

/// <summary>Meteorología de un aeropuerto del plan, tal como la trae SimBrief.</summary>
public sealed record AirportWeather(string Icao, string? Metar, string? Taf);

/// <summary>
/// Briefing de SimBrief (OFP): los datos importantes para el vuelo y el OFP completo en texto. Solo informativo; el
/// ATC no lo usa para decidir. Pesos y combustible en las unidades del OFP (<see cref="Units"/>: "kgs" o "lbs").
/// </summary>
public sealed record Briefing(string Units)
{
    // Combustible
    public int? FuelRamp { get; init; }
    public int? FuelTakeoff { get; init; }
    public int? FuelLanding { get; init; }
    public int? FuelTrip { get; init; }
    public int? FuelContingency { get; init; }
    public int? FuelAlternate { get; init; }
    public int? FuelReserve { get; init; }
    public int? FuelTaxi { get; init; }
    public int? FuelExtra { get; init; }
    public int? FuelMinTakeoff { get; init; }

    // Pesos
    public int? Zfw { get; init; }
    public int? MaxZfw { get; init; }
    public int? Tow { get; init; }
    public int? MaxTow { get; init; }
    public int? Ldw { get; init; }
    public int? MaxLdw { get; init; }
    public int? Payload { get; init; }
    public int? Passengers { get; init; }
    public int? Cargo { get; init; }

    // Tiempos (s)
    public int? EnrouteS { get; init; }
    public int? BlockS { get; init; }
    public int? TaxiOutS { get; init; }

    // Crucero
    public string? CostIndex { get; init; }
    public string? StepClimbs { get; init; }
    public string? AvgWind { get; init; }
    public int? RouteDistanceNm { get; init; }
    public int? TransitionAltitudeFt { get; init; }
    public int? TransitionLevelFt { get; init; }

    public TakeoffPerformance? Takeoff { get; init; }
    public IReadOnlyList<AirportWeather> Weather { get; init; } = [];

    /// <summary>OFP completo como texto (el HTML de SimBrief sin etiquetas).</summary>
    public string OfpText { get; init; } = "";

    /// <summary>Enlace al PDF del OFP en simbrief.com, si viene.</summary>
    public string? PdfUrl { get; init; }
}
