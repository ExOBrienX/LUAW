using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Geo;

public class GeoMathTests
{
    // TEST DATA: punto cualquiera cerca de Santiago.
    private static readonly GeoPoint Origin = new(-33.39, -70.79);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(177.42)]
    [InlineData(270)]
    public void Destination_ThenDistanceAndBearing_RoundTrip(double bearing)
    {
        var p = GeoMath.Destination(Origin, bearing, 1873.5);

        Assert.Equal(1873.5, GeoMath.DistanceM(Origin, p), 3);
        Assert.Equal(0, GeoMath.AngleDiffDeg(bearing, GeoMath.InitialBearingDeg(Origin, p)), 3);
    }

    [Theory]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, -20)]
    [InlineData(0, 180, 180)]
    [InlineData(90, 90, 0)]
    public void AngleDiffDeg_TakesShortestWay(double from, double to, double expected)
    {
        Assert.Equal(expected, GeoMath.AngleDiffDeg(from, to), 9);
    }

    [Fact]
    public void DistanceM_OneDegreeOfLatitude_IsAbout111Km()
    {
        var d = GeoMath.DistanceM(new GeoPoint(0, 0), new GeoPoint(1, 0));
        Assert.Equal(111195, d, 0);
    }

    [Theory]
    [InlineData(-10, 350)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void NormalizeDeg_WrapsToZero360(double input, double expected)
    {
        Assert.Equal(expected, GeoMath.NormalizeDeg(input), 9);
    }

    [Fact]
    public void LocalProjection_RoundTrip_AndMatchesGreatCircleAtAirportScale()
    {
        var proj = new LocalProjection(Origin);
        var local = new LocalPoint(-330.5, -340.1);

        var geo = proj.ToGeo(local);
        var back = proj.ToLocal(geo);

        Assert.Equal(local.EastM, back.EastM, 6);
        Assert.Equal(local.NorthM, back.NorthM, 6);
        // A escala de aeropuerto la proyección y el círculo máximo difieren en menos de 1 cm.
        Assert.Equal(LocalPoint.Distance(default, local), GeoMath.DistanceM(Origin, geo), 2);
    }

    [Fact]
    public void LocalProjection_EastAndNorthAxes()
    {
        var proj = new LocalProjection(Origin);

        var east = proj.ToLocal(GeoMath.Destination(Origin, 90, 500));
        var north = proj.ToLocal(GeoMath.Destination(Origin, 0, 500));

        Assert.Equal(500, east.EastM, 1);
        Assert.Equal(0, east.NorthM, 1);
        Assert.Equal(0, north.EastM, 1);
        Assert.Equal(500, north.NorthM, 1);
    }
}
