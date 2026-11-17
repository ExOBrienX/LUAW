using SimATC.Core.Geo;

namespace SimATC.Core.Airports;

/// <summary>
/// Datos estáticos de un aeropuerto leídos del simulador (API de facilities, RF-07), ya en unidades internas.
/// Los índices de <see cref="TaxiPath.Start"/>/<see cref="TaxiPath.End"/> apuntan a <see cref="TaxiPoints"/>,
/// salvo en los tramos de tipo <see cref="TaxiPathType.Parking"/>, cuyo End apunta a <see cref="Parkings"/>.
/// </summary>
public sealed record AirportData(
    string Icao,
    string Name,
    GeoPoint Reference,
    double ElevationFt,
    // Valor MAGVAR crudo del simulador (SCEL: 356). Convención de signo: VERIFICAR antes de usarlo.
    double MagVarRawDeg,
    double TransitionAltitudeFt,
    double TransitionLevelFt,
    IReadOnlyList<Runway> Runways,
    IReadOnlyList<Frequency> Frequencies,
    IReadOnlyList<TaxiPoint> TaxiPoints,
    IReadOnlyList<Parking> Parkings,
    IReadOnlyList<TaxiPath> TaxiPaths,
    IReadOnlyList<string> TaxiNames,
    IReadOnlyList<Procedure> Departures,
    IReadOnlyList<Procedure> Arrivals,
    IReadOnlyList<Approach> Approaches);

/// <summary>Extremo de pista. La posición es el extremo físico (incluye umbrales desplazados, no las zonas de parada).</summary>
/// <summary>
/// Para qué sirve una cabecera según el escenario del aeropuerto (ADR-137): cerrada, se puede despegar, se puede aterrizar.
/// </summary>
public readonly record struct RunwayUse(bool Closed, bool Takeoff, bool Landing);

public sealed record RunwayEnd(int Number, RunwayDesignator Designator, GeoPoint Position, double HeadingTrueDeg)
{
    /// <summary>Lo que dice el escenario: cerrada (ADR-137). Sin el dato (caché vieja, datos de prueba), abierta.</summary>
    public bool Closed { get; init; }

    /// <summary>El escenario permite despegar por esta cabecera (sin el dato, sí).</summary>
    public bool TakeoffAllowed { get; init; } = true;

    /// <summary>El escenario permite aterrizar por esta cabecera (sin el dato, sí).</summary>
    public bool LandingAllowed { get; init; } = true;

    /// <summary>Identificador como "17L", "35R", "09".</summary>
    public string Ident => FormatIdent(Number, Designator);

    public static string FormatIdent(int number, RunwayDesignator designator)
    {
        var suffix = designator switch
        {
            RunwayDesignator.Left => "L",
            RunwayDesignator.Right => "R",
            RunwayDesignator.Center => "C",
            RunwayDesignator.Water => "W",
            RunwayDesignator.A => "A",
            RunwayDesignator.B => "B",
            _ => "",
        };
        return $"{number:00}{suffix}";
    }
}

/// <summary>Pista física con sus dos extremos. <see cref="HeadingTrueDeg"/> es el rumbo del extremo primario.</summary>
public sealed record Runway(
    GeoPoint Center,
    double HeadingTrueDeg,
    double LengthM,
    double WidthM,
    double ElevationFt,
    RunwayEnd Primary,
    RunwayEnd Secondary)
{
    /// <summary>"17L/35R".</summary>
    public string Name => $"{Primary.Ident}/{Secondary.Ident}";

    /// <summary>
    /// Arma la pista a partir de los datos del simulador: centro, rumbo verdadero del extremo primario y largo.
    /// El extremo primario queda detrás del centro en la dirección del rumbo (desde ahí se despega con ese rumbo).
    /// </summary>
    public static Runway FromCenter(GeoPoint center, double headingTrueDeg, double lengthM, double widthM,
        double elevationFt, int primaryNumber, RunwayDesignator primaryDesignator,
        int secondaryNumber, RunwayDesignator secondaryDesignator)
    {
        var heading = GeoMath.NormalizeDeg(headingTrueDeg);
        var reciprocal = GeoMath.NormalizeDeg(heading + 180);
        var primary = new RunwayEnd(primaryNumber, primaryDesignator,
            GeoMath.Destination(center, reciprocal, lengthM / 2), heading);
        var secondary = new RunwayEnd(secondaryNumber, secondaryDesignator,
            GeoMath.Destination(center, heading, lengthM / 2), reciprocal);
        return new Runway(center, heading, lengthM, widthM, elevationFt, primary, secondary);
    }

    /// <summary>La misma pista con lo que dice el escenario de cada cabecera (ADR-137).</summary>
    public Runway WithUse(RunwayUse primary, RunwayUse secondary) => this with
    {
        Primary = Primary with { Closed = primary.Closed, TakeoffAllowed = primary.Takeoff, LandingAllowed = primary.Landing },
        Secondary = Secondary with { Closed = secondary.Closed, TakeoffAllowed = secondary.Takeoff, LandingAllowed = secondary.Landing },
    };
}

