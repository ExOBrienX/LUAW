using SimATC.Core.Text;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.Sim;

/// <summary>
/// Todos los fijos del mundo (ADR-062): <c>SimConnect_RequestAllFacilities</c> con WAYPOINT, VOR y NDB devuelve la lista entera
/// "no matter the distance" (SDK 1.8.1, doc de la función), en varios paquetes (<c>dwEntryNumber</c>/<c>dwOutOf</c>). Cada
/// elemento trae Ident, Region, Latitude y Longitude (<c>SIMCONNECT_DATA_FACILITY_WAYPOINT</c> y sus derivadas).
/// </summary>
public sealed partial class SimConnectBridge
{
    private const uint NavWaypointsRequestId = 21;
    private const uint NavVorRequestId = 22;
    private const uint NavNdbRequestId = 23;
    private static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(120);

    private TaskCompletionSource<IReadOnlyList<NavFix>>? _nav;
    private readonly List<NavFix> _navBuffer = [];
    private readonly HashSet<uint> _navDone = [];

    /// <summary>Todos los fijos del simulador (puede tardar decenas de segundos y pesa decenas de MB en memoria mientras llega).</summary>
    public Task<IReadOnlyList<NavFix>> RequestAllNavaidsAsync(CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException(Tr.T("No hay conexión con el simulador."));
        var tcs = new TaskCompletionSource<IReadOnlyList<NavFix>>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnSimThread(sim =>
        {
            if (_nav is { } pending)
            {
                pending.Task.ContinueWith(t => tcs.TrySetResult(t.IsCompletedSuccessfully ? t.Result : []), TaskScheduler.Default);
                return;
            }
            _nav = tcs;
            _navBuffer.Clear();
            _navDone.Clear();
            sim.RequestAllFacilities(SIMCONNECT_FACILITY_LIST_TYPE.WAYPOINT, (RequestIdFor)NavWaypointsRequestId);
            sim.RequestAllFacilities(SIMCONNECT_FACILITY_LIST_TYPE.VOR, (RequestIdFor)NavVorRequestId);
            sim.RequestAllFacilities(SIMCONNECT_FACILITY_LIST_TYPE.NDB, (RequestIdFor)NavNdbRequestId);
        });
        _messageEvent.Set();
        _ = Task.Delay(NavTimeout, ct).ContinueWith(_ =>
        {
            // Sin respuesta completa: se devuelve lo que haya llegado (en el hilo de SimConnect).
            RunOnSimThread(_ =>
            {
                if (_nav == tcs)
                    FinishNav($"Fijos del mundo: sin respuesta completa en {NavTimeout.TotalSeconds:0} s.");
            });
            _messageEvent.Set();
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    private void FinishNav(string? message = null)
    {
        if (_nav is not { } tcs)
            return;
        _nav = null;
        if (message is not null)
            _log(message);
        tcs.TrySetResult([.. _navBuffer]);
        _navBuffer.Clear();
    }

    private void NavPacket(uint requestId, uint entry, uint outOf)
    {
        if (entry + 1 >= outOf)
            _navDone.Add(requestId);
        if (_navDone.Count == 3)
            FinishNav();
    }

    private void OnRecvWaypointList(SimConnect sender, SIMCONNECT_RECV_WAYPOINT_LIST data)
    {
        if (data.dwRequestID != NavWaypointsRequestId || _nav is null)
            return;
        foreach (var item in data.rgData)
        {
            if (item is SIMCONNECT_DATA_FACILITY_WAYPOINT w && !string.IsNullOrWhiteSpace(w.Ident))
                _navBuffer.Add(new NavFix(w.Ident.Trim(), w.Region?.Trim() ?? "", new GeoPoint(w.Latitude, w.Longitude), NavKind.Waypoint));
        }
        NavPacket(data.dwRequestID, data.dwEntryNumber, data.dwOutOf);
    }

    private void OnRecvVorList(SimConnect sender, SIMCONNECT_RECV_VOR_LIST data)
    {
        if (data.dwRequestID != NavVorRequestId || _nav is null)
            return;
        foreach (var item in data.rgData)
        {
            if (item is SIMCONNECT_DATA_FACILITY_VOR v && !string.IsNullOrWhiteSpace(v.Ident))
                _navBuffer.Add(new NavFix(v.Ident.Trim(), v.Region?.Trim() ?? "", new GeoPoint(v.Latitude, v.Longitude), NavKind.Vor));
        }
        NavPacket(data.dwRequestID, data.dwEntryNumber, data.dwOutOf);
    }

    private void OnRecvNdbList(SimConnect sender, SIMCONNECT_RECV_NDB_LIST data)
    {
        if (data.dwRequestID != NavNdbRequestId || _nav is null)
            return;
        foreach (var item in data.rgData)
        {
            if (item is SIMCONNECT_DATA_FACILITY_NDB n && !string.IsNullOrWhiteSpace(n.Ident))
                _navBuffer.Add(new NavFix(n.Ident.Trim(), n.Region?.Trim() ?? "", new GeoPoint(n.Latitude, n.Longitude), NavKind.Ndb));
        }
        NavPacket(data.dwRequestID, data.dwEntryNumber, data.dwOutOf);
    }

    private void RegisterNavIndex(SimConnect sim)
    {
        sim.OnRecvWaypointList += OnRecvWaypointList;
        sim.OnRecvVorList += OnRecvVorList;
        sim.OnRecvNdbList += OnRecvNdbList;
    }
}
