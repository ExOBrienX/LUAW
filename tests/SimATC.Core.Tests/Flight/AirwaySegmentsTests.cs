using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Flight;

public class AirwaySegmentsTests
{
    // TEST DATA: dos fijos ficticios unidos por una aerovía (cada uno ve el tramo desde su lado).
    private static readonly GeoPoint A = new(-33, -71);
    private static readonly GeoPoint B = new(-34, -70);

    [Fact]
    public void Each_segment_is_listed_once_and_found_by_box()
    {
        AirwayFix[] fixes =
        [
            new("AAAAA", "SC", A, [new AirwayLink("UL302", 2, "BBBBB", "SC", B, 0, true)]),
            new("BBBBB", "SC", B, [new AirwayLink("UL302", 2, "AAAAA", "SC", A, 0, true), new AirwayLink("UL302", 2, "AAAAA", "SC", null, 0, false)]),
        ];
        var index = AirwaySegments.Build(fixes);
        Assert.Equal(1, index.Count);
        var found = new List<AirwaySegment>();
        index.InBox(-33.6, -33.4, -70.6, -70.4, found);
        Assert.Single(found);
        index.InBox(10, 11, 10, 11, found);
        Assert.Empty(found);
    }
}
