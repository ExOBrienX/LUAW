using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;
using SimATC.Core.Traffic;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Traffic;

/// <summary>ADR-167: fuselajes anchos donde los datos del simulador solo tienen puertas chicas.</summary>
public class WideStandsTests
{
    private static readonly Lazy<AirportData> Lebl = new(() => Load("LEBL"));

    private static AirportData Load(string icao) =>
        AirportCache.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", icao + ".json")))!.Airport;

    // TEST DATA: medidas redondas.
    private static AircraftModel Plane(string type, string airline, double span, double nose, WakeCategory wake) =>
        new($"T_{type}_{airline}", ModelSource.Fsltl, type, airline, wake, 2, span, ["GATE"], [], nose);

    private static ModelMatcher Matcher() => new(
        [
            Plane("B789", "LON", 60, 23, WakeCategory.Heavy), Plane("A388", "LON", 80, 31, WakeCategory.Super),
            Plane("A320", "SHO", 35.8, 13, WakeCategory.Medium), Plane("B789", "SHO", 60, 23, WakeCategory.Heavy),
        ], []);

    private static RouteList Routes(double longNm) => new("LEBL", new RouteSource("test", "", 0, ""), true, null,
    [
        new RouteAirline("SHO", "SHO", true, false, [], Enumerable.Range(0, 40).Select(i => $"D{i:000}").ToList(), []) { LegsNm = [400, 900], Weight = 40 },
        new RouteAirline("LON", "LON", true, false, [], ["KAAA", "KBBB", "KCCC", "KDDD", "KEEE", "KFFF"], [])
        {
            LegsNm = [.. Enumerable.Repeat(longNm, 6)],
            Weight = 6,
        },
    ]);

    private static List<ParkedAircraft> Plan(int seed, double longNm = 3800)
    {
        var matcher = Matcher();
        return ParkedFleetPlanner.PlanForLevel(Lebl.Value, Routes(longNm), matcher, matcher.TypesFor, TrafficPreset.For(TrafficLevel.High, 40), 6, null,
            seed, wide: WideStands.For(Lebl.Value, TaxiGraph.Build(Lebl.Value)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Wide_bodies_park_at_gates_with_real_room_and_the_next_ones_stay_empty(int seed)
    {
        var matcher = Matcher();
        var plan = Plan(seed);
        var wide = plan.Where(p => p.WideHalfSpanM > 0).ToList();
        Assert.NotEmpty(wide);
        Assert.All(wide, p => Assert.Equal("LON", p.Airline)); // la de corto radio no, aunque tenga un fuselaje ancho
        Assert.All(wide.Where(p => p.Type == "A388"), p => Assert.True(p.WideHalfSpanM >= WideStands.SuperHalfSpanM));
        foreach (var w in wide)
        {
            foreach (var other in plan.Where(p => p != w))
            {
                Assert.True(GeoMath.DistanceM(w.Parking.Position, other.Parking.Position)
                    >= w.WideHalfSpanM + matcher.WingspanFor(other.Match.Model) / 2);
            }
        }
    }

    /// <summary>KMIA 2026-11-25: el A380 real de Miami no tenía dónde estacionar (ninguna puerta de su tamaño en los datos).</summary>
    [Fact]
    public void An_A380_needs_more_room_than_a_787()
    {
        var wide = WideStands.For(Lebl.Value, TaxiGraph.Build(Lebl.Value));
        var a380 = Plane("A388", "LON", 71.6, 31, WakeCategory.Super);
        var b787 = Plane("B789", "LON", 60, 23, WakeCategory.Heavy);
        Assert.Equal(WideStands.SuperHalfSpanM, WideStands.HalfSpanFor(a380, 71.6));
        var big = wide.Candidates.Count(i => wide.BackFor(i, a380, WideStands.SuperHalfSpanM) is not null);
        Assert.InRange(big, 1, wide.Candidates.Count(i => wide.BackFor(i, b787, 30) is not null) - 1);
    }

    [Fact]
    public void Without_long_flights_there_are_none()
    {
        Assert.DoesNotContain(Plan(1, longNm: 1500), p => p.WideHalfSpanM > 0);
    }

    [Fact]
    public void Airports_with_gates_of_their_size_do_not_need_it()
    {
        var scel = Load("SCEL");
        Assert.Equal(0, WideStands.For(scel, TaxiGraph.Build(scel)).Max);
    }

    [Fact]
    public void The_wide_body_stops_further_back_never_over_the_taxiway()
    {
        var graph = TaxiGraph.Build(Lebl.Value);
        var wide = WideStands.For(Lebl.Value, graph);
        var model = Plane("B77W", "LON", 64.8, 31, WakeCategory.Heavy);
        var fits = wide.Candidates.Where(i => wide.BackFor(i, model, 32.4) is not null).ToList();
        foreach (var i in fits)
        {
            var p = Lebl.Value.Parkings[i];
            var back = wide.BackFor(i, model, 32.4)!.Value;
            Assert.True(back > 0);
            var tail = SimATC.Core.Engine.Polyline.Offset(graph.Projection.ToLocal(p.Position), p.HeadingTrueDeg + 180, back + 31 + 5.5);
            foreach (var (a, b, spur) in graph.TaxiEdgesNear(tail, 60))
            {
                if (spur < 0)
                    Assert.True(LocalPoint.Distance(graph.Position(a), tail) > 15 && LocalPoint.Distance(graph.Position(b), tail) > 15);
            }
        }
    }

    [Fact]
    public void A_big_airline_seen_once_does_not_operate_there()
    {
        // TEST DATA: 120 vuelos de la aerolínea BIG en otro lado y uno solo en AAAA, que tiene mucho tráfico de otra.
        var text = new System.Text.StringBuilder("Callsign,AirportCodes\n");
        for (var i = 0; i < 120; i++)
            text.Append($"BIG{i},CCCC-DDDD\n").Append($"LOC{i},AAAA-BBBB\n");
        text.Append("BIG900,EEEE-AAAA-FFFF\nSML1,AAAA-BBBB\n");
        var routes = WorldRoutes.ParseZone(new StringReader(text.ToString()), new HashSet<string> { "AAAA" }, new GeoPoint(0, 0), 1);
        Assert.True(routes.IsStray("BIG", "AAAA"));
        Assert.False(routes.IsStray("LOC", "AAAA"));
        Assert.False(routes.IsStray("SML", "AAAA")); // chica: puede tener un solo vuelo
    }
}
