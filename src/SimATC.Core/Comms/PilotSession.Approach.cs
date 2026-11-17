using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// Dónde está el avión respecto de la final de la pista: distancia al umbral sobre el eje (NM, positiva antes del umbral),
/// desvío lateral (NM, positivo a la derecha mirando a la pista), diferencia entre su rumbo y el de la pista (°) y la altitud
/// que le corresponde en la senda (ft).
/// </summary>
public readonly record struct FinalPosition(double AlongNm, double CrossNm, double TrackErrorDeg, double ProfileFt)
{
    /// <summary>Establecido en la final: por delante del umbral, cerca del eje y con rumbo de pista (APROXIMADO).</summary>
    public bool Established => AlongNm > 0.3 && AlongNm <= PilotSession.EstablishedMaxNm
                               && Math.Abs(CrossNm) <= Math.Max(0.6, AlongNm * 0.1) && Math.Abs(TrackErrorDeg) <= 30;
}

/// <summary>Geometría de la final (ADR-105). Función pura.</summary>
public static class FinalGeometry
{
    public const double FeetPerNm = 6076.12;

    /// <summary>Altura de cruce del umbral (ft) que se suma a la senda. APROXIMADO (cartas típicas: 50 ft).</summary>
    public const double ThresholdCrossingFt = 50;

    public static FinalPosition Of(GeoPoint threshold, double runwayCourseDeg, double thresholdElevationFt, double slopeDeg,
        GeoPoint pilot, double headingDeg)
    {
        var d = GeoMath.DistanceM(threshold, pilot) / UnitsNm;
        var off = GeoMath.AngleDiffDeg(GeoMath.NormalizeDeg(runwayCourseDeg + 180), GeoMath.InitialBearingDeg(threshold, pilot)) * GeoMath.DegToRad;
        var along = d * Math.Cos(off);
        var cross = -d * Math.Sin(off);
        var profile = thresholdElevationFt + ThresholdCrossingFt + Math.Max(0, along) * FeetPerNm * Math.Tan(slopeDeg * GeoMath.DegToRad);
        return new FinalPosition(along, cross, GeoMath.AngleDiffDeg(runwayCourseDeg, headingDeg), profile);
    }

    private const double UnitsNm = 1852;
}

/// <summary>
/// Qué tan estricta es la vigilancia de la aproximación (ADR-108, Configuración): completa (avisos y motor y al aire si no
/// está estabilizado), solo avisos (nunca ordena motor y al aire) o apagada (sin avisos de altura ni de senda; la velocidad
/// y la secuencia siguen, son control de tráfico normal).
/// </summary>
public enum ApproachWatchLevel
{
    Full,
    AdvisoryOnly,
    Off,
}

/// <summary>Una instrucción vigente para el panel del piloto: qué ("Altitud") y el valor ("4000 ft · QNH 1018").</summary>
public sealed record PilotOrder(string Label, string Value);

/// <summary>
/// La llegada vigilada (ADR-105, prueba de Marco 2026-11-09 en RJBB): el ATC controla la velocidad y el perfil de descenso,
/// avisa si el piloto va alto o bajo en la final, ordena motor y al aire si no está estabilizado, y después de una
/// frustrada lo reintegra (espera publicada o directo al inicio de la aproximación) antes de volver a autorizarlo.
/// </summary>
public sealed partial class PilotSession
{
    /// <summary>Más lejos que esto (NM) del umbral no se considera "establecido en la final".</summary>
    public const double EstablishedMaxNm = 15;

    /// <summary>Entre dos avisos de vigilancia pasan al menos estos segundos (no saturar la frecuencia).</summary>
    public const double WatchIntervalS = 20;

    /// <summary>Velocidad máxima bajo 10 000 ft (kt) y su tolerancia antes de que el ATC la recuerde.</summary>
    public const double Below10kKt = 250;

    /// <summary>Margen sobre la velocidad asignada antes de que el ATC la pida (o la repita), kt. APROXIMADO.</summary>
    public const double SpeedToleranceKt = 15;

    /// <summary>Puerta de estabilización (NM del umbral, ~800 ft sobre el umbral en una senda de 3°).</summary>
    public const double StableGateNm = 2.5;

    /// <summary>Le faltan más de estas NM para bajar a tiempo: va alto (acelerar el descenso; no se lo autoriza todavía).</summary>
    public const double TooHighNm = 3;

    /// <summary>Le faltan más de estas NM: el ATC le da un viraje de 360° para perder altura. APROXIMADO.</summary>
    public const double OrbitShortNm = 8;

    /// <summary>Separación mínima (NM) con el que aterriza antes: más cerca, el ATC lo frena. APROXIMADO (radar 3 NM + margen).</summary>
    public const double SequenceGapNm = 5;

    /// <summary>Más cerca que esto (NM) y lejos de la final, un viraje de 360° por secuencia.</summary>
    public const double SequenceOrbitNm = 3;

    /// <summary>
    /// La secuencia con el de adelante (más lento, o un 360°) solo se ordena a menos de esto del umbral (NM) y por debajo de
    /// esta altura sobre el aeropuerto (ft): más lejos, la distancia al umbral de cada uno no dice quién va delante (ADR-129).
    /// </summary>
    public const double SequenceMaxNm = 35;
    public const double SequenceMaxAglFt = 12_000;

    /// <summary>En espera tras la frustrada: tiempo mínimo en el fijo antes de la nueva autorización (s, ~un circuito corto).</summary>
    public const double HoldMinS = 120;

    // Frustrada y reintegro.
    private bool _missedApproach;
    private double _missedAtS = double.NaN;
    private bool _reintegrationGiven;
    private string? _reintegrationFix;
    private GeoPoint? _reintegrationAt;
    private bool _reintegrationHold;
    private double _reachedFixAtS = double.NaN;
    // Vigilancia.
    private int? _speedKt;
    private double _speedAtS;
    private bool _speedRepeated;
    private bool _expediteGiven;
    private bool _orbitGiven;
    private bool _sequenceOrbitGiven;
    private bool _profileAdvised;
    private bool _lowAltitudeWarned;
    private bool _gateChecked;
    private bool _viaStar;
    private double _nextWatchS;

    /// <summary>Hizo motor y al aire y todavía no lo vuelven a autorizar a la aproximación (el radar dibuja la frustrada).</summary>
    public bool MissedApproachActive => _missedApproach && !_approachCleared;

    /// <summary>El fijo de la espera o del directo después de la frustrada, o null.</summary>
    public string? ReintegrationFix => _reintegrationGiven ? _reintegrationFix : null;

    /// <summary>La velocidad asignada por el ATC (kt), o null.</summary>
    public int? AssignedSpeedKt => _speedKt;

    /// <summary>La aproximación autorizada (para el radar: dibujar la que dijo el ATC, no la del plan).</summary>
    public bool ApproachCleared => _approachCleared;

    private void ResetFinalWatch()
    {
        _sequenceOrbitGiven = false;
        _profileAdvised = false;
        _lowAltitudeWarned = false;
        _gateChecked = false;
        _expediteGiven = false;
        _orbitGiven = false;
        _wakeCautioned = false;
    }

    // ---------- Geometría ----------

    /// <summary>Umbral, rumbo y elevación de la pista de llegada, o null si la pista no está en el aeropuerto.</summary>
    private static (GeoPoint Threshold, double CourseDeg, double ElevationFt)? LandingEnd(ArrivalInfo arrival)
    {
        foreach (var r in arrival.Airport.Runways)
        {
            if (string.Equals(r.Primary.Ident, arrival.Runway, StringComparison.OrdinalIgnoreCase))
                return (r.Primary.Position, r.Primary.HeadingTrueDeg, r.ElevationFt);
            if (string.Equals(r.Secondary.Ident, arrival.Runway, StringComparison.OrdinalIgnoreCase))
                return (r.Secondary.Position, r.Secondary.HeadingTrueDeg, r.ElevationFt);
        }
        return null;
    }

    /// <summary>Ángulo de la senda (°): el de la carta en el FAF si es razonable; si no, 3°.</summary>
    private static double SlopeDeg(ArrivalInfo arrival)
    {
        if (arrival.Procedure is { } a)
        {
            foreach (var leg in a.FinalLegs)
            {
                var v = Math.Abs(leg.VerticalAngleDeg);
                if (v is >= 2.5 and <= 4.5)
                    return v;
            }
        }
        return 3;
    }

    private FinalPosition? Final(ArrivalInfo arrival)
    {
        if (LandingEnd(arrival) is not { } end || ctx.PilotPosition() is not { } here)
            return null;
        return FinalGeometry.Of(end.Threshold, end.CourseDeg, end.ElevationFt, SlopeDeg(arrival), here, ctx.HeadingDeg?.Invoke() ?? end.CourseDeg);
    }

    private double ThresholdNm(ArrivalInfo arrival) =>
        LandingEnd(arrival) is { } end && ctx.PilotPosition() is { } here ? GeoMath.DistanceM(here, end.Threshold) / Nm : DestinationNm();

    /// <summary>
    /// Millas que necesita para bajar desde su altitud (regla 3:1 de los pilotos: 3 NM cada 1000 ft, más 3 NM para reducir)
    /// y las que tiene por delante. APROXIMADO.
    /// </summary>
    private (double Required, double Available) DescentMiles(ArrivalInfo arrival, double altFt)
    {
        var required = Math.Max(0, altFt - arrival.Airport.ElevationFt) / 1000 * 3 + 3;
        var known = true;
        var route = _missedApproach ? 0 : RouteToThresholdNm(arrival, out known);
        // Sin fijos conocidos por delante no se sabe por dónde lo traerán: margen para no creerlo alto de más (ADR-106).
        if (!_missedApproach && !known)
            route += UnknownRouteMarginNm;
        return (required, Math.Max(ThresholdNm(arrival), route));
    }

    /// <summary>¿La aproximación tiene senda publicada? ILS, o la carta trae el ángulo de descenso de la final (RNAV con VNAV).</summary>
    private static bool HasPublishedPath(ArrivalInfo arrival)
    {
        if (arrival.Procedure is not { } a)
            return false;
        if (a.Type == ApproachType.Ils)
            return true;
        foreach (var leg in a.FinalLegs)
        {
            if (Math.Abs(leg.VerticalAngleDeg) is >= 2.5 and <= 4.5)
                return true;
        }
        return false;
    }

    /// <summary>Millas que se suman a la línea recta cuando no hay fijos del plan ni de la carta por delante. APROXIMADO.</summary>
    public const double UnknownRouteMarginNm = 10;

    /// <summary>
    /// ¿Aproximación directa por el eje de la pista? Las desplazadas (LDA, SDF, curso inverso, RNP con curvas o el FAF fuera del
    /// eje) no se vigilan con la senda ni con la puerta de estabilización: el eje de la pista no es su trayectoria (ADR-106).
    /// </summary>
    private static bool StraightIn(ArrivalInfo arrival)
    {
        if (arrival.Procedure is not { } a)
            return true;
        if (a.Type is ApproachType.Lda or ApproachType.Sdf or ApproachType.LocalizerBackCourse)
            return false;
        foreach (var leg in a.FinalLegs)
        {
            if (leg.Type == LegType.RF)
                return false;
            if (leg.IsFaf && (leg.FixPosition.LatDeg != 0 || leg.FixPosition.LonDeg != 0) && LandingEnd(arrival) is { } end
                && Math.Abs(FinalGeometry.Of(end.Threshold, end.CourseDeg, 0, 3, leg.FixPosition, end.CourseDeg).CrossNm) > 0.3)
                return false;
        }
        return true;
    }

    /// <summary>
    /// NM que le quedan por la ruta hasta el umbral: los fijos del plan por delante (STAR incluida), los de la aproximación
    /// (transición y final) que todavía no pasó, y el umbral. Sin eso, se subestimaba la distancia y el ATC lo creía alto.
    /// </summary>
    private double RouteToThresholdNm(ArrivalInfo arrival, out bool known)
    {
        known = false;
        if (LandingEnd(arrival) is not { } end || ctx.PilotPosition() is not { } here)
            return RouteRemainingNm();
        var mine = GeoMath.DistanceM(here, end.Threshold);
        var points = new List<GeoPoint> { here };
        foreach (var f in ctx.Plan.Fixes)
        {
            if (!f.IsPseudo && GeoMath.DistanceM(f.Position, end.Threshold) < mine - 2 * Nm && GeoMath.DistanceM(f.Position, end.Threshold) > 1 * Nm)
                points.Add(f.Position);
        }
        if (arrival.Procedure is { } a)
        {
            var legs = new List<ProcedureLeg>();
            foreach (var t in a.Transitions)
            {
                if (t.Name == arrival.Transition)
                    legs.AddRange(t.Legs);
            }
            legs.AddRange(a.FinalLegs);
            var last = points[^1];
            foreach (var leg in legs)
            {
                if (leg.FixIcao.Length == 0 || leg.FixIcao.StartsWith("RW", StringComparison.Ordinal))
                    continue;
                var d = GeoMath.DistanceM(leg.FixPosition, end.Threshold);
                // Solo los que quedan por delante y más cerca del umbral que el último punto de la ruta.
                if (d < mine - 1 * Nm && d < GeoMath.DistanceM(last, end.Threshold) + 3 * Nm && GeoMath.DistanceM(leg.FixPosition, last) > 0.5 * Nm)
                {
                    points.Add(leg.FixPosition);
                    last = leg.FixPosition;
                }
            }
        }
        known = points.Count > 1;
        points.Add(end.Threshold);
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
            total += GeoMath.DistanceM(points[i - 1], points[i]);
        return total / Nm;
    }

    // ---------- Vigilancia ----------

    /// <summary>
    /// Lo que el ATC vigila en la llegada, una cosa por vez y con pausas (<see cref="WatchIntervalS"/>): velocidad bajo
    /// 10 000 ft y en la secuencia, demasiado alto para bajar (acelerar el descenso o un viraje de 360°), la senda en la final
    /// (aviso y alerta de altitud baja) y la puerta de estabilización (motor y al aire).
    /// </summary>
    private IReadOnlyList<AtcTransmission> Watch(AtcStation station, ArrivalInfo arrival, double altFt, double nowS)
    {
        if (nowS < _nextWatchS || ctx.OnGround?.Invoke() == true)
            return [];
        var said = WatchOnce(station, arrival, altFt, nowS);
        if (said.Count > 0)
            _nextWatchS = nowS + WatchIntervalS;
        return said;
    }

