using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Units;

namespace SimATC.Core.Tests.Plan;

/// <summary>Geometría de procedimientos y recomendación de SID/STAR/aproximación (datos reales de SCEL y SCIE).</summary>
public class ProcedureAdviceTests
{
    private static readonly Lazy<AirportData> Scel = new(() => Load("SCEL"));
    private static readonly Lazy<AirportData> Scie = new(() => Load("SCIE"));

    private static AirportData Load(string icao) =>
        SimAirports.Load($"{icao}.json");

    // TEST DATA: plan con la forma del de SimBrief SCEL 17L → SCIE 02 por GELU5B / UQ809 / ARUN1A.
    private static FlightPlan Plan() => new(PlanSource.SimBrief, "TST205", "A20N",
        new PlanAirport("SCEL", PlannedRunway: "17L"), new PlanAirport("SCIE", PlannedRunway: "02"), 24000, "GELU5B GELUS UQ809 ARUNI ARUN1A",
        [
            new PlanFix("LOLAT", "wpt", "GELU5B", true, new GeoPoint(-33.54, -70.975), 14400),
            new PlanFix("GELUS", "wpt", "GELU5B", false, new GeoPoint(-33.915, -71.203), 23700),
            new PlanFix("TOC", "ltlg", "UQ809", false, new GeoPoint(-33.93, -71.21), 24000),
            new PlanFix("ARUNI", "wpt", "UQ809", false, new GeoPoint(-35.886, -72.44), 24000),
            new PlanFix("IE593", "wpt", "ARUN1A", true, new GeoPoint(-36.315, -73.003), 12200),
            new PlanFix("DAKTU", "wpt", "ARUN1A", true, new GeoPoint(-36.706, -73.184), 2700),
            new PlanFix("SCIE", "apt", "ARUN1A", true, new GeoPoint(-36.771, -73.062), 2500),
        ])
    {
        SidIdent = "GELU5B",
        StarIdent = "ARUN1A",
    };

    /// <summary>
    /// La salida que recomienda LUAW por otra pista (la línea blanca punteada del radar, ADR-113) sale del extremo de
    /// despegue de esa pista y termina en el primer punto de la ruta del plan: empalma (pedido de Marco 2026-11-11).
    /// </summary>
    [Fact]
    public void The_departure_recommended_for_another_runway_starts_at_it_and_joins_the_route()
    {
        var sid = SimATC.Core.Comms.DepartureChoice.Sid(Scel.Value, Plan(), "17R");
        Assert.NotNull(sid);
        var shape = ProcedureGeometry.Sid(sid, Scel.Value, "17R", null);
        var (liftoff, _) = ProcedureGeometry.FindRunwayEnd(Scel.Value, "17R")!.Value;
        Assert.Equal(liftoff, shape.Segments[0].Points[0]);
        Assert.Equal("GELUS", shape.Waypoints.Last(w => !w.Approximate).Ident);
    }

    [Fact]
    public void Gelu5b_from_17L_climbs_straight_to_4000_then_turns_direct_to_LOLAT()
    {
        var sid = Scel.Value.Departures.First(p => p.Name == "GELU5B");
        var shape = ProcedureGeometry.Sid(sid, Scel.Value, "17L", null);
        var (liftoff, heading) = ProcedureGeometry.FindRunwayEnd(Scel.Value, "17L")!.Value;

        // Primero, aproximado: rumbo de pista hasta 4000 ft con 500 ft/NM (≈ 4,9 NM desde 1550 ft).
        var first = shape.Segments[0];
        Assert.True(first.Approximate);
        Assert.Equal(liftoff, first.Points[0]);
        Assert.Equal(177, GeoMath.InitialBearingDeg(first.Points[0], first.Points[^1]), 0.1); // el curso publicado
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(heading, 177)) < 1);
        var climbNm = GeoMath.DistanceM(first.Points[0], first.Points[^1]) / UnitConversions.MetersPerNauticalMile;
        Assert.InRange(climbNm, 4.5, 5.3);
        Assert.Contains(shape.Waypoints, w => w.Ident == "4000" && w.Approximate);

        // Después el giro (aproximado) y, exactos, LOLAT y GELUS con sus restricciones.
        Assert.Contains(shape.Segments.Skip(1), s => s.Approximate);
        Assert.False(shape.Segments[^1].Approximate);
        Assert.Equal("6500–9000", shape.Waypoints.First(w => w.Ident == "LOLAT").Constraints);
        Assert.Equal("≥10000", shape.Waypoints.First(w => w.Ident == "GELUS").Constraints);
        Assert.Equal("Curso 177° hasta 4000", shape.Legs[0].Description);
    }

    [Fact]
    public void Runway_fix_is_drawn_at_the_runway_end_and_the_final_is_aligned()
    {
        var ils = Scie.Value.Approaches.First(a => a.Type == ApproachType.Ils && a.Suffix == "T");
        var shape = ProcedureGeometry.Approach(ils, Scie.Value, "DAKTU");
        var runway = Scie.Value.Runways[0];
        var end02 = runway.Primary.Ident == "02" ? runway.Primary : runway.Secondary;

        // El simulador lo da en el centro de la pista; se dibuja en la cabecera 02.
        var rw = shape.Waypoints.First(w => w.Ident == "RW02");
        Assert.True(GeoMath.DistanceM(rw.Position, end02.Position) < 1);
        var kalis = shape.Waypoints.First(w => w.Ident == "KALIS");
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(kalis.Position, rw.Position), end02.HeadingTrueDeg)) < 1);
    }

    [Fact]
    public void Rf_leg_is_drawn_as_the_published_arc()
    {
        var ils = Scie.Value.Approaches.First(a => a.Type == ApproachType.Ils && a.Suffix == "T");
        var rf = ils.Transitions[0].Legs.First(l => l.Type == LegType.RF);
        var shape = ProcedureGeometry.Approach(ils, Scie.Value, "DAKTU");

        var arcPoints = shape.Segments.SelectMany(s => s.Points)
            .Where(p => Math.Abs(GeoMath.DistanceM(p, rf.ArcCenter) - rf.ArcRadiusM) < rf.ArcRadiusM * 0.02)
            .ToList();
        Assert.True(arcPoints.Count >= 4, $"{arcPoints.Count} puntos sobre el arco");
        Assert.All(shape.Segments, s => Assert.False(s.Approximate)); // todo publicado: nada aproximado
    }

    [Fact]
    public void Recommends_the_plan_sid_and_explains_the_others()
    {
        var advice = ProcedureAdvisor.Sids(Scel.Value, Plan());

        Assert.Equal("GELU5B", advice[0].Name);
        Assert.True(advice[0].Recommended);
        Assert.Contains(advice[0].Pros, p => p.Contains("SimBrief"));
        Assert.Contains(advice[0].Pros, p => p.Contains("GELUS"));
        var gelu3c = advice.First(a => a.Name == "GELU3C");
        Assert.False(gelu3c.Valid);
        Assert.Contains(gelu3c.Cons, c => c.Contains("17L"));
        var gelu1l = advice.First(a => a.Name == "GELU1L");
        Assert.True(gelu1l.Valid);
        Assert.False(gelu1l.Recommended);
        Assert.Contains(gelu1l.Notes, n => n.Contains("RF"));
        Assert.False(advice.First(a => a.Name == "GELU1R").Valid); // conecta con GELUS, pero es solo para la 17R
    }

    [Fact]
    public void Recommends_the_star_that_starts_where_the_route_ends_and_the_approach_that_chains_with_it()
    {
        var plan = Plan();
        Assert.Equal("GELUS", ProcedureAdvisor.ExitFix(plan));
        Assert.Equal("ARUNI", ProcedureAdvisor.EntryFix(plan));

        var stars = ProcedureAdvisor.Stars(Scie.Value, plan);
        Assert.Equal("ARUN1A", stars[0].Name);
        Assert.True(stars[0].Recommended);

        var starEnd = ProcedureGeometry.Star(stars[0].Procedure!, Scie.Value, "02", null).Waypoints.Last(w => !w.Approximate).Ident;
        var apps = ProcedureAdvisor.Approaches(Scie.Value, plan, starEnd);
        Assert.True(apps[0].Recommended);
        Assert.StartsWith("ILS", apps[0].Name);
        Assert.EndsWith("02", apps[0].Name);
        Assert.Equal(starEnd, apps[0].Transition);
        Assert.Contains(apps, a => !a.Valid && a.Cons.Any(c => c.Contains("20")));
    }
}
