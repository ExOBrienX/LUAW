using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim.Diagnostics;

/// <summary>
/// Spike de V-27 (RF-21c), tercera versión.
/// <list type="bullet">
/// <item>Primera prueba (2026-10-31): <c>TOGGLE_PUSHBACK</c> engancha y empuja recto a ~2.4 kt con el freno suelto;
///   <c>TUG_DISABLE</c> lo detiene en ~2.5 m; <c>TUG_SPEED</c> y <c>TUG_HEADING</c> dan NAME_UNRECOGNIZED.</item>
/// <item>Segunda prueba: escribir <c>PUSHBACK STATE</c> como FLOAT64 da DATA_ERROR y el valor no cambia (se lee 3 sin
///   remolque y 0 con remolque).</item>
/// </list>
/// Ahora: (A) <c>PUSHBACK STATE</c> como INT32; (B) con el remolque empujando, escribir nosotros la velocidad de giro del
/// avión <c>ROTATION VELOCITY BODY Y</c> (rad/s, escribible: V-09) diez veces por segundo, a un lado y al otro.
/// Uso: avión en un puesto con espacio detrás, freno puesto: <c>dotnet run --project tools/SimATC.Probe -- --pushback</c>.
/// </summary>
public static class PushbackSpike
{
    private enum Def : uint { State = 950, PushStateInt = 951, Yaw = 952 }

    private enum Req : uint { State = 950 }

    private enum Ev : uint { Toggle = 970, Disable = 973 }

    private enum Group : uint { Highest = 1 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct State
    {
        public double Lat, Lon, HeadingTrue, GroundKt, ParkingBrake, PushState, Attached, YawRate;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IntValue
    {
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DoubleValue
    {
        public double Value;
    }

    private static readonly (string Var, string Unit)[] Vars =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("GROUND VELOCITY", "knots"),
        ("BRAKE PARKING POSITION", "bool"),
        ("PUSHBACK STATE", "enum"),
        ("PUSHBACK ATTACHED", "bool"),
        ("ROTATION VELOCITY BODY Y", "radians per second"),
    ];

    public static int Run(Action<string> print)
    {
        var inv = CultureInfo.InvariantCulture;
        using var signal = new AutoResetEvent(false);
        SimConnect sim;
        try
        {
            sim = new SimConnect("LUAW Probe pushback", IntPtr.Zero, 0, signal, 0);
        }
        catch (COMException)
        {
            print("No hay conexión con MSFS. Ábrelo y vuelve a intentar.");
            return 1;
        }

        State? last = null;
        double? startLat = null, startLon = null, prevHeading = null;
        sim.OnRecvException += (_, e) => print($"  Excepción de SimConnect: {(SIMCONNECT_EXCEPTION)e.dwException} (paquete {e.dwSendID})");
        sim.OnRecvSimobjectData += (_, d) =>
        {
            if (d.dwRequestID != (uint)Req.State)
                return;
            var s = (State)d.dwData[0];
            last = s;
            startLat ??= s.Lat;
            startLon ??= s.Lon;
            var moved = Distance(startLat.Value, startLon.Value, s.Lat, s.Lon);
            var rate = prevHeading is { } p ? ((s.HeadingTrue - p + 540) % 360) - 180 : 0;
            prevHeading = s.HeadingTrue;
            print($"    rumbo {s.HeadingTrue.ToString("F1", inv)}° ({(rate >= 0 ? "+" : "")}{rate.ToString("F1", inv)}°/s) · " +
                  $"{s.GroundKt.ToString("F1", inv)} kt · freno {(s.ParkingBrake != 0 ? "PUESTO" : "suelto")} · " +
                  $"PUSHBACK STATE {s.PushState.ToString("F0", inv)} · enganchado {(s.Attached != 0 ? "sí" : "no")} · " +
                  $"giro leído {(s.YawRate * 180 / Math.PI).ToString("F1", inv)}°/s · a {moved.ToString("F1", inv)} m");
        };
        foreach (var (v, u) in Vars)
            sim.AddToDataDefinition(Def.State, v, u, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<State>(Def.State);
        sim.AddToDataDefinition(Def.PushStateInt, "PUSHBACK STATE", "enum", SIMCONNECT_DATATYPE.INT32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<IntValue>(Def.PushStateInt);
        sim.AddToDataDefinition(Def.Yaw, "ROTATION VELOCITY BODY Y", "radians per second", SIMCONNECT_DATATYPE.FLOAT64, 0f,
            SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<DoubleValue>(Def.Yaw);
        sim.MapClientEventToSimEvent(Ev.Toggle, "TOGGLE_PUSHBACK");
        sim.MapClientEventToSimEvent(Ev.Disable, "TUG_DISABLE");
        sim.RequestDataOnSimObject(Req.State, Def.State, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

        void Send(Ev ev) =>
            sim.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, ev, 0, Group.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

        void Wait(double seconds, double? yawRadS = null)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (yawRadS is { } yaw)
                    sim.SetDataOnSimObject(Def.Yaw, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new DoubleValue { Value = yaw });
                signal.WaitOne(100);
                sim.ReceiveMessage();
            }
        }

        var cancelled = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancelled = true;
        };

        try
        {
            print("Pushback (V-27, tercera prueba). Ctrl+C para cortar.");
            print("Paso 1 · estado inicial (3 s). Deja el freno PUESTO:");
            Wait(3);
            if (last is null)
            {
                print("No llegan datos del avión.");
                return 1;
            }

            print("Paso 2 · TOGGLE_PUSHBACK. SUELTA EL FRENO AHORA. Empuja recto 6 s:");
            Send(Ev.Toggle);
            Wait(6);
            if (cancelled)
                return 0;

            print("Paso 3 · PUSHBACK STATE = 1 escrito como entero (INT32), 6 s. ¿Hay DATA_ERROR? ¿Cambia el valor o gira?");
            sim.SetDataOnSimObject(Def.PushStateInt, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new IntValue { Value = 1 });
            Wait(6);
            sim.SetDataOnSimObject(Def.PushStateInt, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new IntValue { Value = 0 });
            if (cancelled)
                return 0;

            print("Paso 4 · giro escrito por LUAW: ROTATION VELOCITY BODY Y = +3°/s durante 8 s. ¿Gira? ¿Hacia dónde va la COLA?");
            Wait(8, 3 * Math.PI / 180);
            if (cancelled)
                return 0;

            print("Paso 5 · ROTATION VELOCITY BODY Y = -3°/s durante 8 s. ¿Gira al otro lado?");
            Wait(8, -3 * Math.PI / 180);
            if (cancelled)
                return 0;

            print("Paso 6 · TUG_DISABLE (5 s).");
            Send(Ev.Disable);
            Wait(5);
            print("Listo. Cuenta si giró en los pasos 3, 4 y 5, y hacia dónde fue la cola.");
            return 0;
        }
        finally
        {
            Send(Ev.Disable);
            Wait(0.5);
            sim.Dispose();
        }
    }

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180 * Math.Cos(lat1 * Math.PI / 180);
        return r * Math.Sqrt(dLat * dLat + dLon * dLon);
    }
}
