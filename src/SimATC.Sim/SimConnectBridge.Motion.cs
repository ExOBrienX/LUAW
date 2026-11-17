using System.Diagnostics;
using Microsoft.FlightSimulator.SimConnect;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Sim;

/// <summary>
/// Movimiento de aviones IA (etapa 3, ADR-017): evento por cuadro, pausa, pose con Initial Position, motores y luces.
/// </summary>
public sealed partial class SimConnectBridge
{
    /// <summary>IDs de eventos de sistema (no se mezclan con los IDs de eventos de cliente de las luces).</summary>
    private enum SystemEvent : uint { Frame = 900, PauseEx1 = 901 }

    private readonly Stopwatch _frameClock = new();
    private uint _pauseState;
    private int _simThreadId;

    public event Action<FrameInfo>? Frame;

    public event Action<uint, double, double>? ObjectAltitude;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct AltitudeSample
    {
        public double AltitudeFt;
        public double AglFt;
    }

    public void RequestAltitude(uint objectId) => RunOnSimThread(sim =>
    {
        if (_injected.ContainsKey(objectId))
            sim.RequestDataOnSimObject(RequestId.ObjectAltitude, DefinitionId.ObjectAltitude, objectId,
                SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    });

    /// <summary>Respuesta a <see cref="RequestAltitude"/> (llamado desde OnRecvSimobjectData).</summary>
    private void OnAltitudeData(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        var s = (AltitudeSample)data.dwData[0];
        try
        {
            ObjectAltitude?.Invoke(data.dwObjectID, s.AltitudeFt, s.AglFt);
        }
        catch (Exception ex)
        {
            _log(Tr.F("Error en un suscriptor de ObjectAltitude: {0}", ex));
        }
    }

    /// <inheritdoc />
    public bool AirborneVelocities { get; set; }

    public void SetPose(uint objectId, in AircraftPose pose)
    {
        var p = pose;
        RunOnSimThread(sim =>
        {
            if (!_injected.ContainsKey(objectId))
                return;
            sim.SetDataOnSimObject(DefinitionId.InitialPosition, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new SIMCONNECT_DATA_INITPOSITION
                {
                    Latitude = p.Position.LatDeg,
                    Longitude = p.Position.LonDeg,
                    Altitude = p.AltitudeFt,
                    Pitch = -p.PitchDeg, // SimConnect: negativo = nariz arriba (verificado en el sim)
                    Bank = -p.BankDeg,
                    Heading = p.HeadingTrueDeg,
                    OnGround = p.OnGround ? 1u : 0u,
                    Airspeed = (uint)Math.Max(0, Math.Round(p.AirspeedKt)), // hace girar las ruedas en tierra
                });
            // Animaciones (V-09, probado): rodadura, tren y flaps. Va después de Initial Position, que con OnGround=0 sube
            // el tren de golpe; escribiéndolo cada cuadro el modelo lo anima de a poco.
            sim.SetDataOnSimObject(DefinitionId.BodyMotion, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new BodyMotion
                {
                    LongitudinalFtPerS = p.OnGround || AirborneVelocities ? p.LongitudinalSpeedKt * 1.6878098571 : 0,
                    GearCenter = p.GearDown,
                    GearLeft = p.GearDown,
                    GearRight = p.GearDown,
                    FlapsLeft = p.FlapsPercent / 100,
                    FlapsRight = p.FlapsPercent / 100,
                    VerticalSpeedFtPerS = p.OnGround || AirborneVelocities ? p.VerticalSpeedFpm / 60 : 0,
                });
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct BodyMotion
    {
        public double LongitudinalFtPerS;
        public double GearCenter, GearLeft, GearRight;
        public double FlapsLeft, FlapsRight;
        public double VerticalSpeedFtPerS;
    }

    /// <summary>Arranque/apagado gradual con los eventos del simulador (probado: N1 0 → 30 % en ~8 s).</summary>
    public void SetEngines(uint objectId, bool running, int engineCount) => RunOnSimThread(sim =>
    {
        if (!_injected.ContainsKey(objectId))
            return;
        sim.TransmitClientEvent(objectId, running ? EngineEvent.AutoStart : EngineEvent.AutoShutdown, 0,
            EventGroup.HighestPriority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    });

    /// <summary>Palanca de gases por SimVar (probado: el sonido sube hasta potencia de despegue).</summary>
    public void SetThrottle(uint objectId, double percent, int engineCount) => RunOnSimThread(sim =>
    {
        if (!_injected.ContainsKey(objectId))
            return;
        var count = engineCount is >= 1 and <= 4 ? engineCount : 2;
        var value = new EngineCombustion { Value = Math.Clamp(percent, 0, 100) };
        for (var i = 1; i <= count; i++)
            sim.SetDataOnSimObject(DefinitionId.Throttle1 + (uint)(i - 1), objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
    });

    private enum EngineEvent : uint { AutoStart = 950, AutoShutdown = 951 }

    public void SetLights(uint objectId, LightFlags lights) => RunOnSimThread(sim =>
    {
        if (!_injected.ContainsKey(objectId))
            return;
        foreach (var (ev, _, _) in LightEvents)
        {
            var on = ev switch
            {
                LightEvent.Beacon => lights.HasFlag(LightFlags.Beacon),
                LightEvent.Nav => lights.HasFlag(LightFlags.Nav),
                LightEvent.Strobe => lights.HasFlag(LightFlags.Strobe),
                LightEvent.Landing => lights.HasFlag(LightFlags.Landing),
                LightEvent.Taxi => lights.HasFlag(LightFlags.Taxi),
                LightEvent.Logo => lights.HasFlag(LightFlags.Logo),
                LightEvent.Wing => lights.HasFlag(LightFlags.Wing),
                _ => false,
            };
            sim.TransmitClientEvent(objectId, ev, on ? 1u : 0u, EventGroup.HighestPriority,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
    });

    /// <summary>Dentro del evento Frame (hilo de SimConnect) se aplica en el acto; desde otro hilo, por la cola.</summary>
    private void RunOnSimThread(Action<SimConnect> action)
    {
        if (Environment.CurrentManagedThreadId == _simThreadId && _sim is { } sim)
            action(sim);
        else
            Enqueue(action);
    }

    private static void RegisterMotion(SimConnect sim)
    {
        // Forma oficial (página SimConnect_SetDataOnSimObject): "Initial Position", unidad NULL, tipo INITPOSITION.
        sim.AddToDataDefinition(DefinitionId.InitialPosition, "Initial Position", null, SIMCONNECT_DATATYPE.INITPOSITION, 0f,
            SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<SIMCONNECT_DATA_INITPOSITION>(DefinitionId.InitialPosition);
        sim.AddToDataDefinition(DefinitionId.ObjectAltitude, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DefinitionId.ObjectAltitude, "PLANE ALT ABOVE GROUND", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AltitudeSample>(DefinitionId.ObjectAltitude);
        foreach (var (name, unit) in new[]
                 {
                     ("VELOCITY BODY Z", "feet per second"), ("GEAR CENTER POSITION", "percent over 100"),
                     ("GEAR LEFT POSITION", "percent over 100"), ("GEAR RIGHT POSITION", "percent over 100"),
                     ("TRAILING EDGE FLAPS LEFT PERCENT", "percent over 100"), ("TRAILING EDGE FLAPS RIGHT PERCENT", "percent over 100"),
                     ("VERTICAL SPEED", "feet per second"),
                 })
            sim.AddToDataDefinition(DefinitionId.BodyMotion, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<BodyMotion>(DefinitionId.BodyMotion);
        sim.MapClientEventToSimEvent(EngineEvent.AutoStart, "ENGINE_AUTO_START");
        sim.MapClientEventToSimEvent(EngineEvent.AutoShutdown, "ENGINE_AUTO_SHUTDOWN");
        for (var i = 1; i <= 4; i++)
        {
            var def = DefinitionId.Throttle1 + (uint)(i - 1);
            sim.AddToDataDefinition(def, $"GENERAL ENG THROTTLE LEVER POSITION:{i}", "percent", SIMCONNECT_DATATYPE.FLOAT64, 0f,
                SimConnect.SIMCONNECT_UNUSED);
            sim.RegisterDataDefineStruct<EngineCombustion>(def);
        }
        sim.SubscribeToSystemEvent(SystemEvent.Frame, "Frame");
        sim.SubscribeToSystemEvent(SystemEvent.PauseEx1, "Pause_EX1");
    }

    private void HookMotionEvents(SimConnect sim)
    {
        _simThreadId = Environment.CurrentManagedThreadId;
        _frameClock.Restart();
        sim.OnRecvEventFrame += OnRecvEventFrame;
        sim.OnRecvEvent += OnRecvEvent;
    }

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        if (data.uEventID != (uint)SystemEvent.PauseEx1)
            return;
        if (data.dwData != _pauseState)
            _log(Tr.F("Pausa del simulador: {0} ({1}).", data.dwData, (data.dwData == 0 ? Tr.T("sin pausa") : Tr.T("en pausa"))));
        _pauseState = data.dwData;
    }

    private void OnRecvEventFrame(SimConnect sender, SIMCONNECT_RECV_EVENT_FRAME data)
    {
        var real = _frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        try
        {
            // Cualquier bit de pausa congela el tráfico (ADR-017; la pausa activa llega como 8).
            Frame?.Invoke(new FrameInfo(Math.Min(real, 1.0), data.fSimSpeed, _pauseState != 0));
        }
        catch (Exception ex)
        {
            _log(Tr.F("Error en un suscriptor de Frame: {0}", ex));
        }
    }
}
