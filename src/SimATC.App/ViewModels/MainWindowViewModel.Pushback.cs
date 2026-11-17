using Avalonia.Threading;
using SimATC.Core.Comms;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Pushback del piloto con personal de tierra (RF-21c): abre el planificador, y al iniciar, el personal de tierra
/// (<see cref="GroundCrew"/>) habla en el chat y guía el pushback diez veces por segundo.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Distancia a un puesto (m) para ofrecer el planificador.</summary>
    private const double PushbackStandRadiusM = 60;

    private GroundCrew? _crew;
    private double _crewLoggedAt;
    private PushbackViewModel? _pushbackVm;
    private DispatcherTimer? _crewTimer;
    private readonly System.Diagnostics.Stopwatch _crewClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>La ventana principal abre (o trae al frente) el planificador.</summary>
    public event Action<PushbackViewModel>? PushbackWindowRequested;

    private PushbackPlan? _autoPlan;
    private GeoPoint? _autoPlanFrom;

    private void InitPushback()
    {
        Comms.PushbackPlanRequested += OpenPushbackPlanner;
        Comms.AutoPushbackRequested += () =>
        {
            if (_autoPlan is { } plan && PushbackBlocker() is null)
                StartPushback(_pushbackVm, plan);
        };
    }

    /// <summary>
    /// Con el pushback aprobado y el avión detenido en un puesto: calcula el automático (una vez por posición), lo
    /// dibuja en el radar y lo ofrece en "Qué decir ahora".
    /// </summary>
    private void UpdateAutoPushback()
    {
        var offer = !CrewActive && _session is { Phase: PilotPhase.PushbackApproved, PushedBack: false } && AtStandStopped();
        if (!offer || _graph is not { } graph || Airport is not { } airport || UserState is not { } u)
        {
            if (_autoPlan is not null && !CrewActive)
            {
                _autoPlan = null;
                _autoPlanFrom = null;
                if (_session?.TaxiRoute is null)
                    UserRoute = null;
            }
            Comms.AutoPushbackText = null;
            return;
        }
        if (_autoPlanFrom is not { } from || GeoMath.DistanceM(from, u.Position) > 3)
        {
            _autoPlanFrom = u.Position;
            _autoPlan = PilotPushback.Suggest(graph, airport, graph.Projection.ToLocal(u.Position), u.HeadingTrueDeg, _session?.Clearance?.Runway);
            UserRoute = _autoPlan is { } p ? [.. p.Path.Points.Select(graph.Projection.ToGeo)] : null;
            UserHoldShort = null;
            Log(_autoPlan is { } a ? Tr.F("Pushback automático: {0}, {1:0} m.", a.Summary, a.Path.Length) : Tr.T("Pushback automático: no hay uno que termine sobre una calle; planéalo a mano."));
        }
        Comms.AutoPushbackText = _autoPlan?.Summary;
    }

    /// <summary>Hay un pushback en curso: el panel no ofrece otro.</summary>
    private bool CrewActive => _crew?.Active == true;

    /// <summary>En tierra, detenido y en un puesto (para ofrecer el planificador).</summary>
    private bool AtStandStopped()
    {
        if (UserState is not { OnGround: true, GroundSpeedKt: < 1 } u || Airport is not { } airport)
            return false;
        foreach (var p in airport.Parkings)
        {
            if (GeoMath.DistanceM(p.Position, u.Position) < PushbackStandRadiusM)
                return true;
        }
        return false;
    }

    private void OpenPushbackPlanner()
    {
        if (_pushbackVm is { } open)
        {
            PushbackWindowRequested?.Invoke(open);
            return;
        }
        if (_graph is not { } graph || UserState is not { } u || Airport is not { } airport)
        {
            Log(Tr.T("Pushback: falta el aeropuerto o la posición del avión."));
            return;
        }
        var vm = PushbackViewModel.ForStand(graph, airport, u.Position, u.HeadingTrueDeg, _autoPlan?.Params);
        vm.StartRequested += plan => StartPushback(vm, plan);
        vm.StopRequested += () => Apply(_crew?.Stop(_crewClock.Elapsed.TotalSeconds) ?? CrewOutput.Nothing);
        _pushbackVm = vm;
        UpdatePushbackBlocker();
        PushbackWindowRequested?.Invoke(vm);
    }

    /// <summary>La ventana se cerró: si no está empujando, se olvida el plan (un pushback en curso sigue).</summary>
    public void PushbackWindowClosed()
    {
        if (!CrewActive)
            _pushbackVm = null;
    }

    /// <summary>Se empieza con la aprobación de Ground (sin plan de vuelo, en pruebas, se permite igual).</summary>
    private void UpdatePushbackBlocker()
    {
        if (_pushbackVm is { IsPushing: false } vm)
            vm.Blocker = PushbackBlocker();
    }

    private string? PushbackBlocker() =>
        UserState is not { OnGround: true }
            ? Tr.T("El avión tiene que estar en tierra.")
            : _session is { } s && s.Phase != PilotPhase.PushbackApproved
                ? Tr.T("Pide el pushback a Ground; con \"pushback approved\" puedes empezar.")
                : null;

    private void StartPushback(PushbackViewModel? vm, PushbackPlan plan)
    {
        if (_phrases is not { } phrases || CrewActive)
            return;
        _crew = new GroundCrew(phrases);
        Comms.AutoPushbackText = null;
        if (vm is not null)
        {
            vm.IsPushing = true;
            vm.CrewStatus = Tr.T("Conectando el remolque…");
        }
        // La línea del pushback en el radar mientras dura.
        if (_graph is { } graph)
        {
            UserRoute = [.. plan.Path.Points.Select(graph.Projection.ToGeo)];
            UserHoldShort = null;
        }
        Log(Tr.F("Pushback: {0:0} m, rumbo final {1:0}°.", plan.Path.Length, plan.EndHeadingDeg));
        Apply(_crew.Start(plan.Path, _crewClock.Elapsed.TotalSeconds));
        _crewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => CrewTick());
        _crewTimer.Start();
    }

    private void CrewTick()
    {
        if (_crew is not { } crew || _graph is not { } graph || UserState is not { } u)
            return;
        var here = graph.Projection.ToLocal(u.Position);
        if (_pushbackVm is { } vm)
            vm.Live = (here, u.HeadingTrueDeg);
        var before = crew.Phase;
        Apply(crew.Tick(_crewClock.Elapsed.TotalSeconds, here, u.HeadingTrueDeg, u.ParkingBrakeSet, u.PushbackAttached));
        // Para Diagnóstico: los cambios de fase y, empujando, el estado una vez por segundo.
        if (crew.Phase != before || (crew.Phase == CrewPhase.Pushing && _crewClock.Elapsed.TotalSeconds - _crewLoggedAt >= 1))
        {
            _crewLoggedAt = _crewClock.Elapsed.TotalSeconds;
            Log(Tr.F("Pushback {0}: {1:0.0} kt, rumbo {2:0}°, freno {3}, ", crew.Phase, u.GroundSpeedKt, u.HeadingTrueDeg, (u.ParkingBrakeSet ? Tr.T("freno: puesto") : Tr.T("freno: suelto"))) +
                Tr.F("faltan {0:0.0} m.", crew.Guide?.Remaining ?? 0));
        }
        if (crew.Phase == CrewPhase.Done)
        {
            _crewTimer?.Stop();
            _session?.MarkPushedBack();
            _pushbackVm?.Complete(Tr.T("Pushback terminado. Pide rodaje a Ground."));
            _crew = null;
            UpdateCommsView();
        }
    }

    /// <summary>Lo que dice el personal de tierra va al chat; la orden, al remolque del simulador.</summary>
    private void Apply(CrewOutput output)
    {
        foreach (var m in output.Messages)
        {
            var text = m;
            _ = Speak(Audio.RadioVoice.Interphone, Voice.VoiceRole.GroundCrew, Tr.T("ground crew"), text,
                _ => Comms.Add(new CommsMessage(DateTime.UtcNow, Tr.T("Personal de tierra"), Speaker.OtherAircraft, text, "TIE")));
        }
        switch (output.Tug)
        {
            case TugAction.Attach:
                _bridge.AttachPushbackTug();
                break;
            case TugAction.Drive when output.Drive is { } cmd:
                // Cada paso (10 por segundo): el simulador no mantiene el giro escrito por sí solo.
                _bridge.SetPushbackYawRate(cmd.YawRateDegS);
                break;
            case TugAction.Release:
                _bridge.SetPushbackYawRate(0);
                _bridge.ReleasePushbackTug();
                break;
        }
        if (_pushbackVm is { } vm && _crew is { } crew)
        {
            if (crew.Guide is { Path.Length: > 0 } g)
                vm.PushProgress = Math.Clamp(g.Progress / g.Path.Length * 100, 0, 100);
            vm.CrewStatus = crew.Phase switch
            {
                CrewPhase.Connecting => Tr.T("El remolque se está enganchando…"),
                CrewPhase.WaitBrakeRelease => Tr.T("Suelta el freno de estacionamiento."),
                CrewPhase.Pushing => Tr.F("Empujando… faltan {0:0} m.", crew.Guide?.Remaining ?? 0),
                CrewPhase.Paused => Tr.T("En pausa: suelta el freno para seguir, o Detener."),
                CrewPhase.Holding => Tr.T("Llegando al punto: pon el freno de estacionamiento (si no, el remolque se suelta para dejarte justo ahí)."),
                CrewPhase.WaitBrakeSet => Tr.T("Pon el freno de estacionamiento."),
                _ => vm.CrewStatus,
            };
        }
    }
}
