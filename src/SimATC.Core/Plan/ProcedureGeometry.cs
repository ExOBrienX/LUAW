using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>Un tramo dibujable de un procedimiento. <c>Approximate</c>: calculado (rumbo hasta altitud, giro), no publicado.</summary>
public sealed record ShapeSegment(IReadOnlyList<GeoPoint> Points, bool Approximate);

/// <summary>Un punto con nombre del procedimiento, con sus restricciones ya escritas ("≥4000", "≤250 kt").</summary>
/// <remarks><see cref="Leg"/>: el tramo de la carta (restricciones numéricas), si el punto es un fijo.</remarks>
public sealed record ShapeWaypoint(string Ident, GeoPoint Position, string Constraints, bool Approximate, ProcedureLeg? Leg = null);

/// <summary>Una fila de la tabla de tramos: qué hace el tramo, en español, y sus restricciones.</summary>
public sealed record LegRow(string Fix, string Description, string Altitude, string Speed);

public sealed record ProcedureShape(IReadOnlyList<ShapeSegment> Segments, IReadOnlyList<ShapeWaypoint> Waypoints, IReadOnlyList<LegRow> Legs);

/// <summary>
/// Dibuja un procedimiento del simulador como lo hace la pantalla de un FMS:
/// <list type="bullet">
/// <item>tramos a un punto (TF, CF, DF, IF…): rectos y exactos;</item>
/// <item>arcos RF: con su centro y radio publicados (exactos);</item>
/// <item>tramos sin punto (CA, VA, FA… "rumbo hasta altitud"): aproximados, con un ascenso supuesto de
///   <see cref="ClimbFtPerNm"/>; un giro antes de un "directo a" (DF): aproximado con un radio fijo.</item>
/// </list>
/// Los cursos se usan tal cual: en los datos de SCEL coinciden con el rumbo verdadero a menos de 1° (ADR-025); el
/// MAGVAR del aeropuerto no se usa mientras su signo esté sin verificar.
/// </summary>
public static class ProcedureGeometry
{
    /// <summary>Ascenso supuesto para ubicar el fin de un "rumbo hasta altitud" (ft por NM). Aproximado (aprobado).</summary>
    public const double ClimbFtPerNm = 500;

    /// <summary>Radio de giro supuesto (m): ~210 kt con 25° de inclinación. Aproximado.</summary>
    public const double TurnRadiusM = 2500;

    /// <summary>Largo de un tramo que termina en vectores o en una interceptación (m): solo indica el rumbo.</summary>
    public const double StubM = 2 * UnitConversions.MetersPerNauticalMile;

