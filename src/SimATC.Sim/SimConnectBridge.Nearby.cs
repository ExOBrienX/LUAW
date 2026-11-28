using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Sim;

/// <summary>
/// Aeropuertos cercanos al piloto: <c>SimConnect_RequestFacilitiesList_EX1</c> con <c>SIMCONNECT_FACILITY_LIST_TYPE_AIRPORT</c>
/// devuelve los del caché de la "burbuja de realidad" (alrededor del avión del piloto), con ICAO y posición
/// (SDK 1.8.1, verificado en la doc; ADR-030). La lista puede llegar en varios paquetes (<c>dwEntryNumber</c>/<c>dwOutOf</c>).
/// </summary>
public sealed partial class SimConnectBridge
{
    private const uint NearbyRequestId = 17;
    private static readonly TimeSpan NearbyTimeout = TimeSpan.FromSeconds(10);
    private TaskCompletionSource<IReadOnlyList<NearbyAirport>>? _nearby;
    private List<NearbyAirport> _nearbyBuffer = [];

    public Task<IReadOnlyList<NearbyAirport>> RequestNearbyAirportsAsync(CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException(Tr.T("No hay conexión con el simulador."));
        var tcs = new TaskCompletionSource<IReadOnlyList<NearbyAirport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnSimThread(sim =>
        {
            if (_nearby is { } pending)
            {
                // Ya hay un pedido en curso: este espera el mismo resultado.
                pending.Task.ContinueWith(t => tcs.TrySetResult(t.IsCompletedSuccessfully ? t.Result : []), TaskScheduler.Default);
                return;
            }
            _nearby = tcs;
            _nearbyBuffer = [];
            sim.RequestFacilitiesList_EX1(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, (RequestIdFor)NearbyRequestId);
        });
        _messageEvent.Set();
        var timeout = Task.Delay(NearbyTimeout, ct).ContinueWith(_ =>
        {
            // Sin respuesta (o cancelado): se devuelve lo que haya llegado.
            if (tcs.TrySetResult([.. _nearbyBuffer]))
                _log(Tr.T("Lista de aeropuertos cercanos: sin respuesta completa en 10 s."));
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    private enum RequestIdFor : uint { }

    private void OnRecvAirportList(SimConnect sender, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if (data.dwRequestID != NearbyRequestId || _nearby is not { } tcs)
            return;
        foreach (var item in data.rgData)
        {
            if (item is SIMCONNECT_DATA_FACILITY_AIRPORT a && !string.IsNullOrWhiteSpace(a.Ident))
                _nearbyBuffer.Add(new NearbyAirport(a.Ident.Trim(), new GeoPoint(a.Latitude, a.Longitude), a.Altitude));
        }
        if (data.dwEntryNumber + 1 >= data.dwOutOf)
        {
            _nearby = null;
            tcs.TrySetResult([.. _nearbyBuffer]);
        }
    }

    private void RegisterNearby(SimConnect sim) => sim.OnRecvAirportList += OnRecvAirportList;
}
