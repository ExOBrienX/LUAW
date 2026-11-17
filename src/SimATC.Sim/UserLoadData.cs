using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim;

/// <summary>
/// Carga e identidad del avión del piloto tal como llegan de SimConnect. El orden y el tamaño de los campos deben
/// coincidir con <see cref="Register"/>. SimVars, unidades y largos verificados en el SDK 1.8.1 (ADR-026): ATC ID tiene
/// hasta 10 caracteres; ATC MODEL, ATC TYPE y TITLE hasta 128.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct UserLoadData
{
    public double FuelLbs;
    public double TotalWeightLbs;
    public double EmptyWeightLbs;
    public double MaxGrossWeightLbs;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string AtcModel;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string AtcType;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Title;

    public static void Register(SimConnect sim, Enum definitionId)
    {
        sim.AddToDataDefinition(definitionId, "FUEL TOTAL QUANTITY WEIGHT", "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "TOTAL WEIGHT", "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "EMPTY WEIGHT", "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "MAX GROSS WEIGHT", "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "ATC ID", null, SIMCONNECT_DATATYPE.STRING32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING128, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "ATC TYPE", null, SIMCONNECT_DATATYPE.STRING128, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(definitionId, "TITLE", null, SIMCONNECT_DATATYPE.STRING128, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<UserLoadData>(definitionId);
    }
}
