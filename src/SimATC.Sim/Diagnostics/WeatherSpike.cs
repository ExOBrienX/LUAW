using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim.Diagnostics;

/// <summary>
/// Spike de V-18 (hito 4d): ¿cómo obtiene LUAW el clima que simula MSFS 2024?
/// <list type="number">
/// <item>Pide el METAR de cada estación con <c>WeatherRequestObservationAtStation</c> (marcada "Deprecated" en la doc del
///   SDK 1.8.1: hay que ver si todavía responde).</item>
/// <item>Lee las SimVars ambientales, que el SDK calcula en la posición del avión del piloto.</item>
/// <item>Compara <c>MAGVAR</c> (SimVar) con el MAGVAR del aeropuerto, para fijar su signo (V-04).</item>
/// </list>
/// Uso: <c>dotnet run --project tools/SimATC.Probe -- --weather SCEL SCIE</c>, con MSFS abierto y el avión en un aeropuerto.
/// </summary>
public static class WeatherSpike
{
    private enum Def : uint { Ambient = 900 }

    private enum Req : uint { Ambient = 900, MetarBase = 910 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Ambient
    {
        public double Lat, Lon, WindDirTrue, WindKt, SeaLevelMb, TempC, VisibilityM, MagVar, PrecipState, OnGround;
    }

    private static readonly (string Var, string Unit)[] AmbientVars =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("AMBIENT WIND DIRECTION", "degrees"),
        ("AMBIENT WIND VELOCITY", "knots"),
        ("SEA LEVEL PRESSURE", "millibars"),
        ("AMBIENT TEMPERATURE", "celsius"),
        ("AMBIENT VISIBILITY", "meters"),
        ("MAGVAR", "degrees"),
        ("AMBIENT PRECIP STATE", "mask"),
        ("SIM ON GROUND", "bool"),
    ];

    public static int Run(string[] stations, Action<string> print)
    {
        var inv = CultureInfo.InvariantCulture;
        using var signal = new AutoResetEvent(false);
        SimConnect sim;
        try
        {
            sim = new SimConnect("LUAW Probe weather", IntPtr.Zero, 0, signal, 0);
        }
        catch (COMException)
        {
            print("No hay conexión con MSFS. Ábrelo y vuelve a intentar.");
            return 1;
        }

        var pending = stations.Length + 1; // ambiente + un METAR (o una excepción) por estación
        sim.OnRecvException += (_, e) =>
        {
            print($"  Excepción de SimConnect: {(SIMCONNECT_EXCEPTION)e.dwException} (paquete {e.dwSendID}, índice {e.dwIndex})");
            pending--;
        };
        sim.OnRecvWeatherObservation += (_, w) =>
        {
            var who = stations[w.dwRequestID - (uint)Req.MetarBase];
            print($"  METAR ({who}): {(string.IsNullOrWhiteSpace(w.szMetar) ? "(vacío)" : w.szMetar)}");
            pending--;
        };
        sim.OnRecvSimobjectData += (_, d) =>
        {
            if (d.dwRequestID != (uint)Req.Ambient)
                return;
            var a = (Ambient)d.dwData[0];
            print($"  Posición del avión: {a.Lat.ToString("F4", inv)}, {a.Lon.ToString("F4", inv)} (en tierra: {a.OnGround != 0})");
            print($"  Viento: {a.WindDirTrue.ToString("F0", inv)}° verdaderos, {a.WindKt.ToString("F1", inv)} kt");
            print($"  QNH (SEA LEVEL PRESSURE): {a.SeaLevelMb.ToString("F1", inv)} hPa");
            print($"  Temperatura: {a.TempC.ToString("F1", inv)} °C · visibilidad: {a.VisibilityM.ToString("F0", inv)} m · precipitación (máscara): {a.PrecipState}");
            print($"  MAGVAR (SimVar): {a.MagVar.ToString("F2", inv)}°");
            pending--;
        };

        foreach (var (v, u) in AmbientVars)
            sim.AddToDataDefinition(Def.Ambient, v, u, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Ambient>(Def.Ambient);
        sim.RequestDataOnSimObject(Req.Ambient, Def.Ambient, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

        print("Pidiendo METAR por estación (función marcada Deprecated en MSFS 2024)…");
        for (var i = 0; i < stations.Length; i++)
            sim.WeatherRequestObservationAtStation((Req)((uint)Req.MetarBase + i), stations[i]);

        // Espera las respuestas (o 10 s): ambiente, y por cada estación su METAR o una excepción.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (pending > 0 && DateTime.UtcNow < deadline)
        {
            signal.WaitOne(200);
            sim.ReceiveMessage();
        }
        if (pending > 0)
            print($"  ({pending} pedido(s) sin respuesta en 10 s: la función de METAR probablemente ya no responde)");
        sim.Dispose();
        return 0;
    }
}
