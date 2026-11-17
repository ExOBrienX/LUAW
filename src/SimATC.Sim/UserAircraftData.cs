using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim;

/// <summary>
/// Datos crudos del avión del piloto tal como llegan de SimConnect. El orden de los campos debe coincidir
/// con <see cref="Definition"/>. SimVars y unidades verificadas en el SDK de MSFS 2024 (V-03).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UserAircraftData
{
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double AltitudeMslFt;
    public double AltitudeAglFt;
    public double HeadingTrueDeg;
    public double IndicatedAirspeedKt;
    public double GroundSpeedKt;
    public double VerticalSpeedFpm;
    public double OnGround;
    public double Com1ActiveKhz;
    public double TransponderBco16;
    public double ParkingBrake;
    public double PushbackAttached;
    public double PressureAltitudeFt;
    public double QnhAltitudeFt;
    public double Engine1Running;
    public double Engine2Running;

    /// <summary>(SimVar, unidad) en el mismo orden que los campos.</summary>
    public static readonly (string SimVar, string Unit)[] Definition =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE ALT ABOVE GROUND", "feet"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("AIRSPEED INDICATED", "knots"),
        ("GROUND VELOCITY", "knots"),
        ("VERTICAL SPEED", "feet/minute"),
        ("SIM ON GROUND", "Bool"),
        ("COM ACTIVE FREQUENCY:1", "KHz"),
        ("TRANSPONDER CODE:1", "Bco16"),
        ("BRAKE PARKING POSITION", "Bool"),
        ("PUSHBACK ATTACHED", "Bool"),
        ("PRESSURE ALTITUDE", "feet"),
        ("INDICATED ALTITUDE CALIBRATED", "feet"),
        // Motores encendidos (la misma SimVar que se usa para los IA, SimConnectBridge.Traffic): para el fin del vuelo.
        ("GENERAL ENG COMBUSTION:1", "Bool"),
        ("GENERAL ENG COMBUSTION:2", "Bool"),
    ];

    public static void Register(SimConnect sim, Enum definitionId)
    {
        foreach (var (simVar, unit) in Definition)
            sim.AddToDataDefinition(definitionId, simVar, unit, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<UserAircraftData>(definitionId);
    }
}
