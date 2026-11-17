using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Traffic;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.Traffic;

public class ParkedFleetPlannerTests
{
    // TEST DATA: aerolínea, modelos y rutas inventados.
    private static RouteList Routes(params RouteAirline[] airlines) =>
        new("TEST", new RouteSource("test", "", 0, ""), true, null, airlines);

    private static RouteAirline Airline(string icao, params string[] types) =>
        new(icao, icao, true, false, types, ["DEST1", "DEST2"], []);

    private static AircraftModel M(string title, string type, string airline, double span) =>
        new(title, ModelSource.Fsltl, type, airline, WakeCategory.Medium, 2, span, ["GATE"], []);

    private static ModelMatcher Matcher(double span = 34) =>
        new([M("T_A320_XYZ", "A320", "XYZ", span), M("T_A320_ZZZZ", "A320", "", span)], []);

    [Fact]
    public void Plan_FillsFittingStands_AndDoesNotRepeatThem()
    {
        var airport = TestAirports.Small();

        var parked = ParkedFleetPlanner.Plan(airport, Routes(Airline("XYZ", "A320")), Matcher(), _ => [], 5, user: null);

        Assert.Equal(2, parked.Count); // el aeropuerto de prueba tiene 2 puertas
        Assert.Equal(2, parked.Select(p => p.ParkingIndex).Distinct().Count());
        Assert.All(parked, p => Assert.Equal(MatchKind.AirlineLivery, p.Match.Kind));
    }

    [Fact]
    public void Plan_NeverUsesTheStandWhereThePilotIs()
    {
        var airport = TestAirports.Small();
        var pilot = airport.Parkings[0].Position;

        var parked = ParkedFleetPlanner.Plan(airport, Routes(Airline("XYZ", "A320")), Matcher(), _ => [], 5, pilot);

        Assert.Single(parked);
        Assert.Equal(1, parked[0].ParkingIndex);
    }

    [Fact]
    public void Plan_SkipsAircraftTooBigForEveryStand()
    {
        var parked = ParkedFleetPlanner.Plan(TestAirports.Small(), Routes(Airline("XYZ", "A320")), Matcher(span: 70), _ => [], 5, null);

        Assert.Empty(parked); // media envergadura 35 m > radio 20 m
    }

    [Fact]
    public void Plan_UsesInstalledTypesWhenRoutesHaveNone()
    {
        var parked = ParkedFleetPlanner.Plan(TestAirports.Small(), Routes(Airline("XYZ")), Matcher(), a => a == "XYZ" ? ["A320"] : [], 1, null);

        Assert.Single(parked);
        Assert.Equal("A320", parked[0].Type);
    }

    [Fact]
    public void Plan_IsDeterministicForTheSameSeed()
    {
        var a = ParkedFleetPlanner.Plan(TestAirports.Small(), Routes(Airline("XYZ", "A320")), Matcher(), _ => [], 2, null, seed: 7);
        var b = ParkedFleetPlanner.Plan(TestAirports.Small(), Routes(Airline("XYZ", "A320")), Matcher(), _ => [], 2, null, seed: 7);

        Assert.Equal(a.Select(p => (p.TailNumber, p.ParkingIndex)), b.Select(p => (p.TailNumber, p.ParkingIndex)));
    }

    [Fact]
    public void TrafficLimits_GroundRoomRespectsGroundAndTotal()
    {
        var limits = new TrafficLimits(MaxGround: 25, MaxAir: 15, MaxTotal: 40);

        Assert.Equal(25, limits.GroundRoom(0, 0));
        Assert.Equal(5, limits.GroundRoom(20, 0));
        Assert.Equal(2, limits.GroundRoom(20, 18)); // el total manda
        Assert.Equal(0, limits.GroundRoom(30, 0));
    }

    [Fact]
    public void RouteList_ScelFile_ParsesWithSourceAndAirlines()
    {
        var routes = RouteList.Load(Path.Combine(AppContext.BaseDirectory, "Data", "routes", "SCEL.json"));

        Assert.NotNull(routes);
        Assert.Equal("SCEL", routes.Airport);
        Assert.Equal(1381206791, routes.Source.Revision);
        Assert.Contains(routes.Airlines, a => a.Icao == "SKU" && a.Destinations.Contains("Lima"));
        Assert.All(routes.Airlines, a => Assert.Equal(3, a.Icao.Length));
    }

    /// <summary>
    /// ADR-085 (prueba de Marco en SCIE): se estaciona solo una parte de los puestos y el del piloto queda libre, de los
    /// que caben su envergadura y sin ninguno ocupado.
    /// </summary>
    [Fact]
    public void Only_part_of_the_stands_is_filled_and_the_pilot_gets_a_free_one()
    {
        var scie = SimAirports.Load("SCIE.json");
        var stands = scie.Parkings.Count(ParkedFleetPlanner.AirlineStand);
        var target = ParkedFleetPlanner.FillTarget(scie, 20);
        Assert.True(target < stands, $"{target} de {stands}");
        Assert.Equal((int)Math.Floor(stands * ParkedFleetPlanner.FillShare), target);
        var busy = new HashSet<int>();
        var gates = Enumerable.Range(0, scie.Parkings.Count).Where(i => scie.Parkings[i].Type.ToString().StartsWith("Gate")).ToList();
        busy.UnionWith(gates.Take(gates.Count - 1));
        var pilot = ParkedFleetPlanner.PilotStand(scie, "SKU", 36, busy.Contains, new Random(1));
        Assert.True(pilot < 0 || !busy.Contains(pilot));
        Assert.Equal(-1, ParkedFleetPlanner.PilotStand(scie, "SKU", 36, _ => true, new Random(1)));
    }
}
