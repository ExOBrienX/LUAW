using SimATC.Core.Atc;

namespace SimATC.Core.Tests.Atc;

/// <summary>METAR reales de aviationweather.gov del 2026-11-09 (ADR-100). TEST DATA.</summary>
public class MetarTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 2, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Scel_cavok_and_qnh()
    {
        var m = MetarParser.Parse("METAR SCEL 090200Z 15007KT CAVOK 20/09 Q1012 NOSIG", Now)!;
        Assert.Equal("SCEL", m.Icao);
        Assert.Equal(new DateTime(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc), m.TimeUtc);
        Assert.Equal(150, m.WindFromTrueDeg);
        Assert.Equal(7, m.WindKt);
        Assert.Null(m.GustKt);
        Assert.Equal(1012, m.QnhHpa);
        Assert.Equal(10_000, m.VisibilityM);
        Assert.Equal(20, m.TemperatureC);
    }

    [Fact]
    public void Kjfk_inches_and_statute_miles()
    {
        var m = MetarParser.Parse("METAR KJFK 090151Z 25013KT 10SM SCT250 13/03 A2980 RMK AO2 SLP091 T01280033 $", Now)!;
        Assert.Equal(250, m.WindFromTrueDeg);
        Assert.Equal(13, m.WindKt);
        Assert.Equal(1009.1, m.QnhHpa!.Value, 1);
        Assert.Equal(16_093, m.VisibilityM!.Value, 0);
    }

    [Fact]
    public void Egll_fog_ignores_tempo()
    {
        var m = MetarParser.Parse("METAR EGLL 090150Z 04002KT 0400 R27R/0225N FG FEW002 SCT005 04/04 Q1020 TEMPO 0100 VV///", Now)!;
        Assert.Equal(40, m.WindFromTrueDeg);
        Assert.Equal(400, m.VisibilityM);
        Assert.Equal(1020, m.QnhHpa);
    }

    [Theory]
    [InlineData("METAR LEMD 090200Z VRB02KT CAVOK 08/02 Q1025", null, 2.0, null)]
    [InlineData("METAR EDDF 090150Z 27015G28KT 9999 -RA BKN020 10/08 Q1003", 270.0, 15.0, 28.0)]
    [InlineData("METAR UUEE 090200Z 18005MPS 9999 OVC010 M02/M04 Q1018", 180.0, 9.7, null)]
    [InlineData("METAR SCIE 090200Z 00000KT 9999 SKC 12/10 Q1015", null, 0.0, null)]
    public void Wind_forms(string raw, double? dir, double kt, double? gust)
    {
        var m = MetarParser.Parse(raw, Now)!;
        Assert.Equal(dir, m.WindFromTrueDeg);
        Assert.Equal(kt, m.WindKt, 1);
        Assert.Equal(gust, m.GustKt);
    }

    [Fact]
    public void Rain_and_negative_temperature()
    {
        Assert.Equal(Precipitation.Rain, MetarParser.Parse("METAR EDDF 090150Z 27015KT 9999 -RA BKN020 10/08 Q1003", Now)!.Precipitation);
        Assert.Equal(Precipitation.Snow, MetarParser.Parse("METAR UUEE 090200Z 18005MPS 2000 SN OVC010 M02/M04 Q1018", Now)!.Precipitation);
        Assert.Equal(-2, MetarParser.Parse("METAR UUEE 090200Z 18005MPS 9999 OVC010 M02/M04 Q1018", Now)!.TemperatureC);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No METAR found")]
    [InlineData("METAR SCEL 090200Z")]
    public void Garbage_is_null(string raw) => Assert.Null(MetarParser.Parse(raw, Now));

    /// <summary>Un METAR del día 30 leído el 1 del mes siguiente es del mes anterior.</summary>
    [Fact]
    public void Day_of_the_previous_month()
    {
        var m = MetarParser.Parse("METAR SCEL 302350Z 18010KT 9999 Q1012", new DateTime(2026, 12, 1, 0, 10, 0, DateTimeKind.Utc))!;
        Assert.Equal(new DateTime(2026, 11, 30, 23, 50, 0, DateTimeKind.Utc), m.TimeUtc);
    }

    [Fact]
    public void Observation_from_metar_gives_the_wind_for_the_runway()
    {
        var m = MetarParser.Parse("METAR SCEL 090200Z 15007KT CAVOK 20/09 Q1012 NOSIG", Now)!;
        var o = WeatherObservation.FromMetar(m, 0.5);
        Assert.Equal(150, o.WindFromTrueDeg);
        Assert.Equal(7, o.WindKt);
        Assert.Equal(1012, o.QnhHpa);
        Assert.Equal(149.5, o.WindFromMagneticDeg, 1);
    }
}

public class MetarClientTests
{
    [Fact]
    public void Url_has_only_the_icao_codes() =>
        Assert.Equal("https://aviationweather.gov/api/data/metar?ids=SCEL%2CKJFK&format=raw", MetarClient.UrlFor(["scel", "KJFK", "SCEL"]).AbsoluteUri);

    /// <summary>La respuesta real (sin salto de línea al final en la última), con un aeropuerto repetido: el más nuevo.</summary>
    [Fact]
    public void Parses_every_line_and_keeps_the_newest()
    {
        var body = "METAR EGLL 090150Z 04002KT 0400 FG 04/04 Q1020\nMETAR SCEL 090100Z 18010KT CAVOK 21/09 Q1012\nMETAR SCEL 090200Z 15007KT CAVOK 20/09 Q1012 NOSIG";
        var all = MetarClient.ParseAll(body, new DateTime(2026, 11, 9, 2, 30, 0, DateTimeKind.Utc));
        Assert.Equal(2, all.Count);
        Assert.Equal(150, all["scel"].WindFromTrueDeg);
    }
}

/// <summary>ADR-101: el METAR del OFP contra el actual, como lo ve el piloto en el briefing.</summary>
public class MetarComparisonTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 2, 30, 0, DateTimeKind.Utc);
    private static Metar M(string raw) => MetarParser.Parse(raw, Now)!;

    [Fact]
    public void Same_weather_has_no_differences() =>
        Assert.Empty(MetarComparison.Differences(M("METAR SCIE 082300Z 02010KT 9999 Q1015"), M("METAR SCIE 090200Z 03012KT 9999 Q1016")));

    [Fact]
    public void Wind_shift_gust_qnh_and_rain_are_reported()
    {
        var d = MetarComparison.Differences(M("METAR SCIE 082300Z 02010KT 9999 Q1015"), M("METAR SCIE 090200Z 20018G30KT 4000 RA Q1009"));
        Assert.Contains(d, x => x.Contains("rotó de 020° a 200°"));
        Assert.Contains(d, x => x.Contains("ráfagas de 30 kt"));
        Assert.Contains(d, x => x.Contains("QNH de 1015 a 1009"));
        Assert.Contains(d, x => x.Contains("visibilidad"));
        Assert.Contains(d, x => x == "ahora llueve");
    }

    [Fact]
    public void Calm_to_wind_is_reported_without_a_direction_change() =>
        Assert.Equal(["viento de calma a 180° 12 kt"],
            MetarComparison.Differences(M("METAR SCIE 082300Z 00000KT 9999 Q1015"), M("METAR SCIE 090200Z 18012KT 9999 Q1015")));
}