    private IReadOnlyList<AtcTransmission> WatchOnce(AtcStation station, ArrivalInfo arrival, double altFt, double nowS)
    {
        var ias = ctx.IndicatedAirspeedKt?.Invoke();
        var fp = Final(arrival);
        var thrNm = ThresholdNm(arrival);
        var withTower = Phase == PilotPhase.ArrivalTower;
        var watch = ctx.WatchLevel?.Invoke() ?? ApproachWatchLevel.Full;
        // Sin senda publicada (VOR, NDB, LOC o RNAV sin ángulo): el piloto puede bajar por escalones; la senda de 3° es solo
        // una referencia (ADR-108). Sin "vas bajo" ni motor y al aire por altura, y "vas alto" con margen amplio.
        var path = HasPublishedPath(arrival);

        // 1. La final (con la aproximación autorizada): senda y puerta de estabilización.
        if (watch != ApproachWatchLevel.Off && _approachCleared && StraightIn(arrival) && fp is { } f && f.AlongNm > 0.5 && f.AlongNm <= 8 && Math.Abs(f.CrossNm) < 2 && Math.Abs(f.TrackErrorDeg) <= 45)
        {
            var dev = altFt - f.ProfileFt;
            if (!_gateChecked && f.AlongNm <= StableGateNm)
            {
                _gateChecked = true;
                var unstable = (path ? dev > 700 || dev < -500 : dev > 1200) || ias > 190 || Math.Abs(f.CrossNm) > 0.8 || Math.Abs(f.TrackErrorDeg) > 30;
                if (unstable && withTower && watch == ApproachWatchLevel.Full)
                {
                    log?.Invoke(Tr.F("{0}: no estabilizado a {1:0.0} NM (desvío {2:+0;-0} ft, {3:0} kt, eje {4:+0.0;-0.0} NM, rumbo {5:+0;-0}°): motor y al aire.", Callsign, f.AlongNm, dev, ias, f.CrossNm, f.TrackErrorDeg));
                    return GoAround(station, arrival, nowS, ordered: true);
                }
            }
            // Nivelado a la altitud del FAF antes de pasarlo es lo que dice la carta, aunque quede bajo la senda de 3° vista
            // desde lejos (vuelo RJTT-RJSS: falsa alerta a 1500 ft antes de SEKMO).
            if (path && !_lowAltitudeWarned && dev < -300 && f.AlongNm >= 1.5 && !AtOrAboveFafAltitude(arrival, altFt, f.AlongNm)
                && ctx.QnhHpa?.Invoke() is { } qnh)
            {
                _lowAltitudeWarned = true;
                return [Say(station, "atc.low_altitude_warning", new() { ["qnh"] = qnh.ToString(CultureInfo.InvariantCulture) })];
            }
            if (!_profileAdvised && dev > (path ? 500 : 1000) && f.AlongNm >= 3)
            {
                _profileAdvised = true;
                var miles = Math.Round(f.AlongNm);
                var should = Math.Round((arrival.Airport.ElevationFt + FinalGeometry.ThresholdCrossingFt
                                         + miles * FinalGeometry.FeetPerNm * Math.Tan(SlopeDeg(arrival) * GeoMath.DegToRad)) / 10) * 10;
                return [Say(station, "atc.altitude_should_be", new()
                {
                    ["distance"] = miles.ToString(CultureInfo.InvariantCulture), ["altitude"] = should.ToString(CultureInfo.InvariantCulture),
                })];
            }
        }
        if (withTower)
            return [];

        // 2. Velocidad: 250 kt bajo 10 000 ft; en la secuencia 210, 180 y 160 kt (Approach).
        if (ias is { } v)
        {
            int? want = null;
            if (altFt < 9800 && v > Below10kKt + SpeedToleranceKt && (_speedKt ?? 999) > Below10kKt && Phase != PilotPhase.Arrival && _descentGiven)
                want = (int)Below10kKt;
            else if (Phase == PilotPhase.Arrival && !_missedApproach)
            {
                if (fp is { Established: true } e && e.AlongNm <= 10 && v > 160 + SpeedToleranceKt && (_speedKt ?? 999) > 160)
                    want = 160;
                // En la final: 180 kt establecido a menos de 14 NM (y 160 a menos de 10). Fuera de la final manda la carta: ya
                // no se pide 210 kt por distancia (con ≤230 publicado en OVRID el ATC pedía 210 y 180 antes; RJTT-RJSS).
                else if (fp is { Established: true } e2 && e2.AlongNm <= 14 && v > 180 + SpeedToleranceKt && (_speedKt ?? 999) > 180)
                    want = 180;
                else if (altFt < 9800 && v > Below10kKt + SpeedToleranceKt && (_speedKt ?? 999) > Below10kKt)
                    want = (int)Below10kKt;
            }
            // Secuencia (ADR-107): muy pegado al que aterriza antes y lejos de la final, un 360°; pegado, más lento.
            if (Phase == PilotPhase.Arrival && !_missedApproach && ctx.LeaderAhead?.Invoke() is { } lead && lead.GapNm > 0
                && fp is not { Established: true, AlongNm: <= 8 })
            {
                if (lead.GapNm < SequenceOrbitNm && thrNm > 10 && thrNm <= SequenceMaxNm && altFt < arrival.Airport.ElevationFt + SequenceMaxAglFt
                    && !_sequenceOrbitGiven)
                {
                    _sequenceOrbitGiven = true;
                    log?.Invoke(Tr.F("{0}: a {1:0.0} NM del {2} que aterriza antes: viraje de 360° por secuencia.", Callsign, lead.GapNm, lead.Type));
                    return [Orbit(station, fp is { CrossNm: >= 0 }, nowS, sequencing: true)];
                }
                if (lead.GapNm < SequenceGapNm && thrNm <= SequenceMaxNm && altFt < arrival.Airport.ElevationFt + SequenceMaxAglFt)
                {
                    var slower = v > 180 + SpeedToleranceKt ? 180 : v > 160 + SpeedToleranceKt ? 160 : 0;
                    if (slower > 0 && (_speedKt ?? 999) > slower && (want is null || want > slower))
                        want = slower;
                }
            }
            // Excede una velocidad publicada en el fijo que tiene delante: la de la carta.
            if (want is null && Phase == PilotPhase.Arrival && !_missedApproach && ChartSpeedAhead(arrival) is { } chart
                && v > chart + SpeedToleranceKt && (_speedKt ?? 999) > chart)
                want = (int)chart;
            var slowing = Decelerating(v, nowS);
            // Si ya viene desacelerando hacia esa velocidad, no se le dice nada todavía.
            if (want is { } w && !(slowing && v < w + 40))
                return [ReduceSpeed(station, w, nowS)];
            // Sigue rápido un minuto después de la instrucción y no está desacelerando: se la repite una vez.
            if (_speedKt is { } s && !_speedRepeated && !slowing && v > s + 2 * SpeedToleranceKt && nowS - _speedAtS > 60)
            {
                _speedRepeated = true;
                return [ReduceSpeed(station, s, nowS)];
            }
        }

        // 3. Demasiado alto para bajar a tiempo (antes de la autorización de aproximación).
        if (watch != ApproachWatchLevel.Off && Phase == PilotPhase.Arrival && !_approachCleared && !_missedApproach && _vector == VectorStage.None
            && altFt > Math.Max(arrival.ApproachAltitudeFt + 1000, arrival.InitialAltitudeFt + 500))
        {
            var (required, available) = DescentMiles(arrival, altFt);
            var vs = ctx.VerticalSpeedFpm?.Invoke();
            if (!_orbitGiven && available < required - OrbitShortNm && thrNm <= ApproachClearanceNm + 7)
            {
                _orbitGiven = true;
                var right = fp is { CrossNm: >= 0 };
                return [Orbit(station, right, nowS)];
            }
            if (!_expediteGiven && available < required - TooHighNm && (vs is null || vs > -2000) && _clearedFt is { } target && target < altFt - 1000)
            {
                _expediteGiven = true;
                var level = LevelText(target, arrival.TransitionAltitudeFt);
                return [FlightInstruction(station, "atc.expedite_descent", new() { ["level"] = level }, new ExpectedReadback(Callsign) { LevelFt = target },
                    "pilot.readback_expedite_descent", new() { ["level"] = level }, nowS)];
            }
        }
        return [];
    }

    /// <summary>
    /// ¿Va antes del FAF y a su altitud publicada o más (con 150 ft de tolerancia)? Entonces no está "bajo": espera la senda.
    /// </summary>
    private static bool AtOrAboveFafAltitude(ArrivalInfo arrival, double altFt, double alongNm)
    {
        if (arrival.Procedure is not { } a || LandingEnd(arrival) is not { } end)
            return false;
        foreach (var leg in a.FinalLegs)
        {
            if (!leg.IsFaf || leg.Altitude1Ft <= 0 || leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0)
                continue;
            var fafNm = GeoMath.DistanceM(leg.FixPosition, end.Threshold) / Nm;
            return alongNm >= fafNm - 0.3 && altFt >= leg.Altitude1Ft - 150;
        }
        return false;
    }

    /// <summary>
    /// La mayor velocidad publicada (kt) en un fijo de la aproximación que tiene cerca por delante (a menos de 6 NM), o null:
    /// si la carta dice ≤230 kt en OVRID, el ATC no pide 210 antes (PLAN-PULIDO bloque 2: manda la carta).
    /// </summary>
    private double? ChartSpeedAhead(ArrivalInfo arrival)
    {
        if (arrival.Procedure is not { } a || ctx.PilotPosition() is not { } here)
            return null;
        double? best = null;
        void Look(IReadOnlyList<ProcedureLeg> legs)
        {
            foreach (var leg in legs)
            {
                if (leg.SpeedLimitKt <= 0 || leg.SpeedConstraint is not (SpeedConstraint.At or SpeedConstraint.AtOrBelow)
                    || leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0)
                    continue;
                if (GeoMath.DistanceM(here, leg.FixPosition) < 6 * Nm)
                    best = Math.Max(best ?? 0, leg.SpeedLimitKt);
            }
        }
        foreach (var t in a.Transitions)
        {
            if (t.Name == arrival.Transition)
                Look(t.Legs);
        }
        Look(a.FinalLegs);
        return best;
    }

