using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Geo;

/// <summary>Mapa base del radar (Natural Earth 1:50m).</summary>
public class WorldMapTests
{
    private static readonly GeoPoint Scel = new(-33.393, -70.786); // TEST DATA: referencia aproximada de SCEL

    [Fact]
    public void Loads_the_bundled_map()
    {
        var map = WorldMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map", "world-50m.json"));
        Assert.NotNull(map);
        Assert.True(map.Coast.Count > 1000);
        Assert.True(map.Borders.Count > 100);
    }

    [Fact]
    public void Near_keeps_only_what_is_inside_the_radius()
    {
        var map = WorldMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map", "world-50m.json"))!;
        var near = WorldMap.Near(map.Coast, Scel, 300_000);

        Assert.NotEmpty(near); // la costa de Chile central está a ~100 km
        Assert.All(near, line => Assert.All(line, p => Assert.True(GeoMath.DistanceM(p, Scel) <= 300_000)));
    }

    [Fact]
    public void A_line_leaving_and_entering_the_radius_is_split()
    {
        // TEST DATA: línea que pasa por el centro, sale lejos y vuelve.
        double[] line = [0, 0, 0.1, 0, 10, 0, 0.2, 0, 0.3, 0];
        var parts = WorldMap.Near([line], new GeoPoint(0, 0), 100_000);

        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void Missing_file_is_null()
    {
        Assert.Null(WorldMap.Load(Path.Combine(Path.GetTempPath(), "no-existe-simatc.json")));
    }
}
