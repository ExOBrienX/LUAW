using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

namespace SimATC.Core.Comms;

/// <summary>En qué va el personal de tierra durante el pushback del piloto (RF-21c).</summary>
public enum CrewPhase
{
    Idle,
    /// <summary>El remolque va hacia la rueda de nariz (el simulador lo marca enganchado antes de que llegue).</summary>
    Connecting,
    /// <summary>Remolque conectado; espera que el piloto suelte el freno de estacionamiento.</summary>
    WaitBrakeRelease,
    Pushing,
    /// <summary>El piloto puso el freno empujando: el remolque sigue enganchado y continúa al soltarlo.</summary>
    Paused,
    /// <summary>
    /// Cerca del final: el personal pide el freno y el remolque sigue enganchado hasta que lo pone (prueba de Marco,
    /// 2026-11-01). Si no lo pone a tiempo, el remolque se suelta justo para que el avión se detenga en el punto planeado:
    /// el del simulador no se puede frenar de otra forma (prueba de Marco 2026-11-06: seguía empujando hacia atrás).
    /// </summary>
    Holding,
    /// <summary>Se detuvo (o el remolque se soltó solo); espera que el piloto ponga el freno.</summary>
    WaitBrakeSet,
    Done,
}

/// <summary>Qué hacer con el remolque del simulador en este paso.</summary>
public enum TugAction
{
    None,
    Attach,
    Drive,
    Release,
}

/// <summary>Lo que resulta de un paso: lo que dice el personal de tierra y la orden para el remolque.</summary>
public readonly record struct CrewOutput(IReadOnlyList<string> Messages, TugAction Tug, TugCommand? Drive = null)
{
    public static readonly CrewOutput Nothing = new([], TugAction.None);
}

/// <summary>
/// El personal de tierra del pushback (RF-21c, frases de §8.9): conecta el remolque, espera que el piloto suelte el
/// freno, empuja por la línea planeada (<see cref="PushbackGuide"/>), autoriza el arranque de motores, pide el freno al
/// terminar y se despide. Si el piloto pone el freno empujando, pausa; al soltarlo, sigue. Lógica pura: el reloj, la
/// posición y el freno los da quien llama.
/// </summary>
public sealed class GroundCrew(Phraseology phrases)
{
    /// <summary>
    /// Lo que tarda el remolque del simulador en llegar a la rueda de nariz y engancharse de verdad (s). V-27: el simulador
    /// lo marca enganchado al instante, pero se ve acomodándose varios segundos (en la app, más de 8).
    /// </summary>
    public const double ConnectDelayS = 14;

    /// <summary>Recorrido (m) después del cual el personal autoriza el arranque de motores.</summary>
    public const double StartEnginesAfterM = 10;

    private PushbackGuide? _guide;
    private double _phaseAtS;
    private bool _enginesSaid;

    public CrewPhase Phase { get; private set; } = CrewPhase.Idle;

    /// <summary>Hay un pushback en curso (desde que se conecta hasta la despedida).</summary>
    public bool Active => Phase is not (CrewPhase.Idle or CrewPhase.Done);

    public PushbackGuide? Guide => _guide;

    /// <summary>Conecta el remolque para empujar por <paramref name="path"/>.</summary>
    public CrewOutput Start(Polyline path, double nowS)
    {
        _guide = new PushbackGuide(path);
        _enginesSaid = false;
        Enter(CrewPhase.Connecting, nowS);
        return new([], TugAction.Attach);
    }

    /// <summary>
    /// A esta distancia del final (m), si el remolque del simulador se suelta solo, el pushback se da por completo ("set
    /// parking brake"); más lejos, por detenido. En la prueba de V-27 el remolque dejó el avión a pocos metros del final.
    /// </summary>
    public const double NearEndM = 8;

    /// <summary>Con el freno puesto a menos de esto (m) del punto de soltar, el pushback se da por terminado (no pausado).</summary>
    public const double BrakeAtEndM = 2;

    /// <summary>Tiempo empujando (s) antes de creer que el remolque se soltó solo (al engancharse tarda en marcarlo).</summary>
    public const double DetachGraceS = 3;

    /// <summary>Si al final el piloto no pone el freno en este tiempo (s), el remolque se suelta igual (no empuja sin fin).</summary>
    public const double HoldTimeoutS = 30;

