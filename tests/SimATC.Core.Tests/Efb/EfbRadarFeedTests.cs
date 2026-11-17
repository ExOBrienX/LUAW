using System.Text.Json;
using SimATC.Core.Efb;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Efb;

public class EfbRadarFeedTests
{
    private static readonly GeoPoint Scel = new(-33.393, -70.786);

    private static EfbTarget At(uint id, double northNm, double eastNm, double altFt, string cs = "LAN1") =>
        new(id, cs, "A320", GeoMath.Destination(GeoMath.Destination(Scel, 0, northNm * 1852), 90, eastNm * 1852), 180, altFt, 250,
            false, false, "SCEL", "SCIE");

    private static JsonElement Parse(ReadOnlyMemory<byte> json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Sends_ownship_and_traffic_sorted_by_distance_within_range()
    {
        var feed = new EfbRadarFeed();
        var me = new EfbOwnship(Scel, 170, 1600, 0, true);
        var root = Parse(feed.Build(me, [At(1, 20, 0, 9000, "FAR"), At(2, 0, 5, 3000, "NEAR"), At(3, 150, 0, 30000, "OUT")], 0));
        Assert.Equal(-33.393, root.GetProperty("me").GetProperty("lat").GetDouble(), 3);
        var t = root.GetProperty("traffic");
        Assert.Equal(2, t.GetArrayLength());
        Assert.Equal("NEAR", t[0].GetProperty("cs").GetString());
        Assert.Equal(5.0, t[0].GetProperty("nm").GetDouble(), 1);
        Assert.Equal(90, t[0].GetProperty("brg").GetDouble(), 0);
        Assert.Equal("FAR", t[1].GetProperty("cs").GetString());
        Assert.Equal("SCIE", t[1].GetProperty("to").GetString());
    }

    [Fact]
    public void Marks_climbing_and_descending_against_the_previous_message()
    {
        var feed = new EfbRadarFeed();
        var me = new EfbOwnship(Scel, 0, 1600, 0, true);
        feed.Build(me, [At(1, 10, 0, 5000), At(2, 12, 0, 9000), At(3, 14, 0, 7000)], 0);
        var t = Parse(feed.Build(me, [At(1, 10, 0, 5100), At(2, 12, 0, 8900), At(3, 14, 0, 7002)], 2)).GetProperty("traffic");
        Assert.Equal(1, t[0].GetProperty("trend").GetInt32());   // +3000 ft/min
        Assert.Equal(-1, t[1].GetProperty("trend").GetInt32());  // −3000 ft/min
        Assert.Equal(0, t[2].GetProperty("trend").GetInt32());   // nivelado
    }

    [Fact]
    public void Without_ownship_there_is_no_traffic()
    {
        var root = Parse(new EfbRadarFeed().Build(null, [At(1, 1, 1, 3000)], 0));
        Assert.False(root.TryGetProperty("me", out _));
        Assert.Equal(0, root.GetProperty("traffic").GetArrayLength());
    }
}
