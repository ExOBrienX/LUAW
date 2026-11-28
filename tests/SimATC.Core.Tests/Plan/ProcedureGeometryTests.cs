using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Tramos sin punto final (rumbo hasta altitud o hasta vectores, MANUAL) con su viraje estimado (ADR-102).</summary>
public class ProcedureGeometryTests
{
    private static readonly GeoPoint Start = new(-33.39, -70.79); // TEST DATA

    [Theory]
    [InlineData(0, 90, TurnDirection.None, true)]
    [InlineData(0, 90, TurnDirection.Left, false)] // a la izquierda aunque sea el lado largo (270°)
    [InlineData(170, 350, TurnDirection.Right, true)]
    public void Turn_then_straight_turns_smoothly_to_the_course(double heading, double course, TurnDirection turn, bool right)
    {
        var pts = ProcedureGeometry.TurnThenStraight(Start, heading, course, turn, 2 * 1852);
        Assert.True(pts.Count > 3, "sin viraje");
        var prev = heading;
        var total = 0.0;
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var h = GeoMath.InitialBearingDeg(pts[i - 1], pts[i]);
            var d = GeoMath.AngleDiffDeg(prev, h);
            Assert.True(Math.Abs(d) <= 10, $"quiebre de {d:0}° en el punto {i}");
            Assert.True(right ? d >= -1 : d <= 1, $"gira para el otro lado ({d:0}°)");
            total += Math.Abs(d);
            prev = h;
        }
        var last = GeoMath.InitialBearingDeg(pts[^2], pts[^1]);
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(last, course)) < 1, $"termina en {last:0}°");
    }

    [Fact]
    public void Same_heading_is_straight()
    {
        var pts = ProcedureGeometry.TurnThenStraight(Start, 90, 91, TurnDirection.None, 1852);
        Assert.Equal(2, pts.Count);
        Assert.Equal(1852, GeoMath.DistanceM(pts[0], pts[1]), 0);
    }

    private static ProcedureLeg Leg(LegType type, string fix, GeoPoint at, double course = 0) =>
        new(type, fix, "XX", "W", at, false, TurnDirection.None, course, false, 0, false,
            AltitudeConstraint.None, 0, 0, SpeedConstraint.None, -1, 0, default, 0, false, false, false, false);

    /// <summary>
    /// Prueba de Marco (EDDF TUTO1D, 2026-11-27): tras "desde DF444 rumbo 245 hasta vectores" viene "curso 065 a DF554". Por
    /// dónde llega a DF554 lo decide el ATC: ese tramo va punteado, no como si fuera exacto.
    /// </summary>
    [Fact]
    public void The_leg_after_vectors_is_drawn_approximate()
    {
        var a = Start; // TEST DATA
        var b = GeoMath.Destination(a, 245, 15000);
        var c = GeoMath.Destination(b, 160, 12000);
        var shape = ProcedureGeometry.Build(
            [Leg(LegType.IF, "AAAAA", a), Leg(LegType.TF, "BBBBB", b), Leg(LegType.FM, "BBBBB", b, 245), Leg(LegType.CF, "CCCCC", c, 65)],
            null, 0, 0, 10000);
        var last = shape.Segments[^1];
        Assert.True(last.Approximate);
        Assert.True(GeoMath.DistanceM(last.Points[^1], c) < 1);
        Assert.False(shape.Segments[0].Approximate); // AAAAA-BBBBB sigue exacto
    }
}
