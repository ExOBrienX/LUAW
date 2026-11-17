using SimATC.Core.Geo;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Ruta del plan con arcos de giro fly-by (como la dibuja un FMS).</summary>
public class RouteGeometryTests
{
    private static readonly GeoPoint A = new(-33.0, -71.0);

    [Fact]
    public void A_straight_route_has_no_arcs()
    {
        var b = GeoMath.Destination(A, 90, 50_000);
        var c = GeoMath.Destination(b, 90, 50_000);
        Assert.Equal(3, RouteGeometry.FlyBy([A, b, c]).Count);
    }

    [Fact]
    public void A_right_angle_turn_is_an_arc_tangent_to_both_legs()
    {
        var corner = GeoMath.Destination(A, 90, 80_000);
        var end = GeoMath.Destination(corner, 180, 80_000);
        var pts = RouteGeometry.FlyBy([A, corner, end]);
        Assert.True(pts.Count > 10);
        // No pasa por la esquina: el punto más cercano está a r·(√2 − 1) ≈ 4.1 km de ella.
        var closest = pts.Min(p => GeoMath.DistanceM(p, corner));
        Assert.InRange(closest, 3_500, 4_700);
        // El arco empieza 10 km antes de la esquina, sobre el primer tramo, y termina 10 km después, sobre el segundo.
        var arc = pts.Skip(1).Take(pts.Count - 2).ToList();
        Assert.InRange(GeoMath.DistanceM(arc[0], corner), 9_500, 10_500);
        Assert.InRange(GeoMath.DistanceM(arc[^1], corner), 9_500, 10_500);
        Assert.InRange(GeoMath.InitialBearingDeg(A, arc[0]), 89, 91);
    }

    [Fact]
    public void Short_legs_shrink_the_radius_so_the_arc_fits()
    {
        var corner = GeoMath.Destination(A, 90, 6_000);
        var end = GeoMath.Destination(corner, 0, 6_000);
        var pts = RouteGeometry.FlyBy([A, corner, end]);
        // El arco empieza a mitad del tramo (3 km), no 10 km antes (antes del punto anterior).
        Assert.InRange(GeoMath.DistanceM(pts[1], corner), 2_900, 3_100);
    }
}
