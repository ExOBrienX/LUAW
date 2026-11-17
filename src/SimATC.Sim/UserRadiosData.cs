using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Simulation;

namespace SimATC.Sim;

/// <summary>
/// Radios COM1 a COM3 del piloto, en una definición aparte de la del avión: si un avión no tiene COM3, no afecta a la
/// posición ni a COM1. SimVars de "Aircraft Radio Navigation Variables": COM AVAILABLE, COM ACTIVE FREQUENCY (kHz),
/// COM RECEIVE, COM TRANSMIT, COM STATUS (Enum: 0 OK, 1 no existe, 2 sin electricidad, 3 falla) y COM VOLUME (Percent),
/// con índice 1-3.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UserRadiosData
{
    public double Available1, Khz1, Receive1, Transmit1, Status1, Volume1;
    public double Available2, Khz2, Receive2, Transmit2, Status2, Volume2;
    public double Available3, Khz3, Receive3, Transmit3, Status3, Volume3;

    public static void Register(SimConnect sim, Enum definitionId)
    {
        for (var i = 1; i <= 3; i++)
        {
            sim.AddToDataDefinition(definitionId, $"COM AVAILABLE:{i}", "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.AddToDataDefinition(definitionId, $"COM ACTIVE FREQUENCY:{i}", "KHz", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.AddToDataDefinition(definitionId, $"COM RECEIVE:{i}", "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.AddToDataDefinition(definitionId, $"COM TRANSMIT:{i}", "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.AddToDataDefinition(definitionId, $"COM STATUS:{i}", "Enum", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.AddToDataDefinition(definitionId, $"COM VOLUME:{i}", "Percent", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        }
        sim.RegisterDataDefineStruct<UserRadiosData>(definitionId);
    }

    public readonly UserRadios ToRadios() => new(
    [
        new ComRadio(1, Available1 != 0, (int)Math.Round(Khz1), Receive1 != 0, Transmit1 != 0, Powered(Status1), Volume1),
        new ComRadio(2, Available2 != 0, (int)Math.Round(Khz2), Receive2 != 0, Transmit2 != 0, Powered(Status2), Volume2),
        new ComRadio(3, Available3 != 0, (int)Math.Round(Khz3), Receive3 != 0, Transmit3 != 0, Powered(Status3), Volume3),
    ]);

    /// <summary>Sin electricidad (2) o con falla (3), la radio no funciona; los demás valores no la apagan.</summary>
    private static bool Powered(double status) => Math.Round(status) is not (2 or 3);
}
