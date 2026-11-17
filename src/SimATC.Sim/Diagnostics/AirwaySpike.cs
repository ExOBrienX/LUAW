using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim.Diagnostics;

/// <summary>
/// Spike de aerovías (V-21 a V-24, RF-20b): cuántos waypoints trae la caché de facilities, si un <c>WAYPOINT</c> trae sus
/// <c>ROUTE</c> (aerovías) con el siguiente y el anterior, si un VOR pedido como waypoint también, y cuántos pedidos por
/// segundo aguanta. Campos según SimConnect_AddToFacilityDefinition (MSFS 2024, verificado el 2026-11-01).
/// Uso, con MSFS abierto: <c>dotnet run --project tools/SimATC.Probe -- --airways [FIX:REGION …]</c>.
/// </summary>
public static class AirwaySpike
{
    private enum Def : uint { Waypoint = 980 }

    private enum Req : uint { List = 980, First = 1000 }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct WaypointRaw
    {
        public double Latitude, Longitude, AltitudeM;
        public int Type;
        public float MagVar;
        public int RouteCount;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Icao;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Region;

        public static readonly string[] Fields = ["LATITUDE", "LONGITUDE", "ALTITUDE", "TYPE", "MAGVAR", "N_ROUTES", "ICAO", "REGION"];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct RouteRaw
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public int Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string NextIcao;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string NextRegion;
        public int NextType;
        public double NextLatitude, NextLongitude;
        public float NextAltitudeM;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string PrevIcao;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string PrevRegion;
        public int PrevType;

        public static readonly string[] Fields =
            ["NAME", "TYPE", "NEXT_ICAO", "NEXT_REGION", "NEXT_TYPE", "NEXT_LATITUDE", "NEXT_LONGITUDE", "NEXT_ALTITUDE",
             "PREV_ICAO", "PREV_REGION", "PREV_TYPE"];
    }

    /// <summary>Fijos de prueba de las cartas de SCEL (STAR ASIM7D, VTN1D, EROL2D y el VOR VTN) y su región. TEST DATA.</summary>
    private static readonly (string Icao, string Region)[] DefaultFixes =
        [("ASIMO", "SC"), ("TENUT", "SC"), ("OLMUE", "SC"), ("EROLO", "SC"), ("KADAK", "SC"), ("VTN", "SC"), ("SEDPA", "SC")];

