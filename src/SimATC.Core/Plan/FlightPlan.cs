using SimATC.Core.Geo;

namespace SimATC.Core.Plan;

/// <summary>De dónde viene el plan del piloto (RF-29).</summary>
public enum PlanSource
{
    SimBrief,
    Manual,
}

/// <summary>Aeropuerto del plan (origen, destino o alterno). La posición falta en un plan manual.</summary>
public sealed record PlanAirport(string Icao, string Name = "", GeoPoint? Position = null, string? PlannedRunway = null);

/// <summary>
/// Un punto de la ruta del plan (navlog de SimBrief). <c>Type</c> es el de SimBrief: "wpt", "vor", "ndb", "apt" o
/// "ltlg" (puntos calculados como TOC/TOD, que no son fijos de navegación).
/// </summary>
public sealed record PlanFix(string Ident, string Type, string? Airway, bool IsSidStar, GeoPoint Position, int AltitudeFt)
{
    /// <summary>Punto calculado por SimBrief (TOC, TOD…), no un fijo real.</summary>
    public bool IsPseudo => Type == "ltlg";
}

/// <summary>
/// Plan de vuelo del piloto (RF-29). Se usa en LUAW para el ATC y el radar; <b>no</b> se carga en el simulador
/// (§3). Los procedimientos planificados (SID/STAR) se validan después contra los datos del simulador (hito 4c).
/// </summary>
public sealed record FlightPlan(
    PlanSource Source,
    string Callsign,
    string AircraftIcao,
    PlanAirport Origin,
    PlanAirport Destination,
    int CruiseAltitudeFt,
    string Route,
    IReadOnlyList<PlanFix> Fixes)
{
    public string? AirlineIcao { get; init; }
    public string? FlightNumber { get; init; }
    public string? Registration { get; init; }
    public PlanAirport? Alternate { get; init; }
    public string? SidIdent { get; init; }
    public string? SidTransition { get; init; }
    public string? StarIdent { get; init; }
    public string? StarTransition { get; init; }
    /// <summary>Ciclo AIRAC con el que SimBrief armó la ruta.</summary>
    public string? Airac { get; init; }
    public DateTime? GeneratedUtc { get; init; }
    /// <summary>Hora de salida programada (calzos fuera).</summary>
    public DateTime? ScheduledOffBlockUtc { get; init; }

    /// <summary>Briefing de SimBrief (combustible, pesos, despegue, meteorología, OFP); null en un plan manual.</summary>
    public Briefing? Briefing { get; init; }
}