    /// <summary>
    /// Un paso (≈10 por segundo mientras está activo). <paramref name="tugAttached"/> es <c>PUSHBACK ATTACHED</c> del
    /// simulador (null si no se sabe).
    /// </summary>
    public CrewOutput Tick(double nowS, LocalPoint position, double headingDeg, bool parkingBrakeSet, bool? tugAttached = null)
    {
        if (Phase is CrewPhase.Pushing or CrewPhase.Paused or CrewPhase.Holding)
            _guide?.Track(position);
        var nearEnd = _guide is { } g && g.Remaining <= NearEndM;
        switch (Phase)
        {
            case CrewPhase.Pushing or CrewPhase.Paused or CrewPhase.Holding when tugAttached == false && nowS - _phaseAtS >= DetachGraceS:
                // El remolque del simulador se soltó solo (en V-27 lo hizo a pocos metros del final): se termina ahí, con
                // el freno ya puesto se despide y si no, lo pide.
                if (parkingBrakeSet)
                {
                    Enter(CrewPhase.Done, nowS);
                    return new([Say("crew.disconnect")], TugAction.Release);
                }
                var wasHolding = Phase == CrewPhase.Holding;
                Enter(CrewPhase.WaitBrakeSet, nowS);
                return new(wasHolding ? [] : [Say(nearEnd ? "crew.complete" : "crew.stopped")], TugAction.Release);
            case CrewPhase.Connecting when nowS - _phaseAtS >= ConnectDelayS:
                Enter(CrewPhase.WaitBrakeRelease, nowS);
                return new([Say("crew.connected")], TugAction.None);
            case CrewPhase.WaitBrakeRelease when !parkingBrakeSet:
                Enter(CrewPhase.Pushing, nowS);
                return new([Say("crew.pushing")], TugAction.Drive, _guide!.Next(position, headingDeg));
            case CrewPhase.Pushing when parkingBrakeSet && _guide!.Remaining <= PushbackGuide.StopAheadM + BrakeAtEndM:
                // Freno puesto ya en el final: terminado.
                Enter(CrewPhase.Done, nowS);
                return new([Say("crew.disconnect")], TugAction.Release);
            case CrewPhase.Pushing when parkingBrakeSet:
                // El piloto puso el freno empujando: pausa (el remolque no avanza con el freno puesto).
                Enter(CrewPhase.Paused, nowS);
                return new([Say("crew.paused")], TugAction.Drive, new TugCommand(0));
            case CrewPhase.Paused when !parkingBrakeSet:
                Enter(CrewPhase.Pushing, nowS);
                return new([Say("crew.resumed")], TugAction.Drive, _guide!.Next(position, headingDeg) ?? new TugCommand(0));
            case CrewPhase.Paused:
                return new([], TugAction.Drive, new TugCommand(0));
            case CrewPhase.Pushing when _guide!.Remaining <= PushbackGuide.StopAheadM:
                // Ya en el punto donde hay que soltarlo (el remolque frena en ~2.5 m): se suelta y se pide el freno.
                Enter(CrewPhase.WaitBrakeSet, nowS);
                return new([Say("crew.complete")], TugAction.Release);
            case CrewPhase.Pushing:
                // Guiado hasta el punto exacto (ADR-134): antes pedía el freno 8 m antes y dejaba de girar, y el avión quedaba
                // corto y torcido (KMIA: 9 m y 10° del objetivo). Al llegar se suelta (caso de arriba) y recién ahí el freno.
                if (_guide!.Next(position, headingDeg) is not { } cmd)
                {
                    Enter(CrewPhase.WaitBrakeSet, nowS);
                    return new([Say("crew.complete")], TugAction.Release);
                }
                if (!_enginesSaid && _guide.Progress >= StartEnginesAfterM)
                {
                    _enginesSaid = true;
                    return new([Say("crew.start_engines")], TugAction.Drive, cmd);
                }
                return new([], TugAction.Drive, cmd);
            case CrewPhase.Holding when parkingBrakeSet:
                // Freno puesto: ahora sí se desconecta y el remolque se va.
                Enter(CrewPhase.Done, nowS);
                return new([Say("crew.disconnect")], TugAction.Release);
            case CrewPhase.Holding when _guide!.Remaining <= PushbackGuide.StopAheadM || nowS - _phaseAtS >= HoldTimeoutS:
                // Sin freno todavía: se suelta para que el avión se detenga en el punto (no sigue empujando de largo).
                Enter(CrewPhase.WaitBrakeSet, nowS);
                return new([], TugAction.Release);
            case CrewPhase.Holding:
                return new([], TugAction.Drive, _guide!.Next(position, headingDeg) ?? new TugCommand(0));
            case CrewPhase.WaitBrakeSet when parkingBrakeSet:
                Enter(CrewPhase.Done, nowS);
                return new([Say("crew.disconnect")], TugAction.None);
            default:
                return CrewOutput.Nothing;
        }
    }

    /// <summary>El piloto detiene el pushback (o cierra la ventana empujando).</summary>
    public CrewOutput Stop(double nowS)
    {
        if (Phase is not (CrewPhase.Connecting or CrewPhase.WaitBrakeRelease or CrewPhase.Pushing or CrewPhase.Paused))
            return CrewOutput.Nothing;
        Enter(CrewPhase.WaitBrakeSet, nowS);
        return new([Say("crew.stopped")], TugAction.Release);
    }

    private void Enter(CrewPhase phase, double nowS)
    {
        Phase = phase;
        _phaseAtS = nowS;
    }

    private string Say(string id) => phrases.Say(id, new Dictionary<string, string>());
}
