using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim;

/// <summary>
/// Clima en la posición del avión del piloto (V-18, ADR-027). SimVars y unidades verificadas en el SDK 1.8.1; el orden
/// de los campos debe coincidir con <see cref="Register"/>. MSFS las calcula siempre en la posición del piloto.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AmbientData
{
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double OnGround;
    public double AglFt;
    public double WindFromTrueDeg;
    public double WindKt;
    public double VisibilityM;
    public double TemperatureC;
    public double SeaLevelPressureMb;
    public double PrecipitationMask;
    public double MagVarDeg;

    private static readonly (string SimVar, string Unit)[] Definition =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("SIM ON GROUND", "bool"),
        ("PLANE ALT ABOVE GROUND", "feet"),
        ("AMBIENT WIND DIRECTION", "degrees"),
        ("AMBIENT WIND VELOCITY", "knots"),
        ("AMBIENT VISIBILITY", "meters"),
        ("AMBIENT TEMPERATURE", "celsius"),
        ("SEA LEVEL PRESSURE", "millibars"),
        ("AMBIENT PRECIP STATE", "mask"),
        ("MAGVAR", "degrees"),
    ];

    public static void Register(SimConnect sim, Enum definitionId)
    {
        foreach (var (simVar, unit) in Definition)
            sim.AddToDataDefinition(definitionId, simVar, unit, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AmbientData>(definitionId);
    }
}
