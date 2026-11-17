using SimATC.Core.Atc;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Atc;

/// <summary>Clima en el aeropuerto, viento promediado y ATIS (RF-24, §8.7b).</summary>
public class AtisTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly RunwayInUse Rwy17 = new(["17L", "17R"], 177.4, "test");

    // TEST DATA: valores como los que leyó el Probe en SCEL (viento 170° verdaderos, QNH 1014, 21 °C, MAGVAR 0,51).
    private static WeatherObservation Wx(double windTrue = 170, double kt = 12.1, double? gust = null, double qnh = 1014.0,
        Precipitation p = Precipitation.None, DateTime? t = null) =>
        new("SCEL", t ?? T0, windTrue, kt, gust, 22110, 21.0, qnh, p, 0.51);

    [Fact]
    public void Departure_atis_follows_the_template_and_omits_unknown_elements()
    {
        var atis = new AtisBuilder("Santiago", AtisKind.Departure);
        atis.Update(Wx(), Rwy17, ["17R"], 11000);

        Assert.Equal("Santiago departure information Alfa, time 1500, departure runway 17R, transition level 110, " +
                     "wind 170 degrees 12 knots, temperature 21, QNH 1014, acknowledge receipt of information Alfa on initial contact",
            atis.Current!.Text);
        Assert.DoesNotContain("visibility", atis.Current.Text); // la del simulador es "de partículas": no se usa
    }

    /// <summary>ADR-102: combinado con llegadas y salidas en pistas distintas, las dice por separado; un cambio de cualquiera avanza la letra.</summary>
    [Fact]
    public void Combined_atis_splits_landing_and_departure_runways()
    {
        var atis = new AtisBuilder("Santiago", AtisKind.Combined);
        atis.Update(Wx(), Rwy17, ["17R"], 11000, ["17L"]);
        Assert.Contains("landing runway 17L, departure runway 17R", atis.Current!.Text);
        Assert.Equal("↓17L ↑17R", atis.Current.RunwayLabel);
        Assert.True(atis.Update(Wx(t: T0.AddMinutes(5)), Rwy17, ["17R"], 11000, ["17L", "17R"])); // se abre la 17R para llegadas
        Assert.Equal('B', atis.Current!.Letter);
        Assert.True(atis.Update(Wx(t: T0.AddMinutes(10)), Rwy17, ["17L", "17R"], 11000, ["17L", "17R"]));
        Assert.Contains("runway in use 17L and 17R", atis.Current!.Text);
    }

    [Fact]
    public void Wind_is_magnetic_rounded_with_gusts_and_calm_below_one_knot()
    {
        Assert.Equal("wind 360 degrees 8 knots", AtisBuilder.Wind(Wx(windTrue: 4, kt: 8))); // 4° verdaderos - 0,51 → 360
        Assert.Equal("wind 170 degrees 18 gusting 28 knots", AtisBuilder.Wind(Wx(kt: 18, gust: 28)));
        Assert.Equal("wind calm", AtisBuilder.Wind(Wx(kt: 0.5)));
        Assert.Equal(169.49, Wx().WindFromMagneticDeg, 2); // MAGVAR positivo = Este: magnético = verdadero − MAGVAR
    }

    [Fact]
    public void Letter_changes_with_significant_changes_transition_level_or_after_an_hour()
    {
        var atis = new AtisBuilder("Santiago", AtisKind.Departure);
        Assert.True(atis.Update(Wx(), Rwy17, ["17R"], 11000));
        Assert.False(atis.Update(Wx(windTrue: 180, kt: 14, qnh: 1014.6, t: T0.AddMinutes(10)), Rwy17, ["17R"], 11000)); // menor
        Assert.Equal('A', atis.Current!.Letter);
        Assert.True(atis.Update(Wx(qnh: 1013.2, t: T0.AddMinutes(20)), Rwy17, ["17R"], 11500)); // QNH y nivel de transición
        Assert.Equal('B', atis.Current!.Letter);
        Assert.Contains("transition level 115", atis.Current.Text);
        Assert.True(atis.Update(Wx(qnh: 1013.2, p: Precipitation.Rain, t: T0.AddMinutes(25)), Rwy17, ["17R"], 11500));
        Assert.Contains(", rain,", atis.Current!.Text);
        Assert.True(atis.Update(Wx(qnh: 1013.2, kt: 12, gust: 25, p: Precipitation.Rain, t: T0.AddMinutes(26)), Rwy17, ["17R"], 11500));
        Assert.True(atis.Update(Wx(qnh: 1013.2, kt: 12, gust: 25, p: Precipitation.Rain, t: T0.AddMinutes(90)), Rwy17, ["17R"], 11500));
        Assert.Equal('E', atis.Current!.Letter); // A → B (QNH) → C (lluvia) → D (ráfaga) → E (una hora)
    }

    [Fact]
    public void Wind_average_is_a_two_minute_vector_mean_and_ignores_older_samples()
    {
        var w = new WindAverager();
        // Hace más de 2 min: viento norte fuerte (no cuenta para la media).
        for (var t = 0; t < 60; t++)
            w.Add(t, 0, 30);
        // Últimos 2 min: sur 18 kt con pequeñas oscilaciones de dirección y velocidad.
        for (var t = 200; t < 320; t++)
            w.Add(t, 177 + (t % 2 == 0 ? 5 : -5), t % 3 == 0 ? 10 : 22);
        var avg = w.Current(320)!.Value;
        Assert.Equal(177, avg.FromTrueDeg, 0);
        Assert.InRange(avg.MeanKt, 17, 19);
        Assert.InRange(avg.Samples, 119, 121);
    }

    [Fact]
    public void Gust_is_reported_only_when_the_ten_minute_peak_exceeds_the_mean_by_ten_knots()
    {
        var w = new WindAverager();
        for (var t = 0; t < 120; t++)
            w.Add(t, 180, 15);
        Assert.Null(w.Current(120)!.Value.GustKt);
        w.Add(121, 180, 40); // un pico aislado de 1 s (turbulencia): su media de 3 s no llega a 25
        Assert.Null(w.Current(121)!.Value.GustKt);
        for (var t = 122; t < 125; t++)
            w.Add(t, 180, 15);
        for (var t = 125; t < 128; t++)
            w.Add(t, 180, 27); // 27 kt sostenidos 3 s: ráfaga
        Assert.Equal(27, w.Current(127)!.Value.GustKt!.Value, 0);
        // A los 10 min de la ráfaga ya no se reporta.
        for (var t = 128; t < 760; t++)
            w.Add(t, 180, 15);
        Assert.Null(w.Current(760)!.Value.GustKt);
    }

    [Fact]
    public void Observation_only_when_the_pilot_is_at_the_airport()
    {
        var scel = new GeoPoint(-33.3944, -70.7938);
        var here = new AmbientWeather(new GeoPoint(-33.3974, -70.7978), true, 0, 170, 12, 22110, 21, 1014, Precipitation.None, 0.51);
        var wind = new AveragedWind(170, 12, null, 60);
        Assert.NotNull(WeatherObservation.AtAirport("SCEL", scel, here, wind, T0));
        var far = here with { Position = GeoMath.Destination(scel, 0, 50_000), OnGround = false, AglFt = 10_000 };
        Assert.Null(WeatherObservation.AtAirport("SCEL", scel, far, wind, T0));
        // En final sobre el aeropuerto (EBBR 2026-11-14: a ~1000 ft el viento del simulador era 190/21 con METAR 110/05 y
        // cambió la pista mientras el piloto aterrizaba): el viento en altura no es el de superficie.
        var onFinal = here with { OnGround = false, AglFt = 1000 };
        Assert.False(WeatherObservation.IsAtAirport(scel, onFinal));
        Assert.True(WeatherObservation.IsAtAirport(scel, onFinal with { AglFt = 60 }));
        Assert.Equal(Precipitation.Rain, AmbientWeather.FromMask(4));
        Assert.Equal(Precipitation.None, AmbientWeather.FromMask(2));
    }
}