    // Muestra de velocidad para saber si ya está desacelerando (no se repite la instrucción mientras baja).
    private double _iasSample = double.NaN;
    private double _iasSampleAtS;

    private bool Decelerating(double ias, double nowS)
    {
        if (double.IsNaN(_iasSample) || nowS - _iasSampleAtS > 40)
        {
            _iasSample = ias;
            _iasSampleAtS = nowS;
            return false;
        }
        var slowing = ias < _iasSample - 4;
        if (nowS - _iasSampleAtS >= 15)
        {
            _iasSample = ias;
            _iasSampleAtS = nowS;
        }
        return slowing;
    }

    private AtcTransmission ReduceSpeed(AtcStation station, int kt, double nowS)
    {
        if (_speedKt != kt)
            _speedRepeated = false;
        _speedKt = kt;
        _speedAtS = nowS;
        var speed = kt.ToString(CultureInfo.InvariantCulture);
        return FlightInstruction(station, "atc.reduce_speed", new() { ["speed"] = speed }, new ExpectedReadback(Callsign) { SpeedKt = kt },
            "pilot.readback_reduce_speed", new() { ["speed"] = speed }, nowS);
    }

    private AtcTransmission Orbit(AtcStation station, bool right, double nowS, bool sequencing = false)
    {
        var dir = ctx.Language == AtcLanguage.Spanish ? right ? "derecha" : "izquierda" : right ? "right" : "left";
        if (!sequencing)
            log?.Invoke(Tr.F("{0}: demasiado alto para la aproximación: viraje de 360° por la {1}.", Callsign, (right ? "derecha" : "izquierda")));
        return FlightInstruction(station, sequencing ? "atc.orbit_for_sequencing" : "atc.orbit_for_descent", new() { ["direction"] = dir },
            new ExpectedReadback(Callsign) { Words = ["sixty", "sesenta", "360", "orbit", "viraje", "right", "left", "derecha", "izquierda"] },
            "pilot.readback_orbit_for_descent", new() { ["direction"] = dir }, nowS);
    }

    // ---------- Autorización y paso a la Torre ----------

    /// <summary>
    /// ¿Ya se lo puede autorizar a la aproximación? Normalmente a <see cref="ApproachClearanceNm"/> del umbral (o establecido
    /// en la final) y sin ir demasiado alto. Después de una frustrada, recién cuando se reintegró: establecido, a menos de 4 NM
    /// del fijo del directo, o un rato en la espera (antes: lo autorizaba y lo pasaba a la Torre 30 s después del motor y al aire).
    /// </summary>
    /// <summary>
    /// Las próximas restricciones publicadas (hasta 4) de la STAR, la transición y la final hasta el FAF, para el panel:
    /// "ORKID ≥5000 · SNOOK ≥4000 · OVRID ≥1500 ≤230 kt". Vacío sin posición o sin restricciones por delante.
    /// </summary>
    private string ChartAhead(ArrivalInfo arrival)
    {
        if (ctx.PilotPosition() is not { } here)
            return "";
        var to = LandingEnd(arrival)?.Threshold ?? arrival.Airport.Reference;
        var mine = GeoMath.DistanceM(here, to);
        var sb = new System.Text.StringBuilder();
        var count = 0;
        string? lastFix = null;
        void Add(string fix, string text)
        {
            if (count >= 4 || text.Length == 0 || fix == lastFix)
                return;
            sb.Append(count++ > 0 ? " · " : "").Append(fix).Append(' ').Append(text);
            lastFix = fix;
        }
        foreach (var r in arrival.StarRestrictions)
        {
            if (GeoMath.DistanceM(r.Position, to) < mine - 1 * Nm)
                Add(r.Fix, $"≥{r.MinFt:0}");
        }
        if (arrival.Procedure is { } a)
        {
            void Legs(IReadOnlyList<ProcedureLeg> legs)
            {
                foreach (var leg in legs)
                {
                    if (leg.FixIcao.Length == 0 || leg.FixIcao.StartsWith("RW", StringComparison.Ordinal)
                        || leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0
                        || GeoMath.DistanceM(leg.FixPosition, to) >= mine - 0.5 * Nm)
                        continue;
                    var alt = leg.Altitude1Ft <= 0 ? "" : leg.AltitudeConstraint switch
                    {
                        AltitudeConstraint.AtOrAbove => $"≥{leg.Altitude1Ft:0}",
                        AltitudeConstraint.AtOrBelow => $"≤{leg.Altitude1Ft:0}",
                        AltitudeConstraint.Between when leg.Altitude2Ft > 0 =>
                            $"{Math.Min(leg.Altitude1Ft, leg.Altitude2Ft):0}–{Math.Max(leg.Altitude1Ft, leg.Altitude2Ft):0}",
                        AltitudeConstraint.None => "",
                        _ => $"{leg.Altitude1Ft:0}",
                    };
                    var spd = leg.SpeedLimitKt <= 0 ? "" : leg.SpeedConstraint switch
                    {
                        SpeedConstraint.AtOrBelow => Tr.F("≤{0:0} kt", leg.SpeedLimitKt),
                        SpeedConstraint.AtOrAbove => Tr.F("≥{0:0} kt", leg.SpeedLimitKt),
                        SpeedConstraint.At => $"{leg.SpeedLimitKt:0} kt",
                        _ => "",
                    };
                    Add(leg.FixIcao, alt.Length > 0 && spd.Length > 0 ? alt + " " + spd : alt + spd);
                    if (leg.IsFaf)
                        return;
                }
            }
            foreach (var t in a.Transitions)
            {
                if (t.Name == arrival.Transition)
                    Legs(t.Legs);
            }
            Legs(a.FinalLegs);
        }
        return sb.ToString();
    }

