using System.Runtime.InteropServices;

namespace SimATC.Sim.Facilities;

// Structs crudos de la API de facilities (V-04). Cada uno va con su lista de campos en el MISMO orden y con el tipo
// documentado en SimConnect_AddToFacilityDefinition (INT32 → int, FLOAT32 → float, FLOAT64 → double,
// STRINGn → string de n bytes). Pack = 1: los datos llegan sin relleno.
// Los textos libres (nombres con tildes) llegan en UTF-8: se leen como bytes y se decodifican con FacilityText.

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct AirportRaw
{
    public double Latitude, Longitude, AltitudeM;
    public float MagVar;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] NameUtf8;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Icao;
    public float TransitionAltitudeM, TransitionLevelM;

    public static readonly string[] Fields =
        ["LATITUDE", "LONGITUDE", "ALTITUDE", "MAGVAR", "NAME64", "ICAO", "TRANSITION_ALTITUDE", "TRANSITION_LEVEL"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayRaw
{
    public double Latitude, Longitude, AltitudeM;
    public float Heading, Length, Width;
    public int PrimaryNumber, PrimaryDesignator, SecondaryNumber, SecondaryDesignator;

    // Por cabecera (ADR-137, SDK 1.8.1, SIMCONNECT_AddToFacilityDefinition → RUNWAY, INT8): cerrada, se puede despegar,
    // se puede aterrizar. Los define el escenario del aeropuerto.
    public byte PrimaryClosed, SecondaryClosed, PrimaryTakeoff, PrimaryLanding, SecondaryTakeoff, SecondaryLanding;

    public static readonly string[] Fields =
        ["LATITUDE", "LONGITUDE", "ALTITUDE", "HEADING", "LENGTH", "WIDTH",
         "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR",
         "PRIMARY_CLOSED", "SECONDARY_CLOSED", "PRIMARY_TAKEOFF", "PRIMARY_LANDING", "SECONDARY_TAKEOFF", "SECONDARY_LANDING"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct FrequencyRaw
{
    public int Type, FrequencyHz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] NameUtf8;

    public static readonly string[] Fields = ["TYPE", "FREQUENCY", "NAME"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TaxiPointRaw
{
    public int Type, Orientation;
    public float BiasX, BiasZ;

    public static readonly string[] Fields = ["TYPE", "ORIENTATION", "BIAS_X", "BIAS_Z"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TaxiParkingRaw
{
    public int Type, Name, Suffix;
    public uint Number;
    public float Heading, Radius, BiasX, BiasZ;

    public static readonly string[] Fields =
        ["TYPE", "NAME", "SUFFIX", "NUMBER", "HEADING", "RADIUS", "BIAS_X", "BIAS_Z"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct AirlineRaw
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Name;

    public static readonly string[] Fields = ["NAME"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TaxiPathRaw
{
    public int Type;
    public float Width;
    public int RunwayNumber, RunwayDesignator, Start, End;
    public uint NameIndex;

    public static readonly string[] Fields =
        ["TYPE", "WIDTH", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "START", "END", "NAME_INDEX"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct TaxiNameRaw
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;

    public static readonly string[] Fields = ["NAME"];
}

/// <summary>DEPARTURE y ARRIVAL comparten forma.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct ProcedureRaw
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Name;

    public static readonly string[] Fields = ["NAME"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayTransitionRaw
{
    public int RunwayNumber, RunwayDesignator;

    public static readonly string[] Fields = ["RUNWAY_NUMBER", "RUNWAY_DESIGNATOR"];
}

/// <summary>ENROUTE_TRANSITION y APPROACH_TRANSITION: solo se usa el nombre.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct NamedTransitionRaw
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Name;

    public static readonly string[] Fields = ["NAME"];
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct ApproachRaw
{
    public int Type, Suffix, RunwayNumber, RunwayDesignator;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string FafIcao;

    public static readonly string[] Fields = ["TYPE", "SUFFIX", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "FAF_ICAO"];
}

/// <summary>APPROACH_LEG, FINAL_APPROACH_LEG y MISSED_APPROACH_LEG comparten forma.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
internal struct LegRaw
{
    public int Type;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string FixIcao;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string FixRegion;
    public int FixType;
    public double FixLatitude, FixLongitude;
    public int FlyOver, DistanceMinute, TrueDegree, TurnDirection;
    public float Course, RouteDistance;
    public int AltitudeDescriptor;
    public float Altitude1M, Altitude2M, SpeedLimit;
    public int SpeedDescriptor;
    public float VerticalAngle;
    public double ArcCenterLatitude, ArcCenterLongitude;
    public float RadiusM;
    public int IsIaf, IsIf, IsFaf, IsMap;

    public static readonly string[] Fields =
        ["TYPE", "FIX_ICAO", "FIX_REGION", "FIX_TYPE", "FIX_LATITUDE", "FIX_LONGITUDE",
         "FLY_OVER", "DISTANCE_MINUTE", "TRUE_DEGREE", "TURN_DIRECTION", "COURSE", "ROUTE_DISTANCE",
         "APPROACH_ALT_DESC", "ALTITUDE1", "ALTITUDE2", "SPEED_LIMIT", "APPROACH_SPEED_DESC", "VERTICAL_ANGLE",
         "ARC_CENTER_FIX_LATITUDE", "ARC_CENTER_FIX_LONGITUDE", "RADIUS", "IS_IAF", "IS_IF", "IS_FAF", "IS_MAP"];
}

internal static class FacilityText
{
    /// <summary>Decodifica un texto UTF-8 terminado en NUL.</summary>
    public static string FromUtf8(byte[]? bytes)
    {
        if (bytes is null)
            return "";
        var len = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, len < 0 ? bytes.Length : len).Trim();
    }
}