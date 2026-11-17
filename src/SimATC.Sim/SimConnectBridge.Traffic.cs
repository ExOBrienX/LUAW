using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Sim;

/// <summary>
/// Inyección de aviones (etapa 2): lista de modelos, crear estacionados, borrar. Todo se ejecuta en el hilo de
/// SimConnect a través de <see cref="_commands"/>.
/// </summary>
public sealed partial class SimConnectBridge
{
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EnumerateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// El estado de estacionado se aplica apenas el simulador confirma el avión y se repite: la API de creación no
    /// permite crearlo apagado y el modelo puede seguir arrancando motores o luces mientras se inicializa.
    /// </summary>
    private static readonly TimeSpan[] ParkedStateDelays =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    private const uint SpawnRequestBase = 2000;

    /// <summary>Eventos de luces (V-09: las SimVar LIGHT * no tienen efecto en aviones IA; los eventos sí).</summary>
    private enum LightEvent { Beacon, Nav, Strobe, Landing, Taxi, Logo, Wing, Recognition }

    /// <summary>
    /// Estacionado = todas las luces exteriores apagadas, también NAV: algunos modelos FSLTL no responden a NAV y
    /// así todos se ven iguales. La escala de la etapa 3 (RF-21b) las enciende cuando corresponde.
    /// </summary>
    private static readonly (LightEvent Event, string Name, uint ParkedValue)[] LightEvents =
    [
        (LightEvent.Beacon, "BEACON_LIGHTS_SET", 0),
        (LightEvent.Nav, "NAV_LIGHTS_SET", 0),
        (LightEvent.Strobe, "STROBES_SET", 0),
        (LightEvent.Landing, "LANDING_LIGHTS_SET", 0),
        (LightEvent.Taxi, "TAXI_LIGHTS_SET", 0),
        (LightEvent.Logo, "LOGO_LIGHTS_SET", 0),
        (LightEvent.Wing, "WING_LIGHTS_SET", 0),
        (LightEvent.Recognition, "RECOGNITION_LIGHTS_SET", 0),
    ];

    private enum EventGroup : uint { HighestPriority = 1 } // SIMCONNECT_GROUP_PRIORITY_HIGHEST

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct EngineCombustion
    {
        public double Value;
    }

    private readonly ConcurrentQueue<Action<SimConnect>> _commands = new();
    private readonly ConcurrentDictionary<uint, byte> _injected = new();
    private readonly Dictionary<uint, PendingSpawn> _pendingSpawns = [];
    private readonly List<(uint ObjectId, int Engines, long DueTicks)> _parkedStateQueue = [];
    private PendingEnumeration? _enumeration;
    private uint _nextSpawnRequestId = SpawnRequestBase;

    public int InjectedCount => _injected.Count;

    public Task<IReadOnlyList<string>> EnumerateAircraftTitlesAsync(CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Enqueue(sim =>
        {
            _enumeration?.Completion.TrySetException(new InvalidOperationException("Reemplazado por un pedido nuevo."));
            _enumeration = new PendingEnumeration(tcs, Deadline(EnumerateTimeout));
            sim.EnumerateSimObjectsAndLiveries(RequestId.EnumerateModels, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
        });
        return tcs.Task;
    }

    public Task<uint> SpawnParkedAsync(ParkedSpawnRequest request, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Enqueue(sim =>
        {
            var id = _nextSpawnRequestId++;
            _pendingSpawns[id] = new PendingSpawn(request, tcs, Deadline(SpawnTimeout));
            var init = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = request.Position.LatDeg,
                Longitude = request.Position.LonDeg,
                Altitude = request.ElevationFt,
                Heading = request.HeadingTrueDeg,
                OnGround = 1,
                Airspeed = 0,
            };
            var tail = request.TailNumber.Length > 12 ? request.TailNumber[..12] : request.TailNumber;
            sim.AICreateNonATCAircraft_EX1(request.Title, "", tail, init, (RequestId)id);
        });
        return tcs.Task;
    }

