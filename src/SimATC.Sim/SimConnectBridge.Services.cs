using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Sim;

/// <summary>
/// Vida en tierra con lo del propio simulador (V-15): aviones "Parked ATC" con puesto reservado y pasarela, relevo a
/// un avión sin ATC para salir, y remolcador de pushback.
/// </summary>
public sealed partial class SimConnectBridge
{
    /// <summary>El simulador coloca al Parked ATC en su puesto un momento después de crearlo.</summary>
    private static readonly TimeSpan ParkedAtcSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PoseReadTimeout = TimeSpan.FromSeconds(5);

    // Remolcador de MSFS 2024 (verificado con EnumerateSimObjectsAndLiveries, tipo GROUND). Su origen es el punto de
    // enganche con la rueda de nariz y su frente es el lado que toca al avión (documentación de Pushback).
    private const string TugTitle = "ASO_Pushback_Blue";
    private const string TugLivery = "BLUE";

    private readonly Dictionary<uint, PendingAtcSpawn> _pendingAtc = [];
    private readonly Dictionary<uint, PoseRead> _poseReads = [];
    private readonly Dictionary<uint, Tug> _tugs = [];
    private readonly Dictionary<uint, uint> _tugRequests = [];
    private bool _tugsUnavailable;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ObjectPoseData
    {
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double HeadingTrueDeg;
    }

    public Task<ParkedAtcResult> SpawnParkedAtcAsync(ParkedAtcRequest request, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<ParkedAtcResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Enqueue(sim =>
        {
            var id = _nextSpawnRequestId++;
            _pendingAtc[id] = new PendingAtcSpawn(request, tcs, Deadline(SpawnTimeout));
            var tail = request.TailNumber.Length > 12 ? request.TailNumber[..12] : request.TailNumber;
            sim.AICreateParkedATCAircraft_EX1(request.Title, "", tail, request.AirportIcao, (RequestId)id);
        });
        return tcs.Task;
    }

    public async Task<uint> HandOverAsync(uint parkedAtcId, ParkedSpawnRequest replacement, CancellationToken ct)
    {
        // Primero existe el nuevo y recién entonces se borra el viejo: el avión nunca desaparece a la vista.
        var id = await SpawnParkedAsync(replacement, ct).ConfigureAwait(false);
        // Sale enseguida: el estado de estacionado repetido apagaría las luces que pone la salida.
        Enqueue(_ => _parkedStateQueue.RemoveAll(e => e.ObjectId == id));
        Remove(parkedAtcId);
        return id;
    }

    public void SetTug(uint aircraftId, TugPose? pose)
    {
        var p = pose;
        RunOnSimThread(sim =>
        {
            if (p is not { } tp)
            {
                RemoveTug(sim, aircraftId);
                return;
            }
            if (!_tugs.TryGetValue(aircraftId, out var tug))
            {
                if (_tugsUnavailable || !_injected.ContainsKey(aircraftId))
                    return;
                var request = _nextSpawnRequestId++;
                _tugs[aircraftId] = new Tug { RequestId = request, DeadlineTicks = Deadline(SpawnTimeout) };
                _tugRequests[request] = aircraftId;
                sim.AICreateSimulatedObject_EX1(TugTitle, TugLivery, TugInit(tp), (RequestId)request);
                return;
            }
            if (tug.ObjectId != 0)
                sim.SetDataOnSimObject(DefinitionId.InitialPosition, tug.ObjectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, TugInit(tp));
        });
    }

    private static SIMCONNECT_DATA_INITPOSITION TugInit(in TugPose p) => new()
    {
        Latitude = p.Position.LatDeg,
        Longitude = p.Position.LonDeg,
        Altitude = p.AltitudeFt,
        Heading = p.HeadingTrueDeg,
        OnGround = 1,
        Airspeed = 0,
    };

    private void RemoveTug(SimConnect sim, uint aircraftId)
    {
        if (_tugs.Remove(aircraftId, out var tug) && tug.ObjectId != 0)
            sim.AIRemoveObject(tug.ObjectId, RequestId.RemoveObject);
    }

    private void RemoveAllTugs(SimConnect sim)
    {
        foreach (var aircraftId in _tugs.Keys.ToList())
            RemoveTug(sim, aircraftId);
        _tugRequests.Clear();
    }

