using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Units;

namespace SimATC.Sim;

/// <summary>
/// Radio y transponder del avión del piloto (botones "Sintonizar" y "Poner squawk" del panel de comunicaciones).
/// Eventos verificados en la página oficial "Aircraft Radio Navigation Events" del SDK de MSFS 2024:
/// <c>COM_RADIO_SET_HZ</c>, <c>COM2_RADIO_SET_HZ</c>, <c>COM3_RADIO_SET_HZ</c> y sus <c>_STBY_</c> (frecuencia en Hz) y
/// <c>XPNDR_SET</c> (código en BCD16).
/// </summary>
public sealed partial class SimConnectBridge
{
    private enum RadioEvent : uint { Com1ActiveHz = 960, Com1StandbyHz = 961, Transponder = 962, Com2ActiveHz = 963, Com2StandbyHz = 964,
        Com3ActiveHz = 969, Com3StandbyHz = 959 }

    private static void RegisterRadio(SimConnect sim)
    {
        sim.MapClientEventToSimEvent(RadioEvent.Com1ActiveHz, "COM_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Com1StandbyHz, "COM_STBY_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Com2ActiveHz, "COM2_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Com2StandbyHz, "COM2_STBY_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Com3ActiveHz, "COM3_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Com3StandbyHz, "COM3_STBY_RADIO_SET_HZ");
        sim.MapClientEventToSimEvent(RadioEvent.Transponder, "XPNDR_SET");
    }

    public void TuneCom(int radio, int khz, bool standby) => Enqueue(sim =>
        sim.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, (radio, standby) switch
        {
            (2, false) => RadioEvent.Com2ActiveHz,
            (2, true) => RadioEvent.Com2StandbyHz,
            (3, false) => RadioEvent.Com3ActiveHz,
            (3, true) => RadioEvent.Com3StandbyHz,
            (_, true) => RadioEvent.Com1StandbyHz,
            _ => RadioEvent.Com1ActiveHz,
        }, (uint)khz * 1000, EventGroup.HighestPriority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY));

    public void SetTransponder(int code) => Enqueue(sim =>
        sim.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, RadioEvent.Transponder, Bco16.Encode(code),
            EventGroup.HighestPriority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY));
}