    /// <summary>
    /// Arma la forma de los tramos dados. <paramref name="start"/>/<paramref name="startHeadingDeg"/>: de dónde parte
    /// (el extremo de despegue de la pista para una SID); null si empieza en su primer punto (STAR, aproximación).
    /// </summary>
    /// <param name="runwayEnd">Ubica un punto de pista ("RW02") en su cabecera; null deja la posición del simulador.</param>
    public static ProcedureShape Build(IReadOnlyList<ProcedureLeg> legs, GeoPoint? start, double startHeadingDeg, double startAltFt,
        double transitionAltitudeFt, Func<string, GeoPoint?>? runwayEnd = null)
    {
        var segments = new List<ShapeSegment>();
        var waypoints = new List<ShapeWaypoint>();
        var rows = new List<LegRow>();
        var cur = start;
        var heading = startHeadingDeg;
        var alt = startAltFt;
        var exact = new List<GeoPoint>();
        // Después de un tramo "hasta vectores" no se sabe por dónde llega al punto siguiente: va punteado (aproximado).
        var afterVectors = false;
        if (cur is { } s0)
            exact.Add(s0);

        void Flush()
        {
            if (exact.Count > 1)
                segments.Add(new ShapeSegment([.. exact], false));
            exact.Clear();
            if (cur is { } c)
                exact.Add(c);
        }

        void AddApprox(List<GeoPoint> pts)
        {
            Flush();
            if (pts.Count > 1)
                segments.Add(new ShapeSegment(pts, true));
            exact.Clear();
            exact.Add(pts[^1]);
        }

        foreach (var leg in legs)
        {
            var hasFix = leg.FixIcao.Length > 0 && !(leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0);
            var altText = AltitudeText(leg, transitionAltitudeFt);
            var speedText = SpeedText(leg);
            rows.Add(new LegRow(hasFix ? leg.FixIcao : "—", Describe(leg, transitionAltitudeFt), altText, speedText));

            if (hasFix)
            {
                // Un punto de pista (tipo "R", p. ej. RW02) llega del simulador en el centro de la pista; en las cartas es
                // la cabecera. Se dibuja en el extremo de esa cabecera.
                var fix = leg.FixType == "R" && runwayEnd?.Invoke(leg.FixIcao) is { } threshold ? threshold : leg.FixPosition;
                if (cur is { } c && leg.Type == LegType.RF && leg.ArcRadiusM > 0)
                {
                    exact.AddRange(Arc(leg.ArcCenter, c, fix, leg.Turn == TurnDirection.Left).Skip(1));
                    heading = GeoMath.NormalizeDeg(GeoMath.InitialBearingDeg(leg.ArcCenter, fix) + (leg.Turn == TurnDirection.Left ? -90 : 90));
                }
                else if (cur is { } c2 && leg.Type == LegType.DF && GeoMath.DistanceM(c2, fix) > TurnRadiusM * 2)
                {
                    // "Directo a": el avión gira desde su rumbo hasta apuntar al punto (giro aproximado) y sigue recto.
                    var turn = TurnToward(c2, heading, fix, leg.Turn);
                    if (turn.Count > 1)
                        AddApprox(turn);
                    exact.Add(fix);
                    heading = GeoMath.InitialBearingDeg(turn[^1], fix);
                }
                else if (afterVectors && cur is { } cv)
                {
                    heading = GeoMath.InitialBearingDeg(cv, fix);
                    AddApprox([cv, fix]);
                }
                else
                {
                    if (cur is { } c3 && GeoMath.DistanceM(c3, fix) > 1)
                        heading = GeoMath.InitialBearingDeg(c3, fix);
                    exact.Add(fix);
                }
                afterVectors = false;
                cur = fix;
                waypoints.Add(new ShapeWaypoint(leg.FixIcao, fix, Join(altText, speedText), false, leg));
                if (leg.AltitudeConstraint != AltitudeConstraint.None)
                    alt = Math.Max(alt, leg.AltitudeConstraint == AltitudeConstraint.Between ? Math.Min(leg.Altitude1Ft, leg.Altitude2Ft) : leg.Altitude1Ft);
                // FM ("desde el fijo, curso hasta vectores", ADR-102): el tramo manual sale del fijo, con su viraje estimado.
                if (leg.Type == LegType.FM)
                {
                    var manual = TurnThenStraight(fix, heading, leg.CourseDeg, leg.Turn, StubM);
                    AddApprox(manual);
                    cur = manual[^1];
                    heading = leg.CourseDeg;
                    waypoints.Add(new ShapeWaypoint(Tr.T("vectores"), manual[^1], Tr.T("aprox."), true));
                    afterVectors = true;
                }
                continue;
            }

            // Sin punto: rumbo o curso hasta una altitud, una interceptación o vectores. Aproximado.
            if (cur is not { } from)
                continue;
            var course = leg.CourseDeg;
            double distance;
            string label;
            if (leg.Type is LegType.CA or LegType.VA or LegType.FA && leg.Altitude1Ft > alt)
            {
                distance = Math.Max(0.5, (leg.Altitude1Ft - alt) / ClimbFtPerNm) * UnitConversions.MetersPerNauticalMile;
                label = FormatAltitude(leg.Altitude1Ft, transitionAltitudeFt);
                alt = leg.Altitude1Ft;
            }
            else
            {
                distance = StubM;
                label = leg.Type is LegType.VM or LegType.FM ? Tr.T("vectores") : "";
            }
            // Con su viraje estimado desde el rumbo que traía (ADR-102: antes partía en el rumbo nuevo, con un quiebre).
            var pts = TurnThenStraight(from, heading, course, leg.Turn, distance);
            var end = pts[^1];
            AddApprox(pts);
            cur = end;
            heading = course;
            afterVectors = leg.Type is LegType.VM or LegType.FM;
            if (label.Length > 0)
                waypoints.Add(new ShapeWaypoint(label, end, Tr.T("aprox."), true));
        }
        Flush();
        return new ProcedureShape(segments, waypoints, rows);
    }

