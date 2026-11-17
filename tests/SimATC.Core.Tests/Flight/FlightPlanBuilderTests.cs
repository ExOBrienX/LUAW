using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;
using SimATC.Core.Units;

namespace SimATC.Core.Tests.Flight;

/// <summary>Planes completos como un FMS (ADR-062) con los procedimientos reales de SCEL.</summary>
public class FlightPlanBuilderTests
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    // TEST DATA: posiciones aproximadas de SCIE (al sur) y SAME (al este).
    private static readonly GeoPoint Scie = new(-36.7727, -73.0631);
    private static readonly GeoPoint Same = new(-32.8317, -68.7929);

    private static FlightEnds Arrival(string runway, GeoPoint from, string fromIcao = "SCIE") =>
        new(fromIcao, from, 0, null, null, "SCEL", Scel.Value.Reference, Scel.Value.ElevationFt, Scel.Value,
            FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet));

    private static FlightEnds Departure(string runway, GeoPoint to, string toIcao) =>
        new("SCEL", Scel.Value.Reference, Scel.Value.ElevationFt, Scel.Value, runway, toIcao, to, 0, null, null);

    /// <summary>Un fijo de un procedimiento de SCEL como fijo de ruta (sus datos salen del simulador, no se inventan).</summary>
    private static RouteFix FixOf(ProcedureShape shape, bool first)
    {
        var w = first ? shape.Waypoints.First(x => !x.Approximate) : shape.Waypoints.Last(x => !x.Approximate && !x.Ident.StartsWith("RW"));
        return new RouteFix(w.Ident, w.Leg?.FixRegion ?? "SC", w.Position, "");
    }

    private static void AssertNoBacktracking(FlightPath path, GeoPoint end)
    {
        // Ningún tramo vuelve más de 40 NM hacia atrás respecto del final (sin vueltas completas).
        var best = double.MaxValue;
        for (var i = 0; i < path.Count; i++)
        {
            var d = GeoMath.DistanceM(path[i].Position, end);
            Assert.True(d < best + 40 * Nm, $"el punto {i} ({path[i].Name}) se aleja del final: {d / Nm:0} NM tras {best / Nm:0} NM");
            best = Math.Min(best, d);
        }
    }

    [Fact]
    public void An_arrival_without_route_flies_star_approach_and_final_to_the_threshold()
    {
        var plan = FlightPlanBuilder.Assemble(Arrival("17L", Scie), ResolvedRoute.Direct, PerformanceCategory.MediumJet);
        Assert.NotNull(plan.Star);
        Assert.NotNull(plan.Approach);
        Assert.Equal("RW17L", plan.Path.End.Name);
        Assert.Equal(PathSection.Final, plan.Path.End.Section);
        // La final queda alineada y nada pasa del umbral.
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        Assert.True(FlightPlanner.IsOnFinal(l, plan.Path[plan.Path.Count - 2].Position, 0.5 * Nm));
        var gc = GeoMath.DistanceM(Scie, l.Threshold) / Nm;
        Assert.True(plan.Path.LengthM / Nm < gc * 1.4 + 60, $"{plan.Path.LengthM / Nm:0} NM para {gc:0} NM");
        Assert.True(plan.Path.HasProfile);
    }

    [Fact]
    public void The_star_is_the_one_that_starts_at_the_route_fix_and_the_plan_names_win()
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var simo = Scel.Value.Arrivals.First(s => s.Name == "SIMO7B");
        var entry = FixOf(ProcedureGeometry.Star(simo, Scel.Value, "17L", null), first: true);
        // TEST DATA: un fijo de ruta 120 NM al norte de la entrada, y la entrada de la STAR.
        var before = new RouteFix("AAAAA", "SC", GeoMath.Destination(entry.Position, 0, 120 * Nm), "");
        var route = new ResolvedRoute([before, entry with { Airway = "UL302" }], "AAAAA UL302 SIMOK SIMO7B", "test", StarToken: "SIMO7B");
        var north = GeoMath.Destination(entry.Position, 0, 300 * Nm);
        var plan = FlightPlanBuilder.Assemble(Arrival("17L", north, "SAAR"), route, PerformanceCategory.MediumJet);
        Assert.Equal("SIMO7B", plan.Star);
        // El fijo de entrada aparece una sola vez (no se vuela dos veces) y en la STAR.
        var at = Enumerable.Range(0, plan.Path.Count).Where(i => plan.Path[i].Name == entry.Ident).ToList();
        Assert.Single(at);
        Assert.Equal(PathSection.Star, plan.Path[at[0]].Section);
        AssertNoBacktracking(plan.Path, l.Threshold);
    }

    [Fact]
    public void The_sid_is_the_one_that_ends_at_the_first_route_fix()
    {
        var dont = Scel.Value.Departures.First(s => s.Name == "DONT5B");
        var exit = FixOf(ProcedureGeometry.Sid(dont, Scel.Value, "17L", null), first: false);
        // TEST DATA: la ruta empieza en el final de la DONT5B y sigue 200 NM hacia el este.
        var after = new RouteFix("BBBBB", "SA", GeoMath.Destination(exit.Position, 90, 200 * Nm), "UM795");
        var route = new ResolvedRoute([exit, after], "DONTI5B DONTI UM795 BBBBB", "test", SidToken: "DONTI5B");
        var dest = GeoMath.Destination(after.Position, 90, 100 * Nm);
        var plan = FlightPlanBuilder.Assemble(Departure("17L", dest, "SABE"), route, PerformanceCategory.MediumJet);
        Assert.Equal("DONT5B", plan.Sid);
        Assert.Single(Enumerable.Range(0, plan.Path.Count), i => plan.Path[i].Name == exit.Ident);
        Assert.Equal(PathSection.Sid, plan.Path[0].Section);
        // Crucero semicircular hacia el este: nivel impar.
        Assert.Equal(1, (int)(plan.CruiseFt / 1000) % 2);
    }

    [Fact]
    public void A_departure_without_route_takes_a_sid_toward_the_destination()
    {
        var plan = FlightPlanBuilder.Assemble(Departure("17L", Same, "SAME"), ResolvedRoute.Direct, PerformanceCategory.MediumJet);
        Assert.NotNull(plan.Sid);
        var sidEnd = Enumerable.Range(0, plan.Path.Count).Last(i => plan.Path[i].Section == PathSection.Sid);
        var off = GeoMath.AngleDiffDeg(GeoMath.InitialBearingDeg(Scel.Value.Reference, Same),
            GeoMath.InitialBearingDeg(Scel.Value.Reference, plan.Path[sidEnd].Position));
        Assert.InRange(Math.Abs(off), 0, 100);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void A_flight_spawned_mid_route_is_on_its_path_heading_to_the_next_point(double fraction)
    {
        var plan = FlightPlanBuilder.Assemble(Arrival("17L", Scie), ResolvedRoute.Direct, PerformanceCategory.MediumJet);
        var (pos, heading, next, alt, _) = plan.At(plan.Path.LengthM * fraction, 0);
        var toNext = GeoMath.InitialBearingDeg(pos, plan.Path[next].Position);
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(heading, toNext)) < 5 || GeoMath.DistanceM(pos, plan.Path[next].Position) < 50);
        Assert.InRange(alt, 1000, plan.CruiseFt);
        // El avión creado ahí vuela a ese punto, no al más cercano de otra parte de la ruta.
        var f = new AirborneAgent("TST1", "TST", "A320", "SCIE", "SCEL", null, PerformanceCategory.MediumJet, plan.Path, pos, alt, heading, 280,
            0, plan.CruiseFt, null) { StartTarget = next };
        Assert.Equal(next, f.TargetIndex);
    }

    [Fact]
    public void An_arrival_from_the_south_by_er6epa_does_not_overfly_scel_and_turn_back()
    {
        // Prueba de Marco (JAT630, SCQP→SCEL): la ruta de VATSIM nombra ER6EPA, que termina en PADOP al norte volando al norte,
        // y la ILS 17L va al sur: el plan cruzaba el aeropuerto y daba media vuelta. Debe elegir una llegada limpia.
        var er6 = Scel.Value.Arrivals.First(s => s.Name == "ER6EPA");
        var entry = FixOf(ProcedureGeometry.Star(er6, Scel.Value, "17L", null), first: true);
        // TEST DATA: un fijo de ruta 100 NM al sur de EROLO.
        var before = new RouteFix("CCCCC", "SC", GeoMath.Destination(entry.Position, 180, 100 * Nm), "");
        var route = new ResolvedRoute([before, entry with { Airway = "UQ802" }], "CCCCC UQ802 EROLO ER6EPA", "test", StarToken: "ER6EPA");
        var plan = FlightPlanBuilder.Assemble(Arrival("17L", GeoMath.Destination(entry.Position, 180, 300 * Nm), "SCQP"), route,
            PerformanceCategory.MediumJet);
        Assert.DoesNotContain(plan.Issues, i => i.Contains("media vuelta"));
        Assert.DoesNotContain(plan.Issues, i => i.Contains("pasa sobre"));
        Assert.NotNull(plan.Approach);
    }

    [Theory]
    [InlineData("17L")]
    [InlineData("17R")]
    [InlineData("35L")]
    [InlineData("35R")]
    public void Arrivals_from_every_direction_have_no_reversals(string runway)
    {
        var bad = new List<string>();
        for (var bearing = 0; bearing < 360; bearing += 30)
        {
            var from = GeoMath.Destination(Scel.Value.Reference, bearing, 250 * Nm);
            var plan = FlightPlanBuilder.Assemble(Arrival(runway, from, "XXXX"), ResolvedRoute.Direct, PerformanceCategory.MediumJet);
            foreach (var issue in plan.Issues)
                bad.Add($"{bearing}°: {plan.Star ?? "sin STAR"} {plan.Approach}: {issue}");
        }
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private static readonly Lazy<AirportData> Lebl = new(() => Load("LEBL"));
    private static readonly Lazy<AirportData> Levc = new(() => Load("LEVC"));

    /// <summary>Los fijos con nombre del plan, para el mensaje de un test que falla.</summary>
    private static string Fixes(PlannedFlight plan) =>
        string.Join(" ", Enumerable.Range(0, plan.Path.Count).Where(i => plan.Path[i].IsFix).Select(i => plan.Path[i].Name));

    // Capturas de Marco (2026-11-01, LEBL): VLG95G GCLP→LEBL 06R y VLG25QJ LEMD→LEBL 06L volaban el trombón BL5xx entero y
    // daban media vuelta en BL530/BL529; ANE8651 EDDH→LEVC 12 hacía un rizo en R300R al final del arco DME.
    [Theory]
    [InlineData("LEBL", "06R", 225.0)] // desde Canarias (suroeste)
    [InlineData("LEBL", "06L", 260.0)] // desde Madrid (oeste)
    [InlineData("LEBL", "06R", 20.0)]  // desde el norte
    [InlineData("LEVC", "12", 15.0)]   // desde Hamburgo (norte)
    public void Spanish_arrivals_turn_base_instead_of_flying_the_whole_trombone(string icao, string runway, double fromBearing)
    {
        var airport = icao == "LEBL" ? Lebl.Value : Levc.Value;
        var landing = FlightPlanner.LandingFor(airport, null, runway, PerformanceCategory.MediumJet)!;
        var from = GeoMath.Destination(airport.Reference, fromBearing, 300 * Nm);
        var ends = new FlightEnds("XXXX", from, 0, null, null, icao, airport.Reference, airport.ElevationFt, airport, landing);
        var plan = FlightPlanBuilder.Assemble(ends, ResolvedRoute.Direct, PerformanceCategory.MediumJet);
        var info = $"{plan.Star} {plan.Approach}: {Fixes(plan)} | {string.Join("; ", plan.Issues)}";
        Assert.NotNull(plan.Approach);
        Assert.DoesNotContain(plan.Issues, i => i.Contains("media vuelta"));
        // La base se da cerca: nada de la llegada final más allá de 30 NM del umbral por el lado de la final.
        var outbound = GeoMath.NormalizeDeg(landing.HeadingDeg + 180);
        var firstFinal = Enumerable.Range(0, plan.Path.Count).First(i => plan.Path[i].Section == PathSection.Final);
        var along = GeoMath.DistanceM(landing.Threshold, plan.Path[firstFinal].Position) / Nm;
        Assert.True(along < 16, $"intercepta la final a {along:0} NM · {info}");
    }

    [Fact]
    public void Route_text_names_each_airway_with_its_exit_fix()
    {
        // TEST DATA: fijos ficticios para el formato.
        var p = new GeoPoint(-33, -70);
        RouteFix[] fixes = [new("AAA", "", p, ""), new("BBB", "", p, "UL302"), new("CCC", "", p, "UL302"), new("DDD", "", p, "UM795"), new("EEE", "", p, "")];
        Assert.Equal("AAA UL302 CCC UM795 DDD DCT EEE", FlightPlanBuilder.RouteText(fixes));
    }
}
