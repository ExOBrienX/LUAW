using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// Vectores a la final (PLAN-PULIDO bloque 4, ADR-123; ADR-146 para todos los casos): cuando no hay un procedimiento que lo
/// lleve a la final (frustrada sin espera ni fijo de reintegro, o llegada sin transición que empalme: la "discontinuidad"
/// del FMS), la Aproximación da rumbos. Según dónde esté: lejos, directo a un punto de entrada en la prolongación del eje;
/// al costado o detrás, alejamiento ("turn left heading 090, vectoring for ILS approach runway 27"); después base y un
/// rumbo de interceptación de 30° con la autorización. Si ya viene alineado y convergiendo con la final, no hacen falta.
/// Nunca baja de la altitud de los tramos iniciales de la aproximación hasta la base. Rumbos magnéticos, redondeados a 5°.
/// </summary>
public sealed partial class PilotSession
{
    private enum VectorStage { None, Join, Downwind, Base }

    private VectorStage _vector;
    private int? _vectorHeadingMag;
    private double _vectorTrueDeg;
    private double _vectorAtS;
    private GeoPoint? _vectorTarget;

    /// <summary>Más lejos que esto (NM) del umbral, primero directo al punto de entrada en la prolongación del eje.</summary>
    public const double VectorJoinNm = 25;

    /// <summary>El punto (en la prolongación del eje o en el alejamiento) hacia donde lo llevan los vectores, para dibujarlo.</summary>
    public GeoPoint? VectorTarget => Vectoring && Phase == PilotPhase.Arrival ? _vectorTarget : null;

    /// <summary>NM antes del umbral, medido sobre el eje, donde se vira a base (mínimo; más si va alto). APROXIMADO.</summary>
    public const double VectorBaseNm = 9;

    /// <summary>NM del eje a los que se da el rumbo de interceptación en la base. APROXIMADO.</summary>
    public const double VectorInterceptNm = 3;

    /// <summary>Ángulo de interceptación de la final (Doc 4444 8.9.3.6: 45° o menos; se usan 30°).</summary>
    public const double VectorInterceptDeg = 30;

    /// <summary>El rumbo asignado por el ATC (magnético para decirlo, verdadero para dibujarlo), o null sin vectores.</summary>
    public (int MagneticDeg, double TrueDeg)? AssignedVector =>
        _vectorHeadingMag is { } m && Phase == PilotPhase.Arrival && !InterceptDone() ? (m, _vectorTrueDeg) : null;

    /// <summary>
    /// El rumbo de interceptación vale "hasta establecerse" (Doc 4444 8.9.3.6): establecido en la final ya no hay rumbo
    /// asignado (vuelo de Marco a KEWR, 2026-11-15: la línea del rumbo seguía en el radar hasta el aterrizaje).
    /// </summary>
    private bool InterceptDone() => !Vectoring && ctx.Arrival?.Invoke() is { } a && Final(a) is { Established: true };

    /// <summary>
    /// El recorrido que esperan los vectores, para dibujarlo en el radar (ADR-146): desde el avión por el rumbo asignado hasta
    /// el punto de entrada o de la base, de ahí a la interceptación en el eje y por la final hasta el umbral. Null sin vectores.
    /// </summary>
    public IReadOnlyList<GeoPoint>? VectorPath
    {
        get
        {
            if (AssignedVector is null || ctx.PilotPosition() is not { } here || ctx.Arrival?.Invoke() is not { } arrival
                || LandingEnd(arrival) is not { } end)
                return null;
            var back = GeoMath.NormalizeDeg(end.CourseDeg + 180);
            var path = new List<GeoPoint> { here };
            if (_vectorTarget is { } target)
            {
                path.Add(target);
                var t = FinalGeometry.Of(end.Threshold, end.CourseDeg, end.ElevationFt, 3, target, end.CourseDeg);
                // Donde intercepta el eje: un poco más cerca del umbral que el punto (rumbo de 30°), salvo que ya esté en él.
                var along = Math.Max(4, t.AlongNm - Math.Abs(t.CrossNm) / Math.Tan(VectorInterceptDeg * GeoMath.DegToRad));
                if (Math.Abs(t.CrossNm) > 0.3)
                    path.Add(GeoMath.Destination(end.Threshold, back, along * Nm));
            }
            else if (!Vectoring && Final(arrival) is { } fp)
            {
                // Rumbo de interceptación dado: por el rumbo hasta cortar el eje, y la final.
                var angle = Math.Max(10, Math.Abs(GeoMath.AngleDiffDeg(_vectorTrueDeg, end.CourseDeg)));
                var along = Math.Max(1, fp.AlongNm - Math.Abs(fp.CrossNm) / Math.Tan(angle * GeoMath.DegToRad));
                path.Add(GeoMath.Destination(end.Threshold, back, along * Nm));
            }
            else
            {
                path.Add(GeoMath.Destination(here, _vectorTrueDeg, 8 * Nm));
            }
            path.Add(end.Threshold);
            return path;
        }
    }

    /// <summary>¿Está recibiendo vectores a la final?</summary>
    public bool Vectoring => _vector != VectorStage.None;

    private void ResetVectors()
    {
        _vector = VectorStage.None;
        _vectorHeadingMag = null;
        _vectorTarget = null;
    }

    /// <summary>Llegada sin transición de la aproximación que empalme con su ruta (la discontinuidad del FMS).</summary>
    private static bool Unconnected(ArrivalInfo arrival) => arrival.ApproachName is not null && arrival.Transition is null;

    /// <summary>
    /// ¿Viene hacia la final por delante del umbral, dentro de un cono de 30° del eje, con rumbo a 45° o menos de la final y
    /// sin alejarse del eje? Entonces intercepta solo: no necesita vectores (y se lo puede autorizar cerca).
    /// </summary>
    private bool InboundToFinal(ArrivalInfo arrival, FinalPosition fp)
    {
        if (fp.AlongNm < 4 || Math.Abs(fp.TrackErrorDeg) > 45
            || Math.Abs(fp.CrossNm) > Math.Max(1, fp.AlongNm * Math.Tan(VectorInterceptDeg * GeoMath.DegToRad)))
            return false;
        if (Math.Abs(fp.CrossNm) <= 0.6 || ctx.PilotPosition() is not { } here || ctx.HeadingDeg?.Invoke() is not { } hdg
            || LandingEnd(arrival) is not { } end)
            return true;
        // Un poco más adelante por su rumbo: ¿se acerca al eje?
        var ahead = FinalGeometry.Of(end.Threshold, end.CourseDeg, end.ElevationFt, 3, GeoMath.Destination(here, hdg, 0.5 * Nm), hdg);
        return Math.Abs(ahead.CrossNm) <= Math.Abs(fp.CrossNm) + 0.02;
    }