    /// <summary>Más que esto (°) entre el primer rumbo de la SID y el de la pista: está hecha para la otra cabecera.</summary>
    public const double SidOppositeDeg = 110;

    /// <summary>A menos de esto (m) del despegue, un primer fijo "detrás" de la pista delata una SID de la otra cabecera.</summary>
    public const double SidFirstFixBehindM = 8 * UnitConversions.MetersPerNauticalMile;

    /// <summary>
    /// ¿La SID sirve para despegar por <paramref name="runway"/>? Con transiciones de pista, si tiene la de esa pista. Sin ellas
    /// (SID "común"), si sale en el sentido de la pista: su primer rumbo publicado no va al revés y su primer fijo cercano no
    /// queda detrás (ADR-117, RKPK: ENGO1S y KALO1S salen al sur y los IA las volaban desde la 36L, con media vuelta de 180°).
    /// </summary>
    public static bool SidFitsRunway(Procedure sid, AirportData airport, string runway)
    {
        if (sid.RunwayTransitions.Count > 0)
            return ProcedureMatch.ServesRunway(sid, runway);
        if (FindRunwayEnd(airport, runway) is not { } r)
            return true;
        foreach (var leg in sid.Legs)
        {
            switch (leg.Type)
            {
                case LegType.CA or LegType.CD or LegType.CF or LegType.CI or LegType.CR or LegType.VA or LegType.VD or LegType.VI
                    or LegType.VM or LegType.VR or LegType.FA:
                    // Rumbo magnético o verdadero: el margen cubre la declinación.
                    return Math.Abs(GeoMath.AngleDiffDeg(r.HeadingDeg, leg.CourseDeg)) <= SidOppositeDeg;
                case LegType.IF or LegType.DF or LegType.TF when leg.FixPosition is { } fix && (fix.LatDeg != 0 || fix.LonDeg != 0):
                    var d = GeoMath.DistanceM(r.LiftoffEnd, fix);
                    return d > SidFirstFixBehindM
                           || Math.Abs(GeoMath.AngleDiffDeg(r.HeadingDeg, GeoMath.InitialBearingDeg(r.LiftoffEnd, fix))) <= SidOppositeDeg;
            }
        }
        return true;
    }

    /// <summary>Forma de una SID desde el extremo de despegue de la pista planificada.</summary>
    public static ProcedureShape Sid(Procedure sid, AirportData airport, string? runway, string? transition)
    {
        var legs = new List<ProcedureLeg>();
        GeoPoint? start = null;
        var heading = 0.0;
        if (runway is not null)
        {
            legs.AddRange(RunwayLegs(sid, runway));
            if (FindRunwayEnd(airport, runway) is { } r)
            {
                start = r.LiftoffEnd;
                heading = r.HeadingDeg;
            }
        }
        legs.AddRange(sid.Legs);
        legs.AddRange(EnrouteLegs(sid, transition));
        // Se parte del extremo de despegue: un primer tramo al punto de la pista ("IF RW36L", el umbral) volvía 3 km hacia
        // atrás (ADR-117, RKPK: "media vuelta de 180° en RW36L" en todas las salidas por la 36L).
        if (start is not null)
        {
            while (legs.Count > 0 && legs[0].FixIcao.StartsWith("RW", StringComparison.Ordinal)
                   && legs[0].Type is LegType.IF or LegType.TF or LegType.CF or LegType.DF)
                legs.RemoveAt(0);
        }
        return Build(legs, start, heading, airport.ElevationFt, TransitionAltitude(airport), id => RunwayFix(airport, id));
    }

