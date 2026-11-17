using SimATC.Core.Engine;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Engine;

public class EngineBasicsTests
{
    [Fact]
    public void SimClock_ProducesFixedTicks_AndKeepsRemainderForInterpolation()
    {
        var clock = new SimClock();

        Assert.Equal(0, clock.Advance(0.05, 1, false));
        Assert.Equal(1, clock.Advance(0.06, 1, false)); // 0,11 s acumulados → 1 tick
        Assert.Equal(0.1, clock.SimTime, 9);
        Assert.Equal(0.1, clock.Alpha, 6);              // sobran 0,01 s = 10 % del siguiente tick
    }

    [Fact]
    public void SimClock_RespectsPauseAndSimRate()
    {
        var clock = new SimClock();

        Assert.Equal(0, clock.Advance(1.0, 1, paused: true));
        Assert.Equal(0, clock.SimTime);
        Assert.Equal(4, clock.Advance(0.2, simRate: 2, paused: false)); // 0,2 s reales a x2 = 4 ticks
    }

    [Fact]
    public void SimClock_CapsTicksAfterLongStall()
    {
        var clock = new SimClock();

        Assert.Equal(SimClock.MaxTicksPerFrame, clock.Advance(60, 1, false));
        Assert.Equal(0, clock.Alpha);
    }

    [Fact]
    public void GroundEstimator_SmoothsNoise_IgnoresSingleSpike_AcceptsRealStep()
    {
        var g = new GroundEstimator();
        g.AddSample(1566.3, 11.3);                 // suelo 1555,0
        g.AddSample(1566.3, 11.4);                 // ruido pequeño: se suaviza
        Assert.Equal(1554.975, g.GroundFt, 3);

        g.AddSample(1566.3, 12.76);                // salto aislado (como en SCEL): se ignora
        Assert.Equal(1554.975, g.GroundFt, 3);

        for (var i = 0; i < GroundEstimator.OutliersToAccept; i++)
            g.AddSample(1566.3, 9.3);              // el terreno subió 2 ft de verdad
        Assert.Equal(1557.0, g.GroundFt, 3);
    }

    // TEST DATA: ruta en L de 200 m al norte y 200 m al este, suavizada con curvas de 45 m (sin límite de desvío).
    private static readonly Polyline LRoute = Polyline.Smooth([new LocalPoint(0, 0), new LocalPoint(0, 200), new LocalPoint(200, 200)], 45, maxDeviationM: 100);

    [Fact]
    public void Smooth_ReplacesCornerWithArcOfTheGivenRadius()
    {
        // Punto medio del arco: a R·(√2 − 1) ≈ 18,6 m de la esquina, hacia adentro.
        var mid = LRoute.PointAt(LRoute.Length / 2);
        Assert.Equal(45 * (Math.Sqrt(2) - 1), LocalPoint.Distance(mid, new LocalPoint(0, 200)), 0);
        Assert.Equal(0, LRoute.HeadingAt(0), 1);
        Assert.Equal(90, LRoute.HeadingAt(LRoute.Length), 1);
        Assert.Equal(1 / 45.0, LRoute.CurvatureAt(LRoute.Length / 2), 2);
    }

    [Fact]
    public void Smooth_LimitsHowFarTheArcLeavesThePaintedLine()
    {
        var tight = Polyline.Smooth([new LocalPoint(0, 0), new LocalPoint(0, 200), new LocalPoint(200, 200)], 45, maxDeviationM: 6);
        var mid = tight.PointAt(tight.Length / 2);

        Assert.Equal(6, LocalPoint.Distance(mid, new LocalPoint(0, 200)), 0); // nunca más de 6 m de la esquina
        Assert.Equal(90, tight.HeadingAt(tight.Length), 1);
    }

    [Fact]
    public void PathFollower_StaysOnTheLine_SlowsForTheCurve_TurnsSmoothly_AndStopsAtTheEnd()
    {
        var f = PathFollower.StartOn(LRoute, reverse: false);
        var limits = AircraftPerformance.MediumJet.Ground;
        var minSpeedInCurve = double.MaxValue;
        var maxHeadingStep = 0.0;

        for (var i = 0; i < 3000 && !f.Arrived(LRoute); i++)
        {
            var before = f.HeadingDeg;
            f.Step(LRoute, SimClock.TickSeconds, limits, 20, stopAtEnd: true);
            maxHeadingStep = Math.Max(maxHeadingStep, Math.Abs(GeoMath.AngleDiffDeg(before, f.HeadingDeg)));
            if (LRoute.CurvatureAt(f.Progress) > 0.01)
                minSpeedInCurve = Math.Min(minSpeedInCurve, f.SpeedKt);
        }

        Assert.True(f.Arrived(LRoute));
        Assert.True(LocalPoint.Distance(f.Position, LRoute.End) < 0.5);
        // En una curva de 45 m con 0,6 m/s² laterales: ≈ 10 kt.
        Assert.InRange(minSpeedInCurve, 7, 11);
        Assert.True(maxHeadingStep < 1.5, $"giro de {maxHeadingStep:F2}° en un tick");
        Assert.Equal(90, f.HeadingDeg, 0);
    }

    [Fact]
    public void PathFollower_Reverse_MovesTailFirst()
    {
        var straight = new Polyline([new LocalPoint(0, 0), new LocalPoint(0, -60)]); // hacia el sur
        var f = PathFollower.StartOn(straight, reverse: true);

        for (var i = 0; i < 2000 && !f.Arrived(straight); i++)
            f.Step(straight, SimClock.TickSeconds, AircraftPerformance.MediumJet.Ground, 3, stopAtEnd: true);

        Assert.True(f.Arrived(straight));
        Assert.Equal(0, f.HeadingDeg, 0); // se movió al sur mirando al norte
    }
}