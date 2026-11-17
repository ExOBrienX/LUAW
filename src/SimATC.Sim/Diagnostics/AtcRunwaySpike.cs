using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim.Diagnostics;

/// <summary>
/// Diagnóstico de V-49 (ADR-137): ¿el simulador dice qué pista le asignó su propio ATC al piloto? Lee las SimVars
/// <c>ATC RUNWAY …</c> del SDK 1.8.1 (Aircraft Radio Navigation Variables): si hay una pista seleccionada, el aeropuerto,
/// su rumbo verdadero, largo y ancho, y a qué distancia está el avión. Si <c>ATC RUNWAY SELECTED</c> es falso, todas las
/// demás traen valores por defecto (así lo dice la documentación). Cada 2 s durante 20 s, para verlo cambiar.
/// Uso: <c>dotnet run --project tools/SimATC.Probe -- --atc-runway</c>, con MSFS abierto.
/// </summary>
public static class AtcRunwaySpike
{
    private enum Def : uint { Runway = 950 }

    private enum Req : uint { Runway = 950 }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct Data
    {
        public double Selected, HeadingTrue, LengthM, WidthM, DistanceM, StartM, EndM;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Airport;
    }

    public static int Run(Action<string> print)
    {
        var inv = CultureInfo.InvariantCulture;
        using var signal = new AutoResetEvent(false);
        SimConnect sim;
        try
        {
            sim = new SimConnect("LUAW Probe atc-runway", IntPtr.Zero, 0, signal, 0);
        }
        catch (COMException)
        {
            print("No hay conexión con MSFS. Ábrelo y vuelve a intentar.");
            return 1;
        }
        sim.OnRecvException += (_, e) => print($"  Excepción de SimConnect: {(SIMCONNECT_EXCEPTION)e.dwException} (índice {e.dwIndex})");
        sim.OnRecvSimobjectData += (_, d) =>
        {
            if (d.dwRequestID != (uint)Req.Runway)
                return;
            var r = (Data)d.dwData[0];
            print(r.Selected != 0
                ? $"  Pista del ATC del juego: aeropuerto \"{r.Airport}\", rumbo {r.HeadingTrue.ToString("F1", inv)}° verdaderos, " +
                  $"{r.LengthM.ToString("F0", inv)} × {r.WidthM.ToString("F0", inv)} m, a {r.DistanceM.ToString("F0", inv)} m " +
                  $"(inicio {r.StartM.ToString("F0", inv)} m, final {r.EndM.ToString("F0", inv)} m)"
                : "  ATC RUNWAY SELECTED = falso: el ATC del juego no tiene una pista seleccionada para ti.");
        };
        foreach (var (v, u) in new[]
                 {
                     ("ATC RUNWAY SELECTED", "bool"), ("ATC RUNWAY HEADING DEGREES TRUE", "degrees"), ("ATC RUNWAY LENGTH", "meters"),
                     ("ATC RUNWAY WIDTH", "meters"), ("ATC RUNWAY DISTANCE", "meters"), ("ATC RUNWAY START DISTANCE", "meters"),
                     ("ATC RUNWAY END DISTANCE", "meters"),
                 })
            sim.AddToDataDefinition(Def.Runway, v, u, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(Def.Runway, "ATC RUNWAY AIRPORT NAME", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Data>(Def.Runway);
        sim.RequestDataOnSimObject(Req.Runway, Def.Runway, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 2, 0);
        print("Leyendo ATC RUNWAY … cada 2 s durante 20 s.");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            signal.WaitOne(200);
            sim.ReceiveMessage();
        }
        sim.Dispose();
        return 0;
    }
}
