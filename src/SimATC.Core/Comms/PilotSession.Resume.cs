using SimATC.Core.Atc;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>Retomar el vuelo después de un cierre inesperado de LUAW (prueba de Marco 2026-11-09).</summary>
public sealed partial class PilotSession
{
    /// <summary>Lo que se guarda para retomar: fase, lo autorizado, la dependencia con la que habla y la colación pendiente.</summary>
    public FlightSnapshot Snapshot(DateTime nowUtc) => new(ctx.Plan, Phase, Clearance, PushedBack, ExpectedStation, nowUtc)
    {
        ClearedFt = _clearedFt,
        CruiseCleared = _cruiseCleared,
        DescentGiven = _descentGiven,
        ApproachExpected = _approachExpected,
        ApproachCleared = _approachCleared,
        LandingCleared = _landingCleared,
        DirectFix = _directFix,
        AnnouncedRunway = _announcedRunway,
        ArrivalRunway = Arrival?.Runway,
        MissedApproach = _missedApproach,
        ReintegrationGiven = _reintegrationGiven,
        SpeedKt = _speedKt,
        ViaStar = _viaStar,
        PendingInstruction = _expected is not null ? _lastInstruction : null,
        PendingReadback = _expected,
        PendingHint = _expected is not null ? _readbackHint : null,
        HandoffTo = _handoffTo,
    };

    /// <summary>
    /// Deja la sesión donde quedó el vuelo guardado (en la fase que calza con el avión ahora, ver
    /// <see cref="FlightResume.Phase"/>). En tierra rehace la ruta de rodaje desde donde está; en vuelo, el piloto vuelve a
    /// llamar a la dependencia que lo llevaba (primer contacto). Si la fase no cambió y había una colación pendiente, el ATC
    /// la vuelve a esperar (ADR-105). Devuelve la dependencia a la que tiene que llamar, o null si no hay nada que retomar.
    /// </summary>
    public AtcStation? Resume(FlightSnapshot s, bool atStand, double nowS = 0)
    {
        var onGround = ctx.OnGround?.Invoke() ?? true;
        if (FlightResume.Phase(s.Phase, onGround, atStand, s.PushedBack) is not { } phase)
            return null;
        Clearance = s.Clearance;
        PushedBack = s.PushedBack || (phase >= PilotPhase.Taxiing && phase <= PilotPhase.TakeoffCleared);
        _clearedFt = s.ClearedFt;
        _cruiseCleared = s.CruiseCleared;
        _descentGiven = s.DescentGiven;
        _approachExpected = s.ApproachExpected;
        _approachCleared = s.ApproachCleared;
        _landingCleared = s.LandingCleared;
        _directFix = s.DirectFix;
        _announcedRunway = s.AnnouncedRunway;
        _missedApproach = s.MissedApproach;
        _missedAtS = s.MissedApproach ? nowS : double.NaN;
        _reintegrationGiven = s.ReintegrationGiven;
        _speedKt = s.SpeedKt;
        _viaStar = s.ViaStar;
        _expected = null;
        _lastInstruction = null;
        _recalls = 0;
        Phase = phase;
        // Sin autorización no hay salida que retomar: vuelve a pedirla.
        if (phase <= PilotPhase.TakeoffCleared && Clearance is null)
        {
            Phase = PilotPhase.Parked;
            return ExpectedStation;
        }
        if (_missedApproach && Arrival is { } missedArrival)
            RestoreReintegrationFix(missedArrival);
        switch (phase)
        {
            case PilotPhase.Taxiing or PilotPhase.WithTower:
                // La ruta hasta el punto de espera desde donde está (la Torre la usa para saber si llegó).
                if (Clearance is { } c && ctx.PilotPosition() is { } here
                    && PilotTaxiRoute.From(ctx.Graph, ctx.Airport, here, c.Runway, fromStand: false, headingDeg: ctx.HeadingDeg?.Invoke()) is { } route)
                {
                    TaxiRoute = route;
                    ctx.SetHoldingPoint?.Invoke(route.HoldShort);
                    if (RunwayIndexOf(c.Runway) is { } idx)
                        ctx.SetTowerClearance?.Invoke(idx, PilotTowerClearance.None);
                }
                else if (phase == PilotPhase.Taxiing)
                {
                    // Sin ruta desde aquí: que vuelva a pedir el rodaje.
                    Phase = PilotPhase.PushbackApproved;
                }
                break;
            case PilotPhase.Landed:
                _awaitingCheckIn = Arrival?.Stations.For(AtcRole.Ground) ?? s.Station;
                break;
            case >= PilotPhase.WithApproach:
                // La colación que quedó pendiente en la misma fase: el ATC la vuelve a esperar (y la recuerda si no llega).
                if (phase == s.Phase && s.PendingReadback is { } pending && s.PendingInstruction is { } instruction)
                {
                    _expected = pending;
                    _lastInstruction = instruction;
                    _readbackHint = s.PendingHint;
                    _handoffTo = s.HandoffTo;
                    _instructionAtS = nowS;
                    log?.Invoke(Tr.F("Vuelo {0} retomado con la colación pendiente: \"{1}\".", Callsign, instruction.Text));
                    return instruction.Station;
                }
                _awaitingCheckIn = s.Station ?? DepartureStation;
                break;
        }
        log?.Invoke(Tr.F("Vuelo {0} retomado: {1} → {2}.", Callsign, s.Phase, Phase));
        return ExpectedStation;
    }

    /// <summary>Al retomar después de una frustrada: el fijo de la espera o del directo sale otra vez de la carta.</summary>
    private void RestoreReintegrationFix(ArrivalInfo arrival)
    {
        var (_, _, holdFix, holdAt) = MissedOf(arrival);
        if (holdFix is not null)
            (_reintegrationFix, _reintegrationAt, _reintegrationHold) = (holdFix, holdAt, true);
        else if (ApproachStartFix(arrival) is { } start)
            (_reintegrationFix, _reintegrationAt, _reintegrationHold) = (start.Fix, start.At, false);
    }
}
