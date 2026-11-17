using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Geo;

/// <summary>Proyección conforme del radar: conserva rumbos lejos del origen (ADR-026).</summary>
public class MercatorProjectionTests
{
    // TEST DATA: referencia aproximada de SCEL; KALIS y RW02 (umbral) de SCIE, de los datos del simulador redondeados.
    private static readonly GeoPoint Scel = new(-33.3944, -70.7938);
    private static readonly GeoPoint Kalis = new(-36.8268, -73.0943);
    private static readonly GeoPoint Rw02 = new(-36.7820, -73.0681);

    private static double PlaneBearing(MercatorProjection m, GeoPoint a, GeoPoint b)
    {
        var p = m.ToPlane(a);
        var q = m.ToPlane(b);
        return GeoMath.NormalizeDeg(Math.Atan2(q.EastM - p.EastM, q.NorthM - p.NorthM) / GeoMath.DegToRad);
    }

    [Fact]
    public void Keeps_true_bearings_far_from_the_origin()
    {
        var m = new MercatorProjection(Scel);
        var drawn = PlaneBearing(m, Kalis, Rw02);
        var real = GeoMath.InitialBearingDeg(Kalis, Rw02);
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(real, drawn)) < 0.1, $"dibujado {drawn:F2}°, real {real:F2}°");
    }

    [Fact]
    public void Equals_the_local_projection_near_the_origin()
    {
        var m = new MercatorProjection(Scel);
        var l = new LocalProjection(Scel);
        var p = GeoMath.Destination(Scel, 40, 3000); // 3 km: escala de aeropuerto
        Assert.True(LocalPoint.Distance(m.ToPlane(p), l.ToLocal(p)) < 1);
    }

    [Fact]
    public void Round_trips()
    {
        var m = new MercatorProjection(Scel);
        var back = m.ToGeo(m.ToPlane(Kalis));
        Assert.Equal(Kalis.LatDeg, back.LatDeg, 9);
        Assert.Equal(Kalis.LonDeg, back.LonDeg, 9);
    }

    /// <summary>SCEL→YSSY: Sídney queda al oeste de Santiago en el radar, no dando la vuelta al mapa (ADR-077).</summary>
    [Fact]
    public void Across_the_antimeridian_the_short_way()
    {
        var m = new MercatorProjection(new GeoPoint(-33.39, -70.79)); // SCEL
        var sydney = m.ToPlane(new GeoPoint(-33.95, 151.18));
        Assert.True(sydney.EastM < 0, $"Sídney al este: {sydney.EastM / 1000:0} km");
        var back = m.ToGeo(sydney);
        Assert.Equal(151.18, back.LonDeg, 6);
    }
}