    public static ProcedureShape Star(Procedure star, AirportData airport, string? runway, string? transition)
    {
        var legs = new List<ProcedureLeg>();
        legs.AddRange(EnrouteLegs(star, transition));
        legs.AddRange(star.Legs);
        if (runway is not null)
            legs.AddRange(RunwayLegs(star, runway));
        return Build(legs, null, 0, 0, TransitionAltitude(airport), id => RunwayFix(airport, id));
    }

    /// <summary>Tramos de la STAR en orden: transición de ruta, tramo común y transición a la pista (ADR-098).</summary>
    public static IReadOnlyList<ProcedureLeg> StarLegs(Procedure star, string? runway, string? transition)
    {
        var legs = new List<ProcedureLeg>();
        legs.AddRange(EnrouteLegs(star, transition));
        legs.AddRange(star.Legs);
        if (runway is not null)
            legs.AddRange(RunwayLegs(star, runway));
        return legs;
    }

    /// <summary>La aproximación con su transición; con <paramref name="withMissed"/>, también la frustrada (ADR-105).</summary>
    public static ProcedureShape Approach(Approach approach, AirportData airport, string? transition, bool withMissed = false)
    {
        var legs = new List<ProcedureLeg>();
        foreach (var t in approach.Transitions)
        {
            if (t.Name == transition)
                legs.AddRange(t.Legs);
        }
        legs.AddRange(approach.FinalLegs);
        if (withMissed)
            legs.AddRange(approach.MissedLegs);
        return Build(legs, null, 0, 0, TransitionAltitude(airport), id => RunwayFix(airport, id));
    }

    private static double TransitionAltitude(AirportData a) => a.TransitionAltitudeFt > 0 ? a.TransitionAltitudeFt : 18000;

    /// <summary>Extremo de una pista por su nombre ("17L"): dónde termina el despegue y su rumbo.</summary>
    /// <summary>Extremo de la cabecera de un punto de pista ("RW02" → extremo 02), o null si no está.</summary>
    public static GeoPoint? RunwayFix(AirportData airport, string fixIdent)
    {
        if (!fixIdent.StartsWith("RW", StringComparison.Ordinal))
            return null;
        var ident = fixIdent[2..];
        foreach (var r in airport.Runways)
        {
            if (r.Primary.Ident == ident)
                return r.Primary.Position;
            if (r.Secondary.Ident == ident)
                return r.Secondary.Position;
        }
        return null;
    }

    public static (GeoPoint LiftoffEnd, double HeadingDeg)? FindRunwayEnd(AirportData airport, string runway)
    {
        foreach (var r in airport.Runways)
        {
            if (r.Primary.Ident == runway)
                return (r.Secondary.Position, r.Primary.HeadingTrueDeg);
            if (r.Secondary.Ident == runway)
                return (r.Primary.Position, r.Secondary.HeadingTrueDeg);
        }
        return null;
    }

    internal static IReadOnlyList<ProcedureLeg> RunwayLegs(Procedure p, string runway)
    {
        foreach (var t in p.RunwayTransitions)
        {
            if (RunwayEnd.FormatIdent(t.RunwayNumber, t.RunwayDesignator) == runway)
                return t.Legs;
        }
        return [];
    }