    public static int Run(string[] args, Action<string> print)
    {
        var inv = CultureInfo.InvariantCulture;
        using var signal = new AutoResetEvent(false);
        SimConnect sim;
        try
        {
            sim = new SimConnect("LUAW Probe aerovias", IntPtr.Zero, 0, signal, 0);
        }
        catch (COMException)
        {
            print("No hay conexión con MSFS. Ábrelo y vuelve a intentar.");
            return 1;
        }

        var fixes = args.Where(a => a.Contains(':')).Select(a => a.Split(':')).Select(p => (p[0].ToUpperInvariant(), p[1].ToUpperInvariant())).ToList();
        if (fixes.Count == 0)
            fixes = [.. DefaultFixes];

        sim.OnRecvException += (_, e) => print($"  Excepción de SimConnect: {(SIMCONNECT_EXCEPTION)e.dwException} (paquete {e.dwSendID}, índice {e.dwIndex})");

        // V-21: la lista de waypoints de la caché (la "burbuja" alrededor del avión).
        var listCount = 0;
        var listPackets = 0;
        var listDone = false;
        sim.OnRecvWaypointList += (_, d) =>
        {
            listPackets++;
            listCount += d.rgData.Length;
            if (d.dwEntryNumber + 1 >= d.dwOutOf)
                listDone = true;
        };

        // V-22/V-23: datos de cada waypoint con sus aerovías.
        var waypoints = new Dictionary<uint, WaypointRaw>();
        var routes = new Dictionary<uint, List<RouteRaw>>();
        var ended = new HashSet<uint>();
        sim.OnRecvFacilityData += (_, d) =>
        {
            switch ((SIMCONNECT_FACILITY_DATA_TYPE)d.Type)
            {
                case SIMCONNECT_FACILITY_DATA_TYPE.WAYPOINT:
                    waypoints[d.UserRequestId] = (WaypointRaw)d.Data[0];
                    break;
                case SIMCONNECT_FACILITY_DATA_TYPE.ROUTE:
                    if (!routes.TryGetValue(d.UserRequestId, out var list))
                        routes[d.UserRequestId] = list = [];
                    list.Add((RouteRaw)d.Data[0]);
                    break;
            }
        };
        sim.OnRecvFacilityDataEnd += (_, d) => ended.Add(d.RequestId);

        void Add(string s) => sim.AddToFacilityDefinition(Def.Waypoint, s);
        Add("OPEN WAYPOINT");
        foreach (var f in WaypointRaw.Fields)
            Add(f);
        Add("OPEN ROUTE");
        foreach (var f in RouteRaw.Fields)
            Add(f);
        Add("CLOSE ROUTE");
        Add("CLOSE WAYPOINT");
        sim.RegisterFacilityDataDefineStruct<WaypointRaw>(SIMCONNECT_FACILITY_DATA_TYPE.WAYPOINT);
        sim.RegisterFacilityDataDefineStruct<RouteRaw>(SIMCONNECT_FACILITY_DATA_TYPE.ROUTE);

        void Pump(double seconds, Func<bool> until)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds && !until())
            {
                signal.WaitOne(50);
                sim.ReceiveMessage();
            }
        }

        print("Aerovías (V-21 a V-24). Con MSFS abierto, idealmente cerca de SCEL.");
        var watch = Stopwatch.StartNew();
        sim.RequestFacilitiesList_EX1(SIMCONNECT_FACILITY_LIST_TYPE.WAYPOINT, Req.List);
        Pump(20, () => listDone);
        print($"V-21 · lista de waypoints en caché: {listCount} en {listPackets} paquetes, {watch.Elapsed.TotalSeconds.ToString("0.00", inv)} s " +
              $"(memoria del proceso {Environment.WorkingSet / 1048576} MB).");

        // Cada fijo de prueba, y después se sigue la primera aerovía hacia adelante (V-24: ritmo de pedidos).
        uint next = (uint)Req.First;
        var asked = new Dictionary<uint, string>();
        watch.Restart();
        foreach (var (icao, region) in fixes)
        {
            asked[next] = $"{icao}/{region}";
            sim.RequestFacilityData(Def.Waypoint, (Req)next, icao, region);
            next++;
        }
        Pump(15, () => asked.Keys.All(ended.Contains));
        print($"V-22/V-23 · {asked.Count} fijos pedidos, {ended.Count} respondidos en {watch.Elapsed.TotalSeconds.ToString("0.00", inv)} s:");
        foreach (var (id, name) in asked)
        {
            if (!waypoints.TryGetValue(id, out var w))
            {
                print($"  {name}: sin datos (¿no existe como WAYPOINT o la región no es esa?).");
                continue;
            }
            print($"  {name}: tipo {w.Type}, {w.Latitude.ToString("F4", inv)} {w.Longitude.ToString("F4", inv)}, N_ROUTES {w.RouteCount}");
            foreach (var r in routes.GetValueOrDefault(id) ?? [])
            {
                print($"    aerovía {r.Name} (tipo {r.Type}): siguiente {r.NextIcao}/{r.NextRegion} ('{(char)r.NextType}') " +
                      $"{r.NextLatitude.ToString("F4", inv)} {r.NextLongitude.ToString("F4", inv)} mín {r.NextAltitudeM.ToString("F0", inv)} m · " +
                      $"anterior {r.PrevIcao}/{r.PrevRegion} ('{(char)r.PrevType}')");
            }
        }

        // V-24: recorrer una aerovía hacia adelante, un pedido tras otro, y medir.
        var start = asked.Keys.Select(id => routes.GetValueOrDefault(id)?.FirstOrDefault()).FirstOrDefault(r => r is { NextIcao.Length: > 0 });
        if (start is { } first)
        {
            var airway = first.Name;
            var (icao, region) = (first.NextIcao, first.NextRegion);
            var hops = 0;
            watch.Restart();
            var path = new List<string>();
            while (hops < 60 && icao.Length > 0)
            {
                var id = next++;
                sim.RequestFacilityData(Def.Waypoint, (Req)id, icao, region);
                Pump(5, () => ended.Contains(id));
                path.Add(icao);
                hops++;
                var r = routes.GetValueOrDefault(id)?.FirstOrDefault(x => x.Name == airway);
                if (r is not { } rr || rr.NextIcao.Length == 0 || path.Contains(rr.NextIcao))
                    break;
                (icao, region) = (rr.NextIcao, rr.NextRegion);
            }
            var s = watch.Elapsed.TotalSeconds;
            print($"V-24 · aerovía {airway}: {hops} pedidos seguidos en {s.ToString("0.00", inv)} s " +
                  $"({(hops / Math.Max(0.001, s)).ToString("0.0", inv)} por segundo): {string.Join(" → ", path)}");
        }
        else
        {
            print("V-24 · ningún fijo trajo aerovías: no se pudo medir.");
        }
        sim.Dispose();
        return 0;
    }
}
