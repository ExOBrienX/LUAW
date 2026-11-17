using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Flight;

/// <summary>Rutas por aerovías (RF-20b, ADR-058) sobre un grafo pequeño. TEST DATA: fijos y aerovías inventados.</summary>
public class AirwayRouterTests
{
    private static AirwayFix Fix(string id, double lat, double lon, params (string Airway, string To, double Lat, double Lon)[] next) =>
        new(id, "XX", new GeoPoint(lat, lon),
            [.. next.Select(n => new AirwayLink(n.Airway, 2, n.To, "XX", new GeoPoint(n.Lat, n.Lon), 0, true))]);

    [Fact]
    public async Task Finds_the_airway_route_toward_the_target()
    {
        // A → B → C por UA1; A → D por UA2 (lejos del objetivo). Objetivo cerca de C.
        var fixes = new Dictionary<(string, string), AirwayFix>
        {
            [("A", "XX")] = Fix("A", -33, -71, ("UA1", "B", -32, -71), ("UA2", "D", -34, -71)),
            [("B", "XX")] = Fix("B", -32, -71, ("UA1", "C", -31, -71)),
            [("C", "XX")] = Fix("C", -31, -71),
            [("D", "XX")] = Fix("D", -34, -71),
        };
        var db = new AirwayDatabase((i, r, _) => Task.FromResult(fixes.GetValueOrDefault((i, r))));
        var target = new GeoPoint(-30.8, -71);
        var steps = await AirwayRouter.FindAsync(db, fixes[("A", "XX")], target,
            f => AirwayRouter.DistanceNm(f.Position, target) < 20, high: true, 50, CancellationToken.None);
        Assert.NotNull(steps);
        Assert.Equal(["A", "B", "C"], steps.Select(s => s.Fix.Icao));
        Assert.Equal("UA1", steps[^1].Airway);
    }

    [Fact]
    public async Task No_route_returns_null()
    {
        var a = Fix("A", -33, -71);
        var db = new AirwayDatabase((_, _, _) => Task.FromResult<AirwayFix?>(null));
        Assert.Null(await AirwayRouter.FindAsync(db, a, new GeoPoint(-30, -71), _ => false, true, 10, CancellationToken.None));
    }

    [Fact]
    public void Vatsim_route_tokens_drop_dct_speeds_and_coordinates()
    {
        // TEST DATA: ruta tipo VATSIM (SCEL-KMIA, recortada).
        Assert.Equal(["DONTI5B", "DONTI", "UT131", "ATEDA", "UL302", "ILMAR", "EPSIM"],
            VatsimRouteStore.Tokens("DONTI5B DONTI UT131 ATEDA UL302 ILMAR/N0491F340 DCT 30S070W EPSIM"));
    }

    [Fact]
    public async Task A_written_route_is_expanded_along_the_airway()
    {
        var fixes = new Dictionary<(string, string), AirwayFix>
        {
            [("AA", "XX")] = Fix("AA", -33, -71, ("UA1", "BB", -32, -71)),
            [("BB", "XX")] = Fix("BB", -32, -71, ("UA1", "CC", -31, -71)),
            [("CC", "XX")] = Fix("CC", -31, -71),
        };
        var db = new AirwayDatabase((i, r, _) => Task.FromResult(fixes.GetValueOrDefault((i, r))));
        var steps = await RouteExpander.ExpandAsync(db, VatsimRouteStore.Tokens("SID1A AA UA1 CC STAR2B"), ["XX"], CancellationToken.None);
        Assert.NotNull(steps);
        Assert.Equal(["AA", "BB", "CC"], steps.Select(st => st.Fix.Icao));
    }
}