    /// <summary>¿Ya no le quedan fijos de su ruta por delante (terminó la STAR o el plan)?</summary>
    private bool RouteFinished()
    {
        if (ctx.PilotPosition() is not { } here)
            return false;
        var fixes = ctx.Plan.Fixes;
        var nearest = -1;
        var best = double.MaxValue;
        for (var i = 0; i < fixes.Count; i++)
        {
            if (fixes[i].IsPseudo || string.Equals(fixes[i].Ident, ctx.Plan.Destination.Icao, StringComparison.OrdinalIgnoreCase))
                continue;
            var d = GeoMath.DistanceM(here, fixes[i].Position);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }
        if (nearest < 0)
            return true;
        for (var i = nearest + 1; i < fixes.Count; i++)
        {
            if (!fixes[i].IsPseudo && !string.Equals(fixes[i].Ident, ctx.Plan.Destination.Icao, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        // El último fijo: terminó si ya lo pasó o le queda poco.
        return best <= 3 * Nm || nearest == fixes.Count - 1;
    }

    /// <summary>
    /// La altitud más baja para los vectores antes de la base: la más alta publicada en los tramos iniciales de la carta
    /// (transiciones cerca del aeropuerto y final hasta el FAF), que la carta da libre de obstáculos en esa zona; nunca menos
    /// que la de la final. Con la base, la de la final.
    /// </summary>
    public static double VectorFloorFt(ArrivalInfo arrival)
    {
        var floor = arrival.ApproachAltitudeFt;
        if (arrival.Procedure is not { } a)
            return floor;
        var reference = arrival.Airport.Reference;
        void Take(ProcedureLeg leg)
        {
            if (leg.Altitude1Ft <= 0 || leg.AltitudeConstraint is AltitudeConstraint.None or AltitudeConstraint.AtOrBelow)
                return;
            if (leg.FixPosition is { LatDeg: not 0 } p && GeoMath.DistanceM(p, reference) > 25 * Nm)
                return;
            var alt = leg.AltitudeConstraint == AltitudeConstraint.Between && leg.Altitude2Ft > 0 ? Math.Min(leg.Altitude1Ft, leg.Altitude2Ft) : leg.Altitude1Ft;
            floor = Math.Max(floor, alt);
        }
        foreach (var t in a.Transitions)
        {
            foreach (var leg in t.Legs)
                Take(leg);
        }
        foreach (var leg in a.FinalLegs)
        {
            Take(leg);
            if (leg.IsFaf)
                break;
        }
        return floor;
    }

    /// <summary>Dónde se vira a base: lo que necesita para bajar a 3° desde su altitud, entre 9 y 18 NM.</summary>
    private static double BaseTurnNm(double altFt, double elevationFt) =>
        Math.Clamp((altFt - elevationFt) / 318 + 1, VectorBaseNm, 18);

    /// <summary>
    /// ¿Hay que empezar a darle vectores? Llegada sin transición que empalme (ni frustrada), con la aproximación esperada y
    /// todavía sin autorizar, que no viene ya alineado hacia la final, y: del lado equivocado (abeam o pasado el punto donde
    /// se intercepta) a menos de 25 NM, o sin más ruta por delante (a cualquier distancia), o ya cerca del umbral.
    /// </summary>
    private bool NeedsVectors(ArrivalInfo arrival, FinalPosition fp)
    {
        if (_vector != VectorStage.None || _approachCleared || !_approachExpected || _missedApproach || !Unconnected(arrival))
            return false;
        var dist = ThresholdNm(arrival);
        if (fp.Established || dist < 3 || InboundToFinal(arrival, fp))
            return false;
        return fp.AlongNm < 5 && dist <= VectorJoinNm || RouteFinished() || dist <= ApproachClearanceNm + 2;
    }

    /// <summary>
    /// El siguiente paso de los vectores (o el primero), o null si no hay nada que decir ahora. Con el rumbo de
    /// interceptación va la autorización de aproximación.
    /// </summary>
    private AtcTransmission? VectorStep(AtcStation station, ArrivalInfo arrival, double altFt, double nowS, bool start = false)
    {
        if (LandingEnd(arrival) is not { } end || Final(arrival) is not { } fp)
            return null;
        if (_vector == VectorStage.None && !start && !NeedsVectors(arrival, fp))
            return null;
        // Establecido: ya no hacen falta (lo autoriza ReadyForApproach).
        if (fp.Established)
        {
            ResetVectors();
            return null;
        }
        var right = fp.CrossNm >= 0; // a la derecha del eje, mirando en el sentido del aterrizaje
        var cross = Math.Abs(fp.CrossNm);
        var baseNm = BaseTurnNm(altFt, end.ElevationFt);
        var dist = ThresholdNm(arrival);
        // Ya alineado y convergiendo por su cuenta (o por los vectores): se lo autoriza cerca, sin más rumbos.
        if (_vector is VectorStage.Join or VectorStage.Downwind && InboundToFinal(arrival, fp) && fp.AlongNm <= ApproachClearanceNm)
        {
            ResetVectors();
            return null;
        }
        // Interceptación: en la base (o ya por delante y cerca del eje), a 30° de la final, con la autorización.
        if (fp.AlongNm >= baseNm - 2 && cross <= VectorInterceptNm || _vector == VectorStage.Base && cross <= VectorInterceptNm && fp.AlongNm > 4)
        {
            var hdg = GeoMath.NormalizeDeg(end.CourseDeg + (right ? -VectorInterceptDeg : VectorInterceptDeg));
            _vector = VectorStage.None;
            _vectorTarget = null;
            _approachCleared = true;
            if (_clearedFt is { } was && was > arrival.ApproachAltitudeFt)
                _clearedFt = arrival.ApproachAltitudeFt;
            ResetFinalWatch();
            return Vector(station, arrival, hdg, "atc.vector_intercept_cleared", "pilot.readback_vector_intercept_cleared", nowS, runway: true);
        }
        // Lejos y por delante: directo a un punto de entrada en la prolongación del eje (una vez; otra si se desvía).
        if (fp.AlongNm >= baseNm - 2 && dist > VectorJoinNm)
        {
            var gate = GeoMath.Destination(end.Threshold, GeoMath.NormalizeDeg(end.CourseDeg + 180), Math.Clamp(baseNm + 4, 12, 20) * Nm);
            if (ctx.PilotPosition() is not { } at)
                return null;
            var toGate = GeoMath.InitialBearingDeg(at, gate);
            if (_vector != VectorStage.Join || Math.Abs(GeoMath.AngleDiffDeg(_vectorTrueDeg, toGate)) > 25 && nowS - _vectorAtS > 90)
            {
                _vector = VectorStage.Join;
                _approachExpected = true;
                _vectorTarget = gate;
                log?.Invoke(Tr.F("{0}: vectores a la final de la {1} (a {2:0.0} NM sobre el eje y {3:0.0} NM al costado).", Callsign, arrival.Runway, fp.AlongNm, cross));
                return Vector(station, arrival, toGate, "atc.vector_for_approach", "pilot.readback_vector_for_approach", nowS);
            }
            return VectorDescend(station, arrival, altFt, nowS);
        }
        if (_vector != VectorStage.Base && fp.AlongNm >= baseNm)
        {
            _vector = VectorStage.Base;
            _vectorTarget = GeoMath.Destination(end.Threshold, GeoMath.NormalizeDeg(end.CourseDeg + 180), fp.AlongNm * Nm);
            return Vector(station, arrival, GeoMath.NormalizeDeg(end.CourseDeg + (right ? -90 : 90)), "atc.vector_heading", "pilot.readback_vector_heading", nowS);
        }
        if (_vector == VectorStage.None)
        {
            // Alejamiento: rumbo opuesto a la pista; pegado al eje, 30° abierto para ganar separación lateral.
            _vector = VectorStage.Downwind;
            _approachExpected = true;
            var open = cross < VectorInterceptNm + 1 ? 30 : 0;
            var hdg = GeoMath.NormalizeDeg(end.CourseDeg + 180 + (right ? -open : open));
            // Hasta dónde va el alejamiento: el punto de la base, en su lado del eje.
            _vectorTarget = GeoMath.Destination(GeoMath.Destination(end.Threshold, GeoMath.NormalizeDeg(end.CourseDeg + 180), baseNm * Nm),
                GeoMath.NormalizeDeg(end.CourseDeg + (right ? 90 : -90)), Math.Max(cross, VectorInterceptNm + 2) * Nm);
            log?.Invoke(Tr.F("{0}: vectores a la final de la {1} (a {2:0.0} NM sobre el eje y {3:0.0} NM al costado).", Callsign, arrival.Runway, fp.AlongNm, cross));
            return Vector(station, arrival, hdg, "atc.vector_for_approach", "pilot.readback_vector_for_approach", nowS);
        }
        return VectorDescend(station, arrival, altFt, nowS);
    }

    /// <summary>
    /// Bajando durante los vectores (una instrucción por vez): en el alejamiento y la entrada, hasta la altitud de los tramos
    /// iniciales de la carta; en la base, hasta la de la final.
    /// </summary>
    private AtcTransmission? VectorDescend(AtcStation station, ArrivalInfo arrival, double altFt, double nowS)
    {
        var target = _vector == VectorStage.Base ? arrival.ApproachAltitudeFt : VectorFloorFt(arrival);
        if (_vector != VectorStage.None && _clearedFt is { } c && c > target + 200 && altFt > target + 300)
            return Descend(station, target, arrival.TransitionAltitudeFt, nowS);
        return null;
    }

    private AtcTransmission Vector(AtcStation station, ArrivalInfo arrival, double trueDeg, string id, string readbackId, double nowS,
        bool runway = false)
    {
        var mag = (int)(Math.Round(GeoMath.NormalizeDeg(trueDeg - (ctx.MagVarDeg?.Invoke() ?? 0)) / 5) * 5) % 360;
        if (mag == 0)
            mag = 360;
        _vectorHeadingMag = mag;
        _vectorTrueDeg = trueDeg;
        _vectorAtS = nowS;
        var current = ctx.HeadingDeg?.Invoke() ?? trueDeg;
        var toRight = GeoMath.NormalizeDeg(trueDeg - current) < 180;
        var dir = ctx.Language == AtcLanguage.Spanish ? toRight ? "derecha" : "izquierda" : toRight ? "right" : "left";
        var f = new Dictionary<string, string>
        {
            ["direction"] = dir, ["heading"] = mag.ToString("000", CultureInfo.InvariantCulture),
            ["approach"] = ApproachSpoken(arrival), ["runway"] = arrival.Runway,
            // Para la forma FAA (JO 7110.65 5-9-4): posición, altitud hasta establecerse.
            ["distance"] = Math.Max(1, Math.Round(ThresholdNm(arrival))).ToString(CultureInfo.InvariantCulture),
            ["level"] = LevelText(arrival.ApproachAltitudeFt, arrival.TransitionAltitudeFt),
        };
        return FlightInstruction(station, id, f, new ExpectedReadback(Callsign) { HeadingDeg = mag, Runway = runway ? arrival.Runway : null },
            readbackId, new Dictionary<string, string>(f), nowS);
    }
}
