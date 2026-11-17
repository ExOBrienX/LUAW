using SimATC.Core.Atc;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// Errores del piloto (PLAN-PULIDO bloque 4, ADR-124): el ATC dice qué hacer con frases que ya existen y la sesión se pone
/// al día en vez de quedarse esperando. Nivel equivocado (pasarse del nivel autorizado o dejarlo sin autorización) y
/// despegue sin autorización o sin colacionarla.
/// </summary>
public sealed partial class PilotSession
{
    /// <summary>Desvío (ft) del nivel autorizado a partir del cual el ATC lo corrige. APROXIMADO (300 ft es el límite de Modo C).</summary>
    public const double LevelBustFt = 400;

    /// <summary>Segundos que tiene que durar el desvío antes de la corrección (no por un rebote al nivelar).</summary>
    public const double LevelBustS = 15;

    private double _bustLevelFt = double.NaN;
    private bool _bustReached;
    private bool _bustFromAbove;
    private bool _bustSaid;
    private double _bustSinceS = double.NaN;

    /// <summary>
    /// ¿Se pasó del nivel autorizado, o lo dejó después de alcanzarlo? Entonces el ATC lo devuelve con la instrucción normal
    /// ("climb FL240" / "descend to altitude 4000 feet"), una vez por desvío. No con la aproximación autorizada (ahí manda
    /// la senda), ni en la frustrada, ni en tierra.
    /// </summary>
    private AtcTransmission? LevelBust(AtcStation station, double altFt, double nowS)
    {
        if (Phase is not (PilotPhase.WithApproach or PilotPhase.EnRoute or PilotPhase.Arrival) || _clearedFt is not { } cleared
            || _approachCleared || _missedApproach || ctx.OnGround?.Invoke() == true)
        {
            _bustSinceS = double.NaN;
            return null;
        }
        if (cleared != _bustLevelFt)
        {
            // Nivel nuevo: desde qué lado va hacia él.
            _bustLevelFt = cleared;
            _bustReached = Math.Abs(altFt - cleared) < 200;
            _bustFromAbove = altFt > cleared;
            _bustSaid = false;
            _bustSinceS = double.NaN;
        }
        var dev = altFt - cleared;
        if (Math.Abs(dev) < 200)
        {
            _bustReached = true;
            _bustSaid = false;
            _bustSinceS = double.NaN;
            return null;
        }
        var bust = _bustReached ? Math.Abs(dev) > LevelBustFt : _bustFromAbove ? dev < -LevelBustFt : dev > LevelBustFt;
        // En crucero y bajando cerca del TOD sin que le hayan dado el descenso: no se lo devuelve arriba, se le da el
        // descenso (ADR-129: su FMS empieza a bajar antes que la cuenta del ATC; "ascienda a FL320" en pleno descenso).
        if (bust && dev < 0 && Phase == PilotPhase.EnRoute && !_descentGiven)
        {
            _pilotStartedDescent = true;
            var floor = Arrival?.ApproachAltitudeFt ?? (Arrival?.Airport.ElevationFt ?? 0) + 3000;
            if (DescentDue(cleared, floor, DestinationNm()))
            {
                _bustSinceS = double.NaN;
                return null;
            }
        }
        if (!bust)
        {
            _bustSinceS = double.NaN;
            return null;
        }
        if (_bustSaid)
            return null;
        if (double.IsNaN(_bustSinceS))
        {
            _bustSinceS = nowS;
            return null;
        }
        if (nowS - _bustSinceS < LevelBustS)
            return null;
        _bustSaid = true;
        var ta = Phase >= PilotPhase.Arrival || _descentGiven ? DestTa ?? OriginTa : OriginTa;
        log?.Invoke(Tr.F("{0}: nivel equivocado ({1:0} ft, autorizado {2:0} ft): el ATC lo corrige.", Callsign, altFt, cleared));
        if (dev > 0)
            return Descend(station, cleared, ta, nowS, approachDescent: false);
        var level = LevelText(cleared, ta);
        return FlightInstruction(station, "atc.climb", new() { ["level"] = level }, new ExpectedReadback(Callsign) { LevelFt = cleared },
            "pilot.readback_climb", new() { ["level"] = level }, nowS);
    }

    /// <summary>
    /// ¿Despegó sin que la sesión lo tuviera autorizado y colacionado? (rodando, en el punto de espera, alineado o con la
    /// autorización de despegue sin colacionar). La sesión se pone al día: en el aire, sin colación pendiente.
    /// </summary>
    private bool TookOffEarly()
    {
        if (Phase is < PilotPhase.Taxiing or >= PilotPhase.TakeoffCleared || ctx.OnGround?.Invoke() != false || AglFt is < 100)
            return false;
        log?.Invoke(Phase == PilotPhase.TakeoffGiven
            ? Tr.F("{0} despegó sin colacionar la autorización de despegue: se da por colacionada.", Callsign)
            : Tr.F("{0} DESPEGÓ SIN AUTORIZACIÓN (fase {1}): la sesión sigue desde el aire.", Callsign, Phase));
        _expected = null;
        return true;
    }
}
