using SimATC.Core.Airports;
using SimATC.Core.Atc;

namespace SimATC.Core.Tests.Atc;

/// <summary>Pista en uso según el viento (RF-23), con los datos reales de SCEL (17L/17R y 35L/35R, rumbo ~177°).</summary>
public class ActiveRunwayTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    [Theory]
    [InlineData(180, 15, "17L/17R")] // viento del sur: despegues al sur
    [InlineData(0, 15, "35L/35R")]   // del norte: al norte
    [InlineData(200, 12, "17L/17R")]
    public void Picks_the_ends_with_headwind_and_uses_parallels_together(double windFrom, double kt, string expected)
    {
        var s = new ActiveRunwaySelector(Scel.Value);
        s.Update(windFrom, kt, 0);
        Assert.Equal(expected, s.Current!.Label);
    }

    /// <summary>Prueba de Marco 2026-11-09: un aeropuerto sin pistas (RJ3KM, un parque) hacía caer la app al llegar el METAR.</summary>
    [Fact]
    public void An_airport_without_runways_has_no_runway_in_use_and_does_not_throw()
    {
        var s = new ActiveRunwaySelector(Scel.Value with { Runways = [] });
        Assert.False(s.Update(180, 15, 0));
        Assert.Null(s.Current);
    }

    /// <summary>Dentro del aeropuerto cargado (en un puesto o en la pista) se sigue en él; a 10 km, no.</summary>
    [Fact]
    public void The_pilot_inside_the_loaded_airport_stays_in_it()
    {
        var a = Scel.Value;
        Assert.True(AirportLocator.Inside(a.Parkings[^1].Position, a));
        Assert.True(AirportLocator.Inside(a.Runways[0].Primary.Position, a));
        Assert.False(AirportLocator.Inside(SimATC.Core.Geo.GeoMath.Destination(a.Reference, 90, 10000), a));
    }

    [Fact]
    public void Components_are_right()
    {
        var w = WindComponents.For(180, 210, 20); // 30° a la derecha
        Assert.Equal(17.3, w.HeadwindKt, 1);
        Assert.Equal(10, w.CrosswindKt, 1);
        Assert.Equal(10, WindComponents.For(180, 0, 10).TailwindKt, 6);
    }

    [Fact]
    public void Keeps_the_runway_while_the_tailwind_is_within_the_limit()
    {
        var s = new ActiveRunwaySelector(Scel.Value);
        s.Update(180, 10, 0); // 17
        Assert.False(s.Update(0, 4, 60)); // 4 kt de cola: se queda
        Assert.Equal("17L/17R", s.Current!.Label);
        Assert.True(s.Update(0, 8, 60)); // 8 kt de cola: cambia
        Assert.Equal("35L/35R", s.Current!.Label);
    }

    [Fact]
    public void Returns_to_the_preferred_runway_only_after_the_wind_stays_favorable()
    {
        var s = new ActiveRunwaySelector(Scel.Value, ["17L", "17R"]);
        s.Update(0, 12, 0); // norte fuerte: 35
        Assert.Equal("35L/35R", s.Current!.Label);
        s.Update(0, 2, 60); // se calma: la 35 sigue dentro de los límites; aún no pasaron 10 min
        Assert.Equal("35L/35R", s.Current!.Label);
        for (var i = 0; i < 10; i++)
            s.Update(0, 2, 60);
        Assert.Equal("17L/17R", s.Current!.Label);
    }

    [Fact]
    public void Calm_wind_at_start_uses_the_preferred_runway_and_forced_wins()
    {
        var s = new ActiveRunwaySelector(Scel.Value, ["17L", "17R"]);
        s.Update(0, 1, 0);
        Assert.Equal("17L/17R", s.Current!.Label);
        s.Forced = "35R";
        s.Update(180, 20, 1);
        Assert.Equal("35L/35R", s.Current!.Label);
        Assert.Equal("elegida a mano", s.Current.Reason);
    }

    /// <summary>
    /// ADR-086 (cualquier aeropuerto): una pista corta cruzada (TEST DATA: 09/27 de 1000 m) no se usa para aviones de línea
    /// aunque tenga más viento de frente.
    /// </summary>
    [Fact]
    public void A_short_crossing_runway_is_not_used_for_airliners()
    {
        var r = Scel.Value.Runways[0];
        var shortRwy = r with
        {
            HeadingTrueDeg = 90, LengthM = 1000,
            Primary = r.Primary with { Number = 9, Designator = RunwayDesignator.None, HeadingTrueDeg = 90 },
            Secondary = r.Secondary with { Number = 27, Designator = RunwayDesignator.None, HeadingTrueDeg = 270 },
        };
        var airport = Scel.Value with { Runways = [.. Scel.Value.Runways, shortRwy] };
        var s = new ActiveRunwaySelector(airport);
        s.Update(90, 20, 0);
        Assert.DoesNotContain("09", s.Current!.Ends);
    }
}