    public Task<uint> SpawnAirborneAsync(AirborneSpawnRequest request, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Enqueue(sim =>
        {
            var id = _nextSpawnRequestId++;
            // Sin estado de estacionado: el motor enciende motores y luces en el primer cuadro.
            _pendingSpawns[id] = new PendingSpawn(new ParkedSpawnRequest(request.Title, request.TailNumber, request.Position,
                request.AltitudeFt, request.HeadingTrueDeg, request.EngineCount), tcs, Deadline(SpawnTimeout), Airborne: true);
            var init = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = request.Position.LatDeg,
                Longitude = request.Position.LonDeg,
                Altitude = request.AltitudeFt,
                Heading = request.HeadingTrueDeg,
                OnGround = 0,
                Airspeed = (uint)Math.Max(0, Math.Round(request.AirspeedKt)),
            };
            var tail = request.TailNumber.Length > 12 ? request.TailNumber[..12] : request.TailNumber;
            sim.AICreateNonATCAircraft_EX1(request.Title, "", tail, init, (RequestId)id);
        });
        return tcs.Task;
    }

    public void Remove(uint objectId) => Enqueue(sim =>
    {
        RemoveTug(sim, objectId);
        if (_injected.TryRemove(objectId, out _))
            sim.AIRemoveObject(objectId, RequestId.RemoveObject);
    });

    public void RemoveAll() => Enqueue(RemoveAllNow);

    private void RemoveAllNow(SimConnect sim)
    {
        RemoveAllTugs(sim);
        foreach (var id in _injected.Keys)
        {
            if (_injected.TryRemove(id, out _))
                sim.AIRemoveObject(id, RequestId.RemoveObject);
        }
        _parkedStateQueue.Clear();
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("No hay conexión con el simulador.");
    }

    private void Enqueue(Action<SimConnect> command)
    {
        _commands.Enqueue(command);
        _messageEvent.Set(); // despertar el hilo de SimConnect
    }

    private static long Deadline(TimeSpan timeout) =>
        Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Definiciones y eventos de tráfico; se registran en cada conexión.</summary>
    private static void RegisterTraffic(SimConnect sim)
    {
        for (var i = 1; i <= 4; i++)
        {
            var def = DefinitionId.EngineCombustion1 + (uint)(i - 1);
            sim.AddToDataDefinition(def, $"GENERAL ENG COMBUSTION:{i}", "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f,
                SimConnect.SIMCONNECT_UNUSED);
            sim.RegisterDataDefineStruct<EngineCombustion>(def);
        }
        foreach (var (ev, name, _) in LightEvents)
            sim.MapClientEventToSimEvent(ev, name);
        RegisterMotion(sim);
        RegisterServices(sim);
        RegisterRadio(sim);
        RegisterTug(sim);
    }

    private void HookTrafficEvents(SimConnect sim)
    {
        sim.OnRecvAssignedObjectId += OnRecvAssignedObjectId;
        sim.OnRecvEnumerateSimobjectAndLiveryList += OnRecvEnumerateSimobjectAndLiveryList;
        HookMotionEvents(sim);
    }

    /// <summary>Corre en cada vuelta del hilo de SimConnect con conexión activa.</summary>
    private void ServiceTraffic(SimConnect sim, long now)
    {
        while (_commands.TryDequeue(out var command))
            command(sim);
        ServiceServices(sim, now);

        if (_pendingSpawns.Count > 0)
        {
            foreach (var (id, pending) in _pendingSpawns.ToList())
            {
                if (now < pending.DeadlineTicks)
                    continue;
                _pendingSpawns.Remove(id);
                _log(Tr.F("El simulador no creó '{0}' en {1:0} s.", pending.Request.Title, SpawnTimeout.TotalSeconds));
                pending.Completion.TrySetException(new TimeoutException($"No se pudo crear '{pending.Request.Title}'."));
            }
        }

        if (_enumeration is { } en && now >= en.DeadlineTicks)
        {
            _enumeration = null;
            en.Completion.TrySetException(new TimeoutException("El simulador no entregó la lista de modelos."));
        }

        for (var i = _parkedStateQueue.Count - 1; i >= 0; i--)
        {
            var (objectId, engines, due) = _parkedStateQueue[i];
            if (now < due)
                continue;
            _parkedStateQueue.RemoveAt(i);
            if (_injected.ContainsKey(objectId))
                ApplyParkedState(sim, objectId, engines);
        }
    }

    /// <summary>Motores apagados (SimVar) y luces de estacionamiento (eventos): V-09.</summary>
    private static void ApplyParkedState(SimConnect sim, uint objectId, int engines)
    {
        var count = engines is >= 1 and <= 4 ? engines : 2;
        for (var i = 1; i <= count; i++)
            sim.SetDataOnSimObject(DefinitionId.EngineCombustion1 + (uint)(i - 1), objectId,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, new EngineCombustion { Value = 0 });
        foreach (var (ev, _, value) in LightEvents)
            sim.TransmitClientEvent(objectId, ev, value, EventGroup.HighestPriority,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>Al perder la conexión el simulador ya borró nuestros objetos (V-12): solo se limpia el registro.</summary>
    private void ResetTraffic(Exception error)
    {
        _injected.Clear();
        _parkedStateQueue.Clear();
        ResetServices(error);
        foreach (var pending in _pendingSpawns.Values)
            pending.Completion.TrySetException(error);
        _pendingSpawns.Clear();
        _enumeration?.Completion.TrySetException(error);
        _enumeration = null;
        while (_commands.TryDequeue(out _))
        {
        }
    }

    private void OnRecvAssignedObjectId(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (OnAssignedService(sender, data) || !_pendingSpawns.Remove(data.dwRequestID, out var pending))
            return;
        _injected[data.dwObjectID] = 0;
        if (pending.Airborne)
        {
            pending.Completion.TrySetResult(data.dwObjectID);
            return;
        }
        ApplyParkedState(sender, data.dwObjectID, pending.Request.EngineCount); // en el acto, y se repite abajo
        var now = Stopwatch.GetTimestamp();
        foreach (var delay in ParkedStateDelays)
            _parkedStateQueue.Add((data.dwObjectID, pending.Request.EngineCount,
                now + (long)(delay.TotalSeconds * Stopwatch.Frequency)));
        pending.Completion.TrySetResult(data.dwObjectID);
    }

    private void OnRecvEnumerateSimobjectAndLiveryList(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_SIMOBJECT_AND_LIVERY_LIST data)
    {
        if (_enumeration is not { } en || data.dwRequestID != (uint)RequestId.EnumerateModels)
            return;
        foreach (var item in data.rgData)
        {
            if (item is SIMCONNECT_ENUMERATE_SIMOBJECT_LIVERY entry && !string.IsNullOrWhiteSpace(entry.AircraftTitle))
                en.Titles.Add(entry.AircraftTitle.Trim());
        }
        if (data.dwEntryNumber + 1 >= data.dwOutOf)
        {
            _enumeration = null;
            en.Completion.TrySetResult(en.Titles.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    private sealed record PendingSpawn(ParkedSpawnRequest Request, TaskCompletionSource<uint> Completion, long DeadlineTicks,
        bool Airborne = false);

    private sealed record PendingEnumeration(TaskCompletionSource<IReadOnlyList<string>> Completion, long DeadlineTicks)
    {
        public List<string> Titles { get; } = [];
    }
}
