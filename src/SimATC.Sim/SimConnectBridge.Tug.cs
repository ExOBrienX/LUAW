using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim;

/// <summary>
/// Remolque del simulador para el avión del piloto (RF-21c, V-27). Medido en el simulador (2026-10-31):
/// <list type="bullet">
/// <item><c>TOGGLE_PUSHBACK</c> engancha el remolque y empuja recto a ~2.4 kt con el freno suelto; <c>TUG_DISABLE</c>
///   lo detiene en ~2.5 m y el remolque se va.</item>
/// <item><c>TUG_SPEED</c> y <c>TUG_HEADING</c> no existen en SimConnect (NAME_UNRECOGNIZED) y escribir
///   <c>PUSHBACK STATE</c> da DATA_ERROR: el remolque no se puede dirigir.</item>
/// <item>Escribiendo <c>ROTATION VELOCITY BODY Y</c> (rad/s) en el avión del piloto mientras empuja, el avión gira
///   (positivo = la nariz en sentido horario), unas tres veces más rápido que lo pedido.</item>
/// </list>
/// </summary>
public sealed partial class SimConnectBridge
{
    private enum TugEvent : uint { Toggle = 965, Disable = 968 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct YawRateData
    {
        public double RadPerSecond;
    }

    private static void RegisterTug(SimConnect sim)
    {
        sim.MapClientEventToSimEvent(TugEvent.Toggle, "TOGGLE_PUSHBACK");
        sim.MapClientEventToSimEvent(TugEvent.Disable, "TUG_DISABLE");
        sim.AddToDataDefinition(DefinitionId.PushbackYaw, "ROTATION VELOCITY BODY Y", "radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f,
            SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<YawRateData>(DefinitionId.PushbackYaw);
    }

    public void AttachPushbackTug() => Enqueue(sim => SendUser(sim, TugEvent.Toggle));

    public void SetPushbackYawRate(double degPerSecond) => Enqueue(sim =>
        sim.SetDataOnSimObject(DefinitionId.PushbackYaw, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
            new YawRateData { RadPerSecond = degPerSecond * Math.PI / 180 }));

    public void ReleasePushbackTug() => Enqueue(sim => SendUser(sim, TugEvent.Disable));

    private static void SendUser(SimConnect sim, TugEvent ev) =>
        sim.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, ev, 0, EventGroup.HighestPriority,
            SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
}
