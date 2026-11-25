using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Traffic;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Fleet;

/// <summary>
/// ADR-166 (KMIA 2026-11-25): aviones blancos de Qatar y Etihad, aerolíneas ficticias e históricas, un A321 a Doha y un CRJ7
/// desde San Francisco.
/// </summary>
public class RealFleetTests
{
    // TEST DATA: catálogo inventado con los nombres que usan los paquetes.
    private static AircraftModel M(string title, string type, string airline, WakeCategory wake = WakeCategory.Medium, bool cargo = false) =>
        new(title, ModelSource.Fsltl, type, airline, wake, 2, wake == WakeCategory.Heavy ? 60 : 34, [cargo ? "CARGO" : "GATE"], []);

    private static ModelMatcher Matcher() => new(
        [
            M("T_A21N_QQQ_Airways-STUB", "A21N", "QQQ"),
            M("T_A359_QQQ", "A359", "QQQ", WakeCategory.Heavy),
            M("T_B77L_QQQ_Cargo", "B77L", "QQQ", WakeCategory.Heavy, cargo: true),
            M("T_A320_SSS-STUB", "A320", "SSS"),
            M("T_A320_OLD_Historical", "A320", "OLD"),
            M("T_A320_VVV_Virtual_Fictional", "A320", "VVV"),
            M("T_A320_NNN", "A320", "NNN"),
            M("T_CRJ7_NNN", "CRJ7", "NNN"),
            M("T_B788_NNN", "B788", "NNN", WakeCategory.Heavy),
            M("T_A320_ZZZZ", "A320", ""),
        ], []);

    [Fact]
    public void White_and_old_liveries_are_only_used_by_airlines_without_another_one()
    {
        var m = Matcher();
        Assert.Equal(["A359", "B77L"], m.TypesFor("QQQ"));
        Assert.True(m.HasLivery("QQQ"));
        Assert.False(m.HasLivery("SSS"));
        Assert.False(m.HasLivery("OLD"));
        Assert.Equal(["A320"], m.TypesFor("SSS"));
        Assert.Equal("T_A320_SSS-STUB", m.Match("SSS", "A320", "k")!.Model.Title);
    }

    [Fact]
    public void Fictional_liveries_are_never_used()
    {
        Assert.Empty(Matcher().TypesFor("VVV"));
        Assert.Equal("T_A320_ZZZZ", Matcher().Match("VVV", "A320", "k")!.Model.Title);
    }

    [Fact]
    public void A_passenger_flight_never_gets_a_freighter()
    {
        var m = Matcher();
        Assert.Null(m.MatchOwn("QQQ", "B77L", "k", cargo: false));
        Assert.NotNull(m.MatchOwn("QQQ", "B77L", "k", cargo: true));
        Assert.Null(m.MatchOwn("QQQ", "A320", "k", cargo: false)); // sin librea de ese tipo no cae a la genérica
    }

    [Theory]
    [InlineData("A320", 2300, true)]
    [InlineData("A320", 6600, false)] // Miami-Doha
    [InlineData("CRJ7", 2240, false)] // San Francisco-Miami
    [InlineData("CRJ7", 600, true)]
    [InlineData("B788", 6600, true)]
    [InlineData("B788", 200, false)] // corto, y la aerolínea tiene otros
    public void The_type_must_reach_the_destination(string type, double nm, bool ok)
    {
        var a = new RouteAirline("NNN", "NNN", true, false, [], ["XXXX"], []) { LegsNm = [nm] };
        Assert.Equal(ok, ParkedFleetPlanner.Flies(a, Matcher().MatchOwn("NNN", type, "k", cargo: false)!.Model, otherTypes: true));
    }

    [Fact]
    public void The_route_list_knows_its_distances_and_single_flights_weigh_less()
    {
        // TEST DATA: coordenadas redondas.
        var airports = WorldAirports.Parse(["ICAO,Lat,Lon,ElevFt", "AAAA,0,0,0", "BBBB,0,10,0", "CCCC,0,50,0"]);
        WorldLeg[] legs = [new("NNN1", "NNN", "AAAA", "BBBB"), new("NNN2", "NNN", "AAAA", "BBBB"), new("NNN3", "NNN", "AAAA", "CCCC")];
        var a = Assert.Single(TrafficDirector.BuildRouteList("AAAA", legs, airports).Airlines);
        Assert.Equal(1 + TrafficDirector.SingleCallsignWeight, a.Weight);
        Assert.Equal(2, a.LegsNm.Count);
        Assert.InRange(a.LegsNm.Min(), 595, 605);
    }

    /// <summary>ADR-171 (KMIA): 10 de los 16 B738 de American eran retro; Frontier repetía la misma matrícula.</summary>
    [Fact]
    public void Special_liveries_are_rare_and_never_repeat()
    {
        var m = new ModelMatcher([M("T_B738_American", "B738", "AAL"), M("T_B738_American_TWA", "B738", "AAL"), M("T_B738_American_AIRCAL", "B738", "AAL"),
            M("T_B738_American_N123AA", "B738", "AAL")], []);
        var used = new HashSet<string>();
        var plain = 0;
        for (var i = 0; i < 200; i++)
        {
            var title = m.MatchOwn("AAL", "B738", $"AAL{i}", false, used)!.Model.Title;
            Assert.True(title == "T_B738_American" || used.Add(title), title);
            if (title == "T_B738_American")
                plain++;
        }
        Assert.True(plain >= 197);
        Assert.True(Enumerable.Range(0, 200).Count(i => m.MatchOwn("AAL", "B738", $"AAL{i}", false)!.Model.Title == "T_B738_American") > 120);
    }

    [Fact]
    public void A_mixed_airline_that_never_flies_home_from_there_is_cargo()
    {
        // TEST DATA: como Korean en Miami (de Anchorage a São Paulo) y Qatar (a Doha).
        var text = "Callsign,AirportCodes\nKAL1,RKSI-RJAA\nKAL2,RKSI-PANC\nKAL3,RKSS-RKPC\nKAL4,RKSI-PANC-KMIA-SBKP\nQTR1,OTHH-EGLL\nQTR2,OTHH-KMIA\n";
        var routes = WorldRoutes.ParseZone(new StringReader(text), new HashSet<string> { "KMIA" }, new SimATC.Core.Geo.GeoPoint(0, 0), 1);
        Assert.True(routes.NeverFromHome("KAL", "KMIA"));
        Assert.False(routes.NeverFromHome("QTR", "KMIA"));
        // Turkish vuela a Santiago con escala en São Paulo: es de pasajeros.
        text = "Callsign,AirportCodes\nTHY1,LTFM-LTAC\nTHY2,LTFM-LTAI\nTHY3,LTFM-SBGR-SCEL\n";
        Assert.False(WorldRoutes.ParseZone(new StringReader(text), new HashSet<string> { "SCEL" }, new SimATC.Core.Geo.GeoPoint(0, 0), 1)
            .NeverFromHome("THY", "SCEL"));
    }

    [Fact]
    public void A_route_that_keeps_failing_is_tried_less_and_less()
    {
        Assert.Equal(TrafficDirector.RetryDirectMinutes, TrafficDirector.RetryAfterMinutes(1));
        Assert.Equal(4 * TrafficDirector.RetryDirectMinutes, TrafficDirector.RetryAfterMinutes(3));
        Assert.Equal(TrafficDirector.RetryDirectMaxMinutes, TrafficDirector.RetryAfterMinutes(40));
    }
}
