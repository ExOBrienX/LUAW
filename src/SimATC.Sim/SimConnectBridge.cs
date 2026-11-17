using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Units;
using SimATC.Sim.Facilities;

using SimATC.Core.Text;

namespace SimATC.Sim;

/// <summary>
/// <see cref="ISimBridge"/> sobre el envoltorio administrado de SimConnect (ADR-001).
/// Todas las llamadas a SimConnect ocurren en un único hilo propio, que espera el event handle de SimConnect
/// (no se usa handle de ventana). Si MSFS no está o se cierra, reintenta cada 5 s (RF-01, RF-02).
/// Los pedidos de aeropuerto entran por una cola y se atienden de a uno en ese hilo.
/// </summary>
public sealed partial class SimConnectBridge : ISimBridge
{
    private const string ClientName = "LUAW";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200); // ~5 Hz (RF-05)
    private static readonly TimeSpan LoadInterval = TimeSpan.FromSeconds(2); // carga del piloto: cambia poco
    private static readonly TimeSpan AmbientInterval = TimeSpan.FromSeconds(1); // clima: muestras de viento para la media de 2 min y las ráfagas
    private static readonly TimeSpan FacilityTimeout = TimeSpan.FromSeconds(60);

    private enum DefinitionId : uint
    {
        UserAircraft, Airport, EngineCombustion1, EngineCombustion2, EngineCombustion3, EngineCombustion4,
        InitialPosition, ObjectAltitude, Throttle1, Throttle2, Throttle3, Throttle4, BodyMotion, ObjectPose, UserLoad, Ambient, UserRadios,
        PushbackYaw, Waypoint,
    }

    private enum RequestId : uint { UserAircraft, EnumerateModels = 10, RemoveObject = 11, ObjectAltitude = 12, ObjectPose = 13, ReleaseControl = 14, UserLoad = 15, Ambient = 16, UserRadios = 17, FacilityBase = 1000 }

    private readonly Action<string> _log;
    private readonly AutoResetEvent _messageEvent = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<FacilityRequest> _facilityQueue = new();
    private Thread? _thread;
    private SimConnect? _sim;
    private volatile bool _connected;
    private volatile string? _simVersion;
    private bool _quitReceived;
    private volatile bool _reconnectRequested;
    private readonly AutoResetEvent _retryNow = new(false);
    private bool _waitingLogged;
    private double _lastTransponderRaw = double.NaN;
    private FacilityRequest? _activeFacility;
    private uint _nextFacilityRequestId = (uint)RequestId.FacilityBase;

    /// <param name="log">Destino de los mensajes de diagnóstico (el logging completo llega con RNF-08).</param>
    public SimConnectBridge(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public bool IsConnected => _connected;

    public string? SimVersion => _simVersion;

    public event Action<bool>? ConnectionChanged;
    public event Action<UserAircraftState>? UserStateUpdated;

    public event Action<UserRadios>? UserRadiosUpdated;

    public event Action<UserAircraftLoad>? UserLoadUpdated;

    public event Action<AmbientWeather>? AmbientWeatherUpdated;

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("El puente ya está iniciado.");
        _thread = new Thread(Run) { Name = "SimConnect", IsBackground = true };
        _thread.Start();
    }

    public Task<AirportData> LoadAirportAsync(string icao, CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException("No hay conexión con el simulador.");

        var request = new FacilityRequest(icao.ToUpperInvariant());
        if (ct.CanBeCanceled)
            request.CancelRegistration = ct.Register(() => request.Completion.TrySetCanceled(ct));
        _facilityQueue.Enqueue(request);
        _messageEvent.Set(); // despertar el hilo de SimConnect
        return request.Completion.Task;
    }

    /// <summary>Corta la conexión (si la hay) y vuelve a intentar al instante. Los aviones creados se borran.</summary>
    public void Reconnect()
    {
        _log(Tr.T("Reconexión pedida por la app."));
        _reconnectRequested = true;
        _retryNow.Set();
        _messageEvent.Set();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _messageEvent.Set();
        // El hilo despierta a lo más cada PollInterval; si no termina a tiempo, no se liberan sus handles.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(5)))
        {
            _cts.Dispose();
            _messageEvent.Dispose();
        }
    }

    private void Run()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            _reconnectRequested = false;
            if (!TryOpen())
            {
                // Espera el próximo intento, o un "Reconectar" de la app.
                WaitHandle.WaitAny([ct.WaitHandle, _retryNow], RetryInterval);
                continue;
            }

            try
            {
                Pump(ct);
            }
            catch (COMException ex)
            {
                // MSFS se cerró sin avisar o la conexión se rompió.
                _log(Tr.F("Conexión perdida: {0}", ex.Message));
            }
            catch (Exception ex)
            {
                _log(Tr.F("Error inesperado en el hilo de SimConnect: {0}", ex));
            }
            finally
            {
                Close();
            }
        }
        FailPendingFacilities(new OperationCanceledException("El puente se cerró."));
    }

    private bool TryOpen()
    {
        try
        {
            var sim = new SimConnect(ClientName, IntPtr.Zero, 0, _messageEvent, 0);
            sim.OnRecvOpen += OnRecvOpen;
            sim.OnRecvQuit += OnRecvQuit;
            sim.OnRecvException += OnRecvException;
            sim.OnRecvSimobjectData += OnRecvSimobjectData;
            sim.OnRecvFacilityData += OnRecvFacilityData;
            sim.OnRecvFacilityDataEnd += OnRecvFacilityDataEnd;
            RegisterNearby(sim);
            RegisterNavIndex(sim);
            UserAircraftData.Register(sim, DefinitionId.UserAircraft);
            UserLoadData.Register(sim, DefinitionId.UserLoad);
            UserRadiosData.Register(sim, DefinitionId.UserRadios);
            AmbientData.Register(sim, DefinitionId.Ambient);
            AirportFacilityDefinition.Register(sim, DefinitionId.Airport);
            RegisterAirways(sim);
            RegisterTraffic(sim);
            HookTrafficEvents(sim);
            _sim = sim;
            _quitReceived = false;
            _waitingLogged = false;
            return true;
        }
        catch (COMException)
        {
            // MSFS no está corriendo (o todavía no acepta conexiones).
            if (!_waitingLogged)
            {
                _log(Tr.F("Esperando a MSFS (reintento cada {0:0} s)...", RetryInterval.TotalSeconds));
                _waitingLogged = true;
            }
            return false;
        }
    }

    private void Pump(CancellationToken ct)
    {
        var sim = _sim!;
        var pollTicks = (long)(PollInterval.TotalSeconds * Stopwatch.Frequency);
        var nextPoll = 0L;
        var loadTicks = (long)(LoadInterval.TotalSeconds * Stopwatch.Frequency);
        var nextLoad = 0L;
        var ambientTicks = (long)(AmbientInterval.TotalSeconds * Stopwatch.Frequency);
        var nextAmbient = 0L;

        while (!ct.IsCancellationRequested && !_quitReceived && !_reconnectRequested)
        {
            _messageEvent.WaitOne(PollInterval);
            sim.ReceiveMessage();

            if (!_connected)
                continue;

            var now = Stopwatch.GetTimestamp();
            if (now >= nextPoll)
            {
                sim.RequestDataOnSimObject(RequestId.UserAircraft, DefinitionId.UserAircraft,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.RequestDataOnSimObject(RequestId.UserRadios, DefinitionId.UserRadios,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                nextPoll = now + pollTicks;
            }
            if (now >= nextLoad)
            {
                sim.RequestDataOnSimObject(RequestId.UserLoad, DefinitionId.UserLoad,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                nextLoad = now + loadTicks;
            }
            if (now >= nextAmbient)
            {
                sim.RequestDataOnSimObject(RequestId.Ambient, DefinitionId.Ambient,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                nextAmbient = now + ambientTicks;
            }

            ServiceFacilities(sim, now);
            ServiceWaypoints(now);
            ServiceTraffic(sim, now);
        }

        // Cierre o reconexión pedidos por la app: borrar nuestros aviones antes de soltar la conexión (RF-03).
        if ((ct.IsCancellationRequested || _reconnectRequested) && _connected)
            RemoveAllNow(sim);
    }

    /// <summary>Inicia el siguiente pedido de aeropuerto o corta el activo si pasó su tiempo límite.</summary>
    private void ServiceFacilities(SimConnect sim, long now)
    {
        if (_activeFacility is { } active)
        {
            if (now < active.DeadlineTicks)
                return;
            _log(Tr.F("Sin respuesta del simulador para {0} tras {1:0} s.", active.Icao, FacilityTimeout.TotalSeconds));
            active.Completion.TrySetException(new TimeoutException($"Sin respuesta del simulador para {active.Icao}."));
            active.Dispose();
            _activeFacility = null;
        }

        while (_facilityQueue.TryDequeue(out var next))
        {
            if (next.Completion.Task.IsCompleted) // cancelado mientras esperaba
            {
                next.Dispose();
                continue;
            }
            next.RequestId = _nextFacilityRequestId++;
            next.DeadlineTicks = now + (long)(FacilityTimeout.TotalSeconds * Stopwatch.Frequency);
            next.Stopwatch.Start();
            _activeFacility = next;
            sim.RequestFacilityData(DefinitionId.Airport, (RequestId)next.RequestId, next.Icao, "");
            return;
        }
    }

    private void Close()
    {
        var sim = _sim;
        _sim = null;
        try
        {
            sim?.Dispose();
        }
        catch (COMException)
        {
            // La conexión ya estaba rota.
        }
        _simVersion = null;
        SetConnected(false);
        FailPendingFacilities(new InvalidOperationException("Se perdió la conexión con el simulador."));
        FailPendingWaypoints();
        ResetTraffic(new InvalidOperationException("Se perdió la conexión con el simulador."));
    }

    private void FailPendingFacilities(Exception error)
    {
        if (_activeFacility is { } active)
        {
            active.Completion.TrySetException(error);
            active.Dispose();
            _activeFacility = null;
        }
        while (_facilityQueue.TryDequeue(out var queued))
        {
            queued.Completion.TrySetException(error);
            queued.Dispose();
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected)
            return;
        _connected = connected;
        try
        {
            ConnectionChanged?.Invoke(connected);
        }
        catch (Exception ex)
        {
            _log(Tr.F("Error en un suscriptor de ConnectionChanged: {0}", ex));
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        _simVersion = $"{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}." +
                      $"{data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}";
        _log(Tr.F("Conectado a {0} {1}.{2} ", data.szApplicationName, data.dwApplicationVersionMajor, data.dwApplicationVersionMinor) +
             $"(build {data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}), " +
             $"SimConnect {data.dwSimConnectVersionMajor}.{data.dwSimConnectVersionMinor}");
        SetConnected(true);
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        _log(Tr.T("MSFS cerró la conexión."));
        _quitReceived = true;
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        _log(Tr.F("Excepción de SimConnect: {0} (SendID {1}, índice {2})", (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID, data.dwIndex));
    }

    private void OnRecvFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (OnWaypointData(data))
            return;
        if (_activeFacility is not { } active || data.UserRequestId != active.RequestId)
            return;
        try
        {
            active.Assembler.Add(data);
        }
        catch (Exception ex)
        {
            _log(Tr.F("Error al leer datos de {0}: {1}", active.Icao, ex));
            active.Completion.TrySetException(ex);
            active.Dispose();
            _activeFacility = null;
        }
    }

    private void OnRecvFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (OnWaypointEnd(data.RequestId))
            return;
        if (_activeFacility is not { } active || data.RequestId != active.RequestId)
            return;
        _activeFacility = null;
        try
        {
            var airport = active.Assembler.Build();
            _log(Tr.F("{0}: {1} mensajes en {2} ms", active.Icao, active.Assembler.MessageCount, active.Stopwatch.ElapsedMilliseconds) +
                 (active.Assembler.OrphanCount > 0 ? $", {active.Assembler.OrphanCount} sin padre" : ""));
            active.Completion.TrySetResult(airport);
        }
        catch (Exception ex)
        {
            active.Completion.TrySetException(ex);
        }
        finally
        {
            active.Dispose();
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (OnLoadInfoData(data))
            return;
        if (data.dwRequestID == (uint)RequestId.ObjectAltitude)
        {
            OnAltitudeData(data);
            return;
        }
        if (data.dwRequestID == (uint)RequestId.ObjectPose)
        {
            OnObjectPose(data);
            return;
        }
        if (data.dwRequestID == (uint)RequestId.Ambient)
        {
            var a = (AmbientData)data.dwData[0];
            try
            {
                AmbientWeatherUpdated?.Invoke(new AmbientWeather(new GeoPoint(a.LatitudeDeg, a.LongitudeDeg), a.OnGround != 0, a.AglFt,
                    GeoMath.NormalizeDeg(a.WindFromTrueDeg), a.WindKt, a.VisibilityM, a.TemperatureC, a.SeaLevelPressureMb,
                    AmbientWeather.FromMask((int)a.PrecipitationMask), a.MagVarDeg));
            }
            catch (Exception ex)
            {
                _log(Tr.F("Error en un suscriptor de AmbientWeatherUpdated: {0}", ex));
            }
            return;
        }
        if (data.dwRequestID == (uint)RequestId.UserRadios)
        {
            try
            {
                UserRadiosUpdated?.Invoke(((UserRadiosData)data.dwData[0]).ToRadios());
            }
            catch (Exception ex)
            {
                _log(Tr.F("Error en un suscriptor de UserRadiosUpdated: {0}", ex));
            }
            return;
        }
        if (data.dwRequestID == (uint)RequestId.UserLoad)
        {
            var load = (UserLoadData)data.dwData[0];
            try
            {
                UserLoadUpdated?.Invoke(new UserAircraftLoad(load.FuelLbs, load.TotalWeightLbs, load.EmptyWeightLbs,
                    load.MaxGrossWeightLbs, load.AtcId.Trim(), load.AtcModel.Trim(), load.AtcType.Trim(), load.Title.Trim()));
            }
            catch (Exception ex)
            {
                _log(Tr.F("Error en un suscriptor de UserLoadUpdated: {0}", ex));
            }
            return;
        }
        if (data.dwRequestID != (uint)RequestId.UserAircraft)
            return;

        var raw = (UserAircraftData)data.dwData[0];

        // Diagnóstico de V-03: confirmar en el sim cómo llega el transpondedor.
        if (raw.TransponderBco16 != _lastTransponderRaw)
        {
            _lastTransponderRaw = raw.TransponderBco16;
            _log(Tr.F("Transpondedor crudo (Bco16): {0} = 0x{1:X4}", raw.TransponderBco16, (uint)raw.TransponderBco16));
        }

        var transponder = Bco16.TryDecode((uint)raw.TransponderBco16, out var code) ? code : -1;
        var state = new UserAircraftState(
            new GeoPoint(raw.LatitudeDeg, raw.LongitudeDeg),
            raw.AltitudeMslFt,
            raw.AltitudeAglFt,
            GeoMath.NormalizeDeg(raw.HeadingTrueDeg),
            raw.IndicatedAirspeedKt,
            raw.GroundSpeedKt,
            raw.VerticalSpeedFpm,
            raw.OnGround != 0,
            (int)Math.Round(raw.Com1ActiveKhz),
            transponder,
            raw.ParkingBrake != 0,
            raw.PushbackAttached != 0,
            raw.PressureAltitudeFt,
            raw.QnhAltitudeFt,
            raw.Engine1Running != 0 || raw.Engine2Running != 0);

        try
        {
            UserStateUpdated?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log(Tr.F("Error en un suscriptor de UserStateUpdated: {0}", ex));
        }
    }

    /// <summary>Un pedido de aeropuerto en cola o en curso.</summary>
    private sealed class FacilityRequest(string icao) : IDisposable
    {
        public string Icao { get; } = icao;
        public AirportAssembler Assembler { get; } = new(icao);
        public TaskCompletionSource<AirportData> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stopwatch Stopwatch { get; } = new();
        public CancellationTokenRegistration CancelRegistration { get; set; }
        public uint RequestId { get; set; }
        public long DeadlineTicks { get; set; }

        public void Dispose() => CancelRegistration.Dispose();
    }
}