public sealed record Frequency(FrequencyType Type, int Khz, string Name);

public sealed record TaxiPoint(TaxiPointType Type, HoldShortOrientation Orientation, GeoPoint Position)
{
    public bool IsHoldShort => Type is TaxiPointType.HoldShort or TaxiPointType.IlsHoldShort
        or TaxiPointType.HoldShortNoDraw or TaxiPointType.IlsHoldShortNoDraw;
}

public sealed record Parking(
    ParkingType Type,
    ParkingName Name,
    ParkingName Suffix,
    int Number,
    GeoPoint Position,
    double HeadingTrueDeg,
    double RadiusM,
    IReadOnlyList<string> Airlines)
{
    /// <summary>Cómo se muestra: "Gate 14", "Gate A12", "Parking 412" (sin nombre en los datos: por su tipo).</summary>
    public string DisplayName => StandNames.Display(this);
}

public sealed record TaxiPath(
    TaxiPathType Type,
    double WidthM,
    int RunwayNumber,
    RunwayDesignator RunwayDesignator,
    int Start,
    int End,
    int NameIndex);

/// <summary>SID o STAR.</summary>
public sealed record Procedure(
    string Name,
    IReadOnlyList<ProcedureLeg> Legs,
    IReadOnlyList<RunwayTransition> RunwayTransitions,
    IReadOnlyList<EnrouteTransition> EnrouteTransitions);

public sealed record RunwayTransition(int RunwayNumber, RunwayDesignator RunwayDesignator, IReadOnlyList<ProcedureLeg> Legs);

public sealed record EnrouteTransition(string Name, IReadOnlyList<ProcedureLeg> Legs);

public sealed record Approach(
    ApproachType Type,
    // Sufijo de aproximación múltiple ("X", "Y", "Z"…) o vacío.
    string Suffix,
    int RunwayNumber,
    RunwayDesignator RunwayDesignator,
    string FafIcao,
    IReadOnlyList<ApproachTransition> Transitions,
    IReadOnlyList<ProcedureLeg> FinalLegs,
    IReadOnlyList<ProcedureLeg> MissedLegs);

public sealed record ApproachTransition(string Name, IReadOnlyList<ProcedureLeg> Legs);

/// <summary>
/// Tramo de procedimiento. Rumbos en grados (verdaderos si <see cref="CourseIsTrue"/>, si no magnéticos),
/// distancias en metros, altitudes en pies, velocidades en nudos.
/// </summary>
public sealed record ProcedureLeg(
    LegType Type,
    string FixIcao,
    string FixRegion,
    // 'A' aeropuerto, 'V' VOR, 'N' NDB, 'W' waypoint, o vacío.
    string FixType,
    GeoPoint FixPosition,
    bool FlyOver,
    TurnDirection Turn,
    double CourseDeg,
    bool CourseIsTrue,
    // Distancia del tramo en metros, o minutos si <see cref="RouteDistanceIsMinutes"/>.
    double RouteDistance,
    bool RouteDistanceIsMinutes,
    AltitudeConstraint AltitudeConstraint,
    double Altitude1Ft,
    double Altitude2Ft,
    SpeedConstraint SpeedConstraint,
    double SpeedLimitKt,
    double VerticalAngleDeg,
    GeoPoint ArcCenter,
    double ArcRadiusM,
    bool IsIaf,
    bool IsIf,
    bool IsFaf,
    bool IsMap);
