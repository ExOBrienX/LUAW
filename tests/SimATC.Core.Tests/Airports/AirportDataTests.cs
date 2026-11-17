using SimATC.Core.Airports;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Airports;

public class AirportDataTests
{
    [Fact]
    public void Runway_FromCenter_PlacesPrimaryEndBehindHeading()
    {
        var center = new GeoPoint(-33.39, -70.79);

        var rwy = Runway.FromCenter(center, 177.42, 3747, 55, 1555, 17, RunwayDesignator.Left, 35, RunwayDesignator.Right);

        Assert.Equal("17L/35R", rwy.Name);
        Assert.Equal(3747, GeoMath.DistanceM(rwy.Primary.Position, rwy.Secondary.Position), 1);
        // Desde el extremo 17L se despega con rumbo 177.42 hacia el extremo 35R.
        Assert.Equal(177.42, GeoMath.InitialBearingDeg(rwy.Primary.Position, rwy.Secondary.Position), 2);
        Assert.Equal(177.42, rwy.Primary.HeadingTrueDeg, 6);
        Assert.Equal(357.42, rwy.Secondary.HeadingTrueDeg, 6);
        Assert.True(rwy.Primary.Position.LatDeg > rwy.Secondary.Position.LatDeg); // 17 queda al norte
    }

    [Theory]
    [InlineData(17, RunwayDesignator.Left, "17L")]
    [InlineData(35, RunwayDesignator.Right, "35R")]
    [InlineData(9, RunwayDesignator.None, "09")]
    [InlineData(1, RunwayDesignator.Center, "01C")]
    public void RunwayEnd_FormatIdent(int number, RunwayDesignator designator, string expected)
    {
        Assert.Equal(expected, RunwayEnd.FormatIdent(number, designator));
    }
}