    /// <summary>A menos de esto (NM) del IAF de su transición ya se lo autoriza a la aproximación. APROXIMADO.</summary>
    public const double IafClearanceNm = 5;

    private bool ReadyForApproach(ArrivalInfo arrival, double altFt, double nowS)
    {
        if (!_approachExpected || _approachCleared || arrival.ApproachName is null)
            return false;
        var fp = Final(arrival);
        // Con vectores, la autorización va con el rumbo de interceptación (o si ya se estableció por su cuenta).
        if (_vector != VectorStage.None)
            return fp is { Established: true, AlongNm: <= 20 };
        // A la altitud del IAF (la que le dieron) va como dice la carta: no está "alto" (ADR-122).
        if (altFt > Math.Max(arrival.ApproachAltitudeFt + 1000, arrival.InitialAltitudeFt + 500) && !_missedApproach)
        {
            var (required, available) = DescentMiles(arrival, altFt);
            if (available < required - TooHighNm && fp is not { Established: true })
                return false; // primero que baje (lo vigila Watch)
        }
        if (fp is { Established: true } f && f.AlongNm <= 20)
            return true;
        // Sin transición que empalme (ADR-146): solo si viene alineado hacia la final y cerca; si no, lo llevan los vectores.
        // Antes se lo autorizaba a 18 NM aunque cruzara la final a 90° con la discontinuidad en el FMS.
        if (!_missedApproach && Unconnected(arrival))
            return fp is { } g && InboundToFinal(arrival, g) && g.AlongNm <= ApproachClearanceNm;
        if (!_missedApproach)
        {
            // Llegando al IAF de su transición, o a ApproachClearanceNm por la ruta (no en línea recta: con la transición
            // que da la vuelta se autorizaba antes de tiempo).
            if (arrival.IafPosition is { } iaf && ctx.PilotPosition() is { } at && GeoMath.DistanceM(at, iaf) <= IafClearanceNm * Nm)
                return true;
            var route = RouteToThresholdNm(arrival, out var known);
            return Math.Max(ThresholdNm(arrival), known ? route : 0) <= ApproachClearanceNm;
        }
        if (!_reintegrationGiven)
            return false;
        if (_reintegrationAt is { } fix && ctx.PilotPosition() is { } here)
        {
            var d = GeoMath.DistanceM(here, fix) / Nm;
            if (!_reintegrationHold)
                return d <= 4;
            if (d <= 3 && double.IsNaN(_reachedFixAtS))
                _reachedFixAtS = nowS;
            return !double.IsNaN(_reachedFixAtS) && nowS - _reachedFixAtS >= HoldMinS;
        }
        // Sin fijo conocido: cuando se alejó y pasó un rato (vuelve por su cuenta).
        return nowS - _missedAtS >= 180 && ThresholdNm(arrival) >= 8;
    }

    /// <summary>
    /// ¿Se lo pasa a la Torre? Autorizado a la aproximación y establecido a menos de 10 NM; sin geometría de la pista, a
    /// <see cref="TowerHandoffNm"/> del aeropuerto. Nunca alejándose (rumbo opuesto a la pista: subiendo en la frustrada).
    /// </summary>
    private bool ReadyForTower(ArrivalInfo arrival, double destNm)
    {
        if (!_approachCleared && arrival.ApproachName is not null)
            return false;
        if (Final(arrival) is not { } f)
            return destNm <= TowerHandoffNm;
        if (f.Established && f.AlongNm <= 10)
            return true;
        return destNm <= TowerHandoffNm && f.AlongNm > 0 && Math.Abs(f.TrackErrorDeg) <= 90 && !(_missedApproach && f.AlongNm > 6);
    }

    // ---------- Motor y al aire ----------

    /// <summary>Lo de la frustrada en la carta: si hay procedimiento publicado, su altitud y el fijo de espera (si termina en una).</summary>
    private static (bool Published, double AltitudeFt, string? HoldFix, GeoPoint? HoldAt) MissedOf(ArrivalInfo arrival)
    {
        var legs = arrival.Procedure?.MissedLegs ?? [];
        var alt = 0.0;
        string? hold = null;
        GeoPoint? holdAt = null;
        foreach (var leg in legs)
        {
            alt = Math.Max(alt, Math.Max(leg.Altitude1Ft, leg.Altitude2Ft));
            if (leg.Type is LegType.HM or LegType.HA or LegType.HF && leg.FixIcao.Length > 0)
            {
                hold = leg.FixIcao;
                holdAt = leg.FixPosition;
            }
        }
        if (alt <= 0)
            alt = arrival.ApproachAltitudeFt;
        alt = Math.Max(Math.Round(alt / 100) * 100, Math.Ceiling((arrival.Airport.ElevationFt + 1500) / 100) * 100);
        return (legs.Count > 0, alt, hold, holdAt);
    }

    /// <summary>El fijo para volver a la aproximación sin espera: el IF de la carta (o el IAF, o el primero con nombre).</summary>
    private static (string Fix, GeoPoint At)? ApproachStartFix(ArrivalInfo arrival)
    {
        if (arrival.Procedure is not { } a)
            return null;
        ProcedureLeg? best = null;
        foreach (var leg in a.FinalLegs)
        {
            if (leg.FixIcao.Length == 0 || leg.FixIcao.StartsWith("RW", StringComparison.Ordinal))
                continue;
            if (leg.IsIf)
                return (leg.FixIcao, leg.FixPosition);
            if (leg.IsIaf && best is not { IsIaf: true })
                best = leg;
            else
                best ??= leg;
        }
        return best is null ? null : (best.FixIcao, best.FixPosition);
    }