    internal static IReadOnlyList<ProcedureLeg> EnrouteLegs(Procedure p, string? transition)
    {
        if (transition is null)
            return [];
        foreach (var t in p.EnrouteTransitions)
        {
            if (string.Equals(t.Name, transition, StringComparison.OrdinalIgnoreCase))
                return t.Legs;
        }
        return [];
    }

    /// <summary>Arco alrededor de <paramref name="center"/> de <paramref name="from"/> a <paramref name="to"/>, cada 5°.</summary>
    internal static List<GeoPoint> Arc(GeoPoint center, GeoPoint from, GeoPoint to, bool left)
    {
        var radius = GeoMath.DistanceM(center, to);
        var a0 = GeoMath.InitialBearingDeg(center, from);
        var a1 = GeoMath.InitialBearingDeg(center, to);
        // Derecha = sentido horario (el rumbo desde el centro crece); izquierda = antihorario.
        var sweep = left ? -GeoMath.NormalizeDeg(a0 - a1) : GeoMath.NormalizeDeg(a1 - a0);
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 5));
        var points = new List<GeoPoint>(steps + 1) { from };
        for (var i = 1; i < steps; i++)
            points.Add(GeoMath.Destination(center, a0 + sweep * i / steps, radius));
        points.Add(to);
        return points;
    }

    /// <summary>
    /// Viraje estimado (radio <see cref="TurnRadiusM"/>, pasos de 5°) desde el rumbo <paramref name="heading"/> hasta el curso
    /// <paramref name="course"/> (por el lado que indica el tramo, o el más corto), y después recto hasta completar
    /// <paramref name="distanceM"/> (al menos un tercio en recto). Para los tramos sin punto final: rumbo o curso hasta una
    /// altitud o hasta vectores (MANUAL). APROXIMADO.
    /// </summary>
    internal static List<GeoPoint> TurnThenStraight(GeoPoint from, double heading, double course, TurnDirection turn, double distanceM)
    {
        var points = new List<GeoPoint> { from };
        var diff = GeoMath.AngleDiffDeg(heading, course);
        var pos = from;
        var travelled = 0.0;
        if (Math.Abs(diff) >= 3)
        {
            var right = turn switch
            {
                TurnDirection.Right => true,
                TurnDirection.Left => false,
                _ => diff > 0,
            };
            var sweep = right ? (diff + 360) % 360 : (360 - diff) % 360; // grados a girar por ese lado
            const double step = 5.0;
            var arcStep = 2 * Math.PI * TurnRadiusM * step / 360;
            var h = heading;
            for (var turned = 0.0; turned + step <= sweep; turned += step)
            {
                h = GeoMath.NormalizeDeg(h + (right ? step : -step));
                pos = GeoMath.Destination(pos, h, arcStep);
                travelled += arcStep;
                points.Add(pos);
            }
        }
        points.Add(GeoMath.Destination(pos, course, Math.Max(distanceM - travelled, distanceM / 3)));
        return points;
    }

    /// <summary>Giro aproximado desde <paramref name="from"/> con rumbo <paramref name="heading"/> hasta apuntar al punto.</summary>
    private static List<GeoPoint> TurnToward(GeoPoint from, double heading, GeoPoint fix, TurnDirection turn)
    {
        var points = new List<GeoPoint> { from };
        var target = GeoMath.InitialBearingDeg(from, fix);
        var diff = GeoMath.AngleDiffDeg(heading, target);
        if (Math.Abs(diff) < 3)
            return points;
        var right = turn switch
        {
            TurnDirection.Right => true,
            TurnDirection.Left => false,
            _ => diff > 0,
        };
        var pos = from;
        var h = heading;
        var step = 5.0;
        var arcStep = 2 * Math.PI * TurnRadiusM * step / 360;
        for (var i = 0; i < 72; i++)
        {
            var remaining = GeoMath.AngleDiffDeg(h, GeoMath.InitialBearingDeg(pos, fix));
            if (right ? remaining <= step && remaining > -step : remaining >= -step && remaining < step)
                break;
            h = GeoMath.NormalizeDeg(h + (right ? step : -step));
            pos = GeoMath.Destination(pos, h, arcStep);
            points.Add(pos);
        }
        return points;
    }

    // ---------- Textos ----------

    public static string FormatAltitude(double ft, double transitionAltitudeFt) =>
        ft > transitionAltitudeFt && transitionAltitudeFt > 0
            ? $"FL{Math.Round(ft / 100):000}"
            : Math.Round(ft).ToString("0", CultureInfo.InvariantCulture);

    public static string AltitudeText(ProcedureLeg leg, double ta) => leg.AltitudeConstraint switch
    {
        AltitudeConstraint.At => FormatAltitude(leg.Altitude1Ft, ta),
        AltitudeConstraint.AtOrAbove => $"≥{FormatAltitude(leg.Altitude1Ft, ta)}",
        AltitudeConstraint.AtOrBelow => $"≤{FormatAltitude(leg.Altitude1Ft, ta)}",
        AltitudeConstraint.Between =>
            $"{FormatAltitude(Math.Min(leg.Altitude1Ft, leg.Altitude2Ft), ta)}–{FormatAltitude(Math.Max(leg.Altitude1Ft, leg.Altitude2Ft), ta)}",
        _ => "",
    };

    public static string SpeedText(ProcedureLeg leg) => leg.SpeedLimitKt <= 0 ? "" : leg.SpeedConstraint switch
    {
        SpeedConstraint.At => $"{leg.SpeedLimitKt:0} kt",
        SpeedConstraint.AtOrAbove => Tr.F("≥{0:0} kt", leg.SpeedLimitKt),
        _ => Tr.F("≤{0:0} kt", leg.SpeedLimitKt),
    };

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a} {b}";

    /// <summary>Qué hace el tramo, en español (tipos ARINC 424).</summary>
    public static string Describe(ProcedureLeg leg, double ta)
    {
        var c = $"{leg.CourseDeg:000}°";
        var fix = leg.FixIcao;
        var alt = leg.Altitude1Ft > 0 ? FormatAltitude(leg.Altitude1Ft, ta) : "";
        var side = leg.Turn switch { TurnDirection.Left => Tr.T(" (por izquierda)"), TurnDirection.Right => Tr.T(" (por derecha)"), _ => "" };
        return leg.Type switch
        {
            LegType.IF => Tr.F("Inicio en {0}", fix),
            LegType.TF => $"A {fix}",
            LegType.CF => Tr.F("Curso {0} a {1}", c, fix),
            LegType.DF => Tr.F("Directo a {0}{1}", fix, side),
            LegType.RF => Tr.F("Arco{0} a {1}", side, fix),
            LegType.AF => Tr.F("Arco DME a {0}", fix),
            LegType.CA => Tr.F("Curso {0} hasta {1}", c, alt),
            LegType.VA => Tr.F("Rumbo {0} hasta {1}", c, alt),
            LegType.FA => Tr.F("Desde {0} curso {1} hasta {2}", fix, c, alt),
            LegType.CI or LegType.VI => Tr.F("Rumbo {0} hasta interceptar", c),
            LegType.VM => Tr.F("Rumbo {0} hasta vectores", c),
            LegType.FM => Tr.F("Desde {0} curso {1} hasta vectores", fix, c),
            LegType.CD or LegType.VD or LegType.FD => Tr.F("Curso {0} hasta distancia DME", c),
            LegType.CR or LegType.VR => Tr.F("Curso {0} hasta radial", c),
            LegType.HA or LegType.HF or LegType.HM => Tr.F("Espera en {0}", fix),
            LegType.PI => Tr.F("Viraje de procedimiento en {0}", fix),
            LegType.FC => Tr.F("Desde {0} curso {1}", fix, c),
            _ => leg.Type.ToString(),
        };
    }
}
