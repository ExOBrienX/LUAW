using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Simulation;

namespace SimATC.Sim;

/// <summary>
/// "Aplicar OFP al avión" (ADR-079, V-44): combustible por tanque, peso por estación de carga y los datos de ATC del avión
/// del piloto, con SimVars oficiales escribibles (SDK MSFS 2024: FUEL TANK … QUANTITY, PAYLOAD STATION WEIGHT:n, ATC ID,
/// ATC AIRLINE, ATC FLIGHT NUMBER). Primero lee estaciones, capacidades y peso por galón; después escribe.
/// </summary>
public sealed partial class SimConnectBridge
{
    private const uint LoadInfoDef = 200, FuelLeftDef = 201, FuelRightDef = 202, FuelCenterDef = 203, AtcIdDef = 204,
        AtcAirlineDef = 205, AtcFlightDef = 206, StationDefBase = 220, LoadInfoRequest = 40;

    private bool _loadDefsReady;
    private TaskCompletionSource<LoadInfo>? _loadInfoTcs;

    private enum RawDef : uint { }

    private enum RawRequest : uint { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct LoadInfo
    {
        public double StationCount, LeftCapGal, RightCapGal, CenterCapGal, LbsPerGal;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N2;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N3;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N4;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N5;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N6;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N7;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N8;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N9;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N10;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N11;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N12;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N13;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N14;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string N15;

        public readonly string[] Names => [N1, N2, N3, N4, N5, N6, N7, N8, N9, N10, N11, N12, N13, N14, N15];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct Str32
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct Str64
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct Str8
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Value;
    }

    private void EnsureLoadDefinitions(SimConnect sim)
    {
        if (_loadDefsReady)
            return;
        _loadDefsReady = true;
        var info = (RawDef)LoadInfoDef;
        sim.AddToDataDefinition(info, "PAYLOAD STATION COUNT", "number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(info, "FUEL TANK LEFT MAIN CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(info, "FUEL TANK RIGHT MAIN CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(info, "FUEL TANK CENTER CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(info, "FUEL WEIGHT PER GALLON", "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        for (var i = 1; i <= 15; i++)
            sim.AddToDataDefinition(info, $"PAYLOAD STATION NAME:{i}", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<LoadInfo>(info);
        void Double(uint def, string name, string unit)
        {
            sim.AddToDataDefinition((RawDef)def, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sim.RegisterDataDefineStruct<double>((RawDef)def);
        }
        Double(FuelLeftDef, "FUEL TANK LEFT MAIN QUANTITY", "gallons");
        Double(FuelRightDef, "FUEL TANK RIGHT MAIN QUANTITY", "gallons");
        Double(FuelCenterDef, "FUEL TANK CENTER QUANTITY", "gallons");
        for (var i = 1; i <= 15; i++)
            Double(StationDefBase + (uint)i, $"PAYLOAD STATION WEIGHT:{i}", "pounds");
        sim.AddToDataDefinition((RawDef)AtcIdDef, "ATC ID", null, SIMCONNECT_DATATYPE.STRING32, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Str32>((RawDef)AtcIdDef);
        sim.AddToDataDefinition((RawDef)AtcAirlineDef, "ATC AIRLINE", null, SIMCONNECT_DATATYPE.STRING64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Str64>((RawDef)AtcAirlineDef);
        sim.AddToDataDefinition((RawDef)AtcFlightDef, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING8, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Str8>((RawDef)AtcFlightDef);
    }

    /// <inheritdoc />
    public async Task<string> ApplyLoadAsync(AircraftLoadRequest r, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<LoadInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadInfoTcs = tcs;
        RunOnSimThread(sim =>
        {
            EnsureLoadDefinitions(sim);
            sim.RequestDataOnSimObject((RawRequest)LoadInfoRequest, (RawDef)LoadInfoDef, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        });
        var info = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var plan = AircraftLoadPlanner.Plan(r, (int)info.StationCount, info.Names, info.LeftCapGal, info.RightCapGal, info.CenterCapGal,
            info.LbsPerGal);
        RunOnSimThread(sim =>
        {
            var user = SimConnect.SIMCONNECT_OBJECT_ID_USER;
            sim.SetDataOnSimObject((RawDef)FuelLeftDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, plan.LeftGal);
            sim.SetDataOnSimObject((RawDef)FuelRightDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, plan.RightGal);
            sim.SetDataOnSimObject((RawDef)FuelCenterDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, plan.CenterGal);
            for (var i = 0; i < plan.StationLbs.Count; i++)
            {
                if (plan.StationLbs[i] is { } lbs)
                    sim.SetDataOnSimObject((RawDef)(StationDefBase + (uint)(i + 1)), user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, lbs);
            }
            if (r.AtcId is { Length: > 0 } id)
                sim.SetDataOnSimObject((RawDef)AtcIdDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new Str32 { Value = id.Length > 10 ? id[..10] : id });
            if (r.Airline is { Length: > 0 } al)
                sim.SetDataOnSimObject((RawDef)AtcAirlineDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new Str64 { Value = al.Length > 50 ? al[..50] : al });
            if (r.FlightNumber is { Length: > 0 } fn)
                sim.SetDataOnSimObject((RawDef)AtcFlightDef, user, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new Str8 { Value = fn.Length > 6 ? fn[..6] : fn });
        });
        return plan.Summary;
    }

    /// <summary>Respuesta a la lectura previa de <see cref="ApplyLoadAsync"/> (desde OnRecvSimobjectData).</summary>
    private bool OnLoadInfoData(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID != LoadInfoRequest)
            return false;
        _loadInfoTcs?.TrySetResult((LoadInfo)data.dwData[0]);
        return true;
    }
}