    /// <summary>
    /// Motor y al aire (lo avisa el piloto o lo ordena la Torre): la Torre le da la frustrada publicada (o, sin publicada,
    /// "climb to (altitud), fly runway heading") y lo pasa a la Aproximación, que lo reintegra (ADR-105).
    /// </summary>
    private IReadOnlyList<AtcTransmission> GoAround(AtcStation tower, ArrivalInfo arrival, double nowS, bool ordered)
    {
        _approachCleared = false;
        _landingCleared = false;
        _continueGiven = false;
        _approachExpected = arrival.ApproachName is not null;
        _missedApproach = true;
        ResetVectors();
        _missedAtS = nowS;
        _reintegrationGiven = false;
        _reachedFixAtS = double.NaN;
        _speedKt = null;
        _speedRepeated = false;
        ResetFinalWatch();
        Phase = PilotPhase.Arrival;
        var (published, altFt, holdFix, holdAt) = MissedOf(arrival);
        if (holdFix is not null)
        {
            (_reintegrationFix, _reintegrationAt, _reintegrationHold) = (holdFix, holdAt, true);
        }
        else if (ApproachStartFix(arrival) is { } start)
        {
            (_reintegrationFix, _reintegrationAt, _reintegrationHold) = (start.Fix, start.At, false);
        }
        else
        {
            (_reintegrationFix, _reintegrationAt, _reintegrationHold) = (null, null, false);
        }
        _clearedFt = altFt;
        log?.Invoke(Tr.F("{0}: motor y al aire ({1}); frustrada {2} a {3:0} ft", Callsign, (ordered ? Tr.T("ordenado por la Torre") : Tr.T("del piloto")), (published ? "publicada" : Tr.T("sin publicar")), altFt)
                    + (_reintegrationFix is { } rf ? Tr.F(", reintegro {0} {1}.", (_reintegrationHold ? Tr.T("en espera sobre") : Tr.T("directo a")), rf) : "."));
        var list = new List<AtcTransmission>();
        if (ordered)
            list.Add(Say(tower, "atc.go_around", []));
        var next = arrival.Stations.For(AtcRole.Approach) ?? _approachStation;
        var contact = next is not null && next.Khz != tower.Khz;
        var level = LevelText(altFt, arrival.TransitionAltitudeFt);
        var f = new Dictionary<string, string> { ["level"] = level };
        if (contact)
        {
            f["next_station"] = next!.Callsign;
            f["frequency"] = next.FrequencyText;
            _handoffTo = next;
            _enroute = next;
        }
        var id = (published ? "missed_approach_published" : "missed_approach_climb") + (contact ? "_contact" : "");
        var expected = new ExpectedReadback(Callsign)
        {
            FrequencyKhz = contact ? next!.Khz : null, LevelFt = published ? null : altFt,
            Words = ["missed", "published", "frustrada", "publicada", "procedimiento"],
        };
        list.Add(FlightInstruction(tower, "atc." + id, f, expected, "pilot.readback_" + id, new Dictionary<string, string>(f), nowS));
        return list;
    }

    /// <summary>
    /// Después de la frustrada, la Aproximación (o la misma dependencia) lo reintegra: espera publicada sobre el fijo de la
    /// frustrada o directo al inicio de la aproximación, y la aproximación que se espera.
    /// </summary>
    private IReadOnlyList<AtcTransmission> Reintegration(AtcStation station, ArrivalInfo arrival, bool checkIn, double nowS)
    {
        _reintegrationGiven = true;
        var list = new List<AtcTransmission>();
        if (checkIn)
            list.Add(Say(station, "atc.identified_no_level", []));
        if (_reintegrationFix is { } fix)
        {
            var levelFt = _reintegrationHold ? _clearedFt ?? arrival.ApproachAltitudeFt : arrival.ApproachAltitudeFt;
            _clearedFt = levelFt;
            var level = LevelText(levelFt, arrival.TransitionAltitudeFt);
            var f = new Dictionary<string, string> { ["fix"] = fix, ["level"] = level };
            list.Add(_reintegrationHold
                ? FlightInstruction(station, "atc.hold_published", f, new ExpectedReadback(Callsign) { LevelFt = levelFt },
                    "pilot.readback_hold_published", new Dictionary<string, string>(f), nowS)
                : FlightInstruction(station, "atc.direct_maintain", f, new ExpectedReadback(Callsign) { Fix = fix, LevelFt = levelFt },
                    "pilot.readback_direct_maintain", new Dictionary<string, string>(f), nowS));
        }
        else if (arrival.ApproachName is not null && VectorStep(station, arrival, ctx.AltitudeFt?.Invoke() ?? 0, nowS, start: true) is { } vector)
        {
            // Sin espera publicada ni fijo de reintegro: vectores a la final (ADR-123). La frase ya dice la aproximación.
            list.Add(vector);
            return list;
        }
        if (arrival.ApproachName is not null)
        {
            _approachExpected = true;
            list.Add(ExpectApproach(station, arrival));
        }
        return list;
    }

    /// <summary>El paso con la Aproximación, en español: qué hacer ahora según la frustrada, la altitud y la autorización.</summary>
    private string ArrivalStepText(ArrivalInfo arrival)
    {
        var app = arrival.ApproachName ?? Tr.T("aproximación");
        var via = arrival.Transition is { } t ? Tr.F(" vía {0}", t) : "";
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        if (MissedApproachActive)
        {
            var (published, missedFt, _, _) = MissedOf(arrival);
            var climb = published ? Tr.F("Vuela la frustrada publicada y sube a {0:0} ft", missedFt) : Tr.F("Sube a {0:0} ft con rumbo de pista", missedFt);
            if (!_reintegrationGiven)
                return Tr.F("{0}. La Aproximación te dirá cómo volver.", climb);
            return _reintegrationFix is { } fix
                ? _reintegrationHold
                    ? Tr.F("{0} y entra en espera sobre {1} según lo publicado. Prepara otra vez la {2} de la {3}: te autorizan desde la espera.", climb, fix, app, arrival.Runway)
                    : Tr.F("Ve directo a {0} a {1:0} ft y prepara otra vez la {2} de la {3}{4}: te autorizan al llegar.", fix, _clearedFt ?? arrival.ApproachAltitudeFt, app, arrival.Runway, via)
                : Vectoring || AssignedVector is not null
                    ? Tr.F("Sigue los rumbos de la Aproximación: te lleva a la final de la {0} y te autoriza al interceptar.", arrival.Runway)
                    : Tr.F("{0} y vuelve a la final de la {1}: te autorizan cuando estés alineado.", climb, arrival.Runway);
        }
        if (Vectoring)
            return Tr.F("Sigue los rumbos de la Aproximación: te lleva a la final de la {0} y te autoriza la {1} al interceptar.", arrival.Runway, app);
        if (_approachCleared)
            return Tr.F("Autorizado a la {0} de la {1}{2}. Intercepta la final y baja por la senda; te pasarán a la Torre.", app, arrival.Runway, via);
        var (required, available) = DescentMiles(arrival, alt);
        if (alt > arrival.ApproachAltitudeFt + 1000 && available < required - TooHighNm)
            return Tr.F("Vas alto: te faltan unas {0:0} NM para bajar. Baja más rápido (aerofrenos) o pide más distancia; ", required - available)
                   + Tr.F("la Aproximación te autoriza la {0} cuando puedas hacerla.", app);
        return Tr.F("Baja a la altitud autorizada y prepara la {0} de la {1}{2} (inicio a {3:0} ft). ", app, arrival.Runway, via, arrival.ApproachAltitudeFt)
               + Tr.F("Te autorizan a unas {0:0} NM del umbral.", ApproachClearanceNm);
    }