    private static void RegisterServices(SimConnect sim)
    {
        sim.AddToDataDefinition(DefinitionId.ObjectPose, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DefinitionId.ObjectPose, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DefinitionId.ObjectPose, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<ObjectPoseData>(DefinitionId.ObjectPose);
    }

    /// <summary>Respuestas de creación que son de esta parte (Parked ATC o remolcador). false si no es nuestra.</summary>
    private bool OnAssignedService(SimConnect sim, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (_pendingAtc.Remove(data.dwRequestID, out var atc))
        {
            _injected[data.dwObjectID] = 0;
            ApplyParkedState(sim, data.dwObjectID, atc.Request.EngineCount);
            var now = Stopwatch.GetTimestamp();
            foreach (var delay in ParkedStateDelays)
                _parkedStateQueue.Add((data.dwObjectID, atc.Request.EngineCount, now + (long)(delay.TotalSeconds * Stopwatch.Frequency)));
            _poseReads[data.dwObjectID] = new PoseRead(atc.Completion, now + (long)(ParkedAtcSettle.TotalSeconds * Stopwatch.Frequency));
            return true;
        }
        if (_tugRequests.Remove(data.dwRequestID, out var aircraftId))
        {
            if (_tugs.TryGetValue(aircraftId, out var tug) && tug.RequestId == data.dwRequestID)
            {
                tug.ObjectId = data.dwObjectID;
                sim.AIReleaseControl(data.dwObjectID, RequestId.ReleaseControl); // que no lo maneje la IA del simulador
            }
            else
            {
                sim.AIRemoveObject(data.dwObjectID, RequestId.RemoveObject); // ya no se necesitaba
            }
            return true;
        }
        return false;
    }

    private void OnObjectPose(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (!_poseReads.Remove(data.dwObjectID, out var read))
            return;
        var p = (ObjectPoseData)data.dwData[0];
        read.Completion.TrySetResult(new ParkedAtcResult(data.dwObjectID, new GeoPoint(p.LatitudeDeg, p.LongitudeDeg), p.HeadingTrueDeg));
    }

    private void ServiceServices(SimConnect sim, long now)
    {
        if (_pendingAtc.Count > 0)
        {
            foreach (var (id, pending) in _pendingAtc.ToList())
            {
                if (now < pending.DeadlineTicks)
                    continue;
                _pendingAtc.Remove(id);
                _log(Tr.F("El simulador no creó el Parked ATC '{0}' en {1:0} s.", pending.Request.Title, SpawnTimeout.TotalSeconds));
                pending.Completion.TrySetException(new TimeoutException($"No se pudo crear '{pending.Request.Title}' como Parked ATC."));
            }
        }
        if (_poseReads.Count > 0)
        {
            foreach (var (objectId, read) in _poseReads.ToList())
            {
                if (now < read.DueTicks)
                    continue;
                if (!read.Requested)
                {
                    sim.RequestDataOnSimObject(RequestId.ObjectPose, DefinitionId.ObjectPose, objectId, SIMCONNECT_PERIOD.ONCE,
                        SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    _poseReads[objectId] = read with { Requested = true, DueTicks = now + (long)(PoseReadTimeout.TotalSeconds * Stopwatch.Frequency) };
                }
                else
                {
                    _poseReads.Remove(objectId);
                    read.Completion.TrySetException(new TimeoutException("El simulador no informó dónde quedó el avión."));
                }
            }
        }
        if (_tugs.Count > 0)
        {
            foreach (var (aircraftId, tug) in _tugs.ToList())
            {
                if (tug.ObjectId != 0 || now < tug.DeadlineTicks)
                    continue;
                _tugs.Remove(aircraftId);
                _tugRequests.Remove(tug.RequestId);
                _tugsUnavailable = true;
                _log(Tr.F("El simulador no creó el remolcador '{0}': los pushback siguen sin él.", TugTitle));
            }
        }
    }

    private void ResetServices(Exception error)
    {
        foreach (var pending in _pendingAtc.Values)
            pending.Completion.TrySetException(error);
        _pendingAtc.Clear();
        foreach (var read in _poseReads.Values)
            read.Completion.TrySetException(error);
        _poseReads.Clear();
        _tugs.Clear();
        _tugRequests.Clear();
    }

    private sealed record PendingAtcSpawn(ParkedAtcRequest Request, TaskCompletionSource<ParkedAtcResult> Completion, long DeadlineTicks);

    private sealed record PoseRead(TaskCompletionSource<ParkedAtcResult> Completion, long DueTicks, bool Requested = false);

    private sealed class Tug
    {
        public uint RequestId;
        public uint ObjectId;
        public long DeadlineTicks;
    }
}