    // ---------- Panel "Instrucciones vigentes" ----------

    /// <summary>
    /// Lo que el piloto tiene autorizado y lo que conviene vigilar ahora, en español para el panel (ADR-105): dependencia,
    /// altitud con QNH, velocidad, llegada, aproximación, espera o directo, frustrada y la senda en la final.
    /// </summary>
    public IReadOnlyList<PilotOrder> Orders()
    {
        if (Phase < PilotPhase.Airborne || Phase >= PilotPhase.TaxiIn)
            return [];
        var list = new List<PilotOrder>();
        var arrival = Arrival;
        var alt = ctx.AltitudeFt?.Invoke() ?? 0;
        var ta = Phase >= PilotPhase.Arrival || _descentGiven ? DestTa ?? OriginTa : OriginTa;
        if (ExpectedStation is { } st)
            list.Add(new(Tr.T("Frecuencia"), $"{st.Callsign} {st.FrequencyText}{(_awaitingCheckIn is not null ? " · llámala" : "")}"));
        if (_clearedFt is { } c)
        {
            var below = c < (ta ?? 18_000);
            var text = below ? $"{c:0} ft" : $"FL{c / 100:000}";
            if (below && ctx.QnhHpa?.Invoke() is { } qnh && (Phase >= PilotPhase.Arrival || _descentGiven))
                text += Tr.F(" · QNH {0}", qnh);
            if (_viaStar && Phase is PilotPhase.EnRoute or PilotPhase.Arrival && !_approachCleared)
                text += Tr.T(" · vía STAR (cumple sus restricciones)");
            if (_expediteGiven && alt > c + 500)
                text += Tr.T(" · acelera el descenso");
            list.Add(new(Tr.T("Altitud"), text));
        }
        if (_speedKt is { } spd)
            list.Add(new(Tr.T("Velocidad"), Tr.F("máx. {0} kt", spd)));
        else if (Phase is PilotPhase.EnRoute or PilotPhase.Arrival && alt < 10_500 && _descentGiven)
            list.Add(new(Tr.T("Velocidad"), Tr.T("máx. 250 kt bajo 10 000 ft")));
        if (AssignedVector is { } vec)
            list.Add(new(Tr.T("Rumbo"), Tr.F("{0:000}° · vectores a la final", vec.MagneticDeg)));
        if (_directFix is { } dfix && Phase is PilotPhase.WithApproach or PilotPhase.EnRoute)
            list.Add(new(Tr.T("Ruta"), Tr.F("directo a {0}", dfix)));
        if (arrival is null || Phase < PilotPhase.EnRoute)
            return list;
        if (arrival.StarName is { } star)
            list.Add(new(Tr.T("Llegada"), star));
        if (arrival.ApproachName is { } app)
        {
            var state = _approachCleared ? "autorizada" : _approachExpected ? "esperada" : Tr.T("por confirmar");
            var via = arrival.Transition is { } t ? Tr.F(" vía {0}", t) : "";
            list.Add(new(Tr.T("Aproximación"), Tr.F("{0} pista {1}{2} ({3}) · inicio a {4:0} ft", app, arrival.Runway, via, state, arrival.ApproachAltitudeFt)));
        }
        if (Phase is PilotPhase.EnRoute or PilotPhase.Arrival or PilotPhase.ArrivalTower && !MissedApproachActive && ChartAhead(arrival) is { Length: > 0 } chart)
            list.Add(new(Tr.T("Carta"), chart));
        else
        {
            list.Add(new(Tr.T("Pista"), arrival.Runway));
        }
        if (MissedApproachActive)
        {
            var (published, missedFt, _, _) = MissedOf(arrival);
            list.Add(new(Tr.T("Frustrada"), published ? Tr.F("publicada · sube a {0:0} ft", missedFt) : Tr.F("rumbo de pista · sube a {0:0} ft", missedFt)));
            if (_reintegrationGiven && _reintegrationFix is { } rf)
                list.Add(new(_reintegrationHold ? Tr.T("Espera") : Tr.T("Directo"), _reintegrationHold ? Tr.F("sobre {0} según lo publicado", rf) : $"a {rf}"));
        }
        if (Phase is PilotPhase.Arrival or PilotPhase.ArrivalTower && Final(arrival) is { } f && f.AlongNm is > 0.5 and <= 15 && Math.Abs(f.CrossNm) < 3)
        {
            var dev = alt - f.ProfileFt;
            var how = Math.Abs(dev) < 200 ? Tr.T("en la senda") : dev > 0 ? Tr.F("{0:0} ft alto", dev) : Tr.F("{0:0} ft bajo", -dev);
            list.Add(new(HasPublishedPath(arrival) ? Tr.T("Senda") : Tr.T("Senda (ref. 3°)"),
                Tr.F("a {0:0.0} NM: {1:0} ft (vas a {2:0} ft, {3})", f.AlongNm, Math.Round(f.ProfileFt / 10) * 10, alt, how)));
        }
        return list;
    }
}
