using SimATC.Core.Airports;
using SimATC.Core.Atc;

namespace SimATC.Core.Tests.Atc;

/// <summary>Dependencias y frecuencias (RF-22), squawk y formatos de radio.</summary>
public class AtcStationsTests
{
    private static AirportData Load(string icao) =>
        SimAirports.Load($"{icao}.json");

    private static AirportAtcData ScelAtc() =>
        AirportAtcData.Load(AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL"))!;

    [Fact]
    public void With_the_atc_file_uses_the_aip_stations()
    {
        var s = new AtcStations(Load("SCEL"), ScelAtc());
        Assert.Equal(new AtcStation(AtcRole.Delivery, "Santiago Delivery", 136700), s.For(AtcRole.Delivery));
        Assert.Equal("Santiago Ground", s.For(AtcRole.Ground)!.Callsign);
        Assert.Equal(118100, s.For(AtcRole.Tower)!.Khz);
        Assert.Equal(new AtcStation(AtcRole.Approach, "Santiago Approach", 119700), s.For(AtcRole.Departure)); // sin Departure: Approach
        Assert.Equal("Santiago Tower", s.OnFrequency(118100)!.Callsign);
        Assert.DoesNotContain(s.All, x => x.Khz == 135400); // sin verificar: nunca
    }

    [Fact]
    public void Without_the_file_uses_the_sim_frequencies_and_rf22()
    {
        // SCIE en el simulador: solo Tower 118.6 y Ground 121.9 ("CONCEPCION").
        var s = new AtcStations(Load("SCIE"), null);
        Assert.Equal(new AtcStation(AtcRole.Ground, "Concepcion Ground", 121900), s.For(AtcRole.Ground));
        Assert.Equal(AtcRole.Ground, s.For(AtcRole.Delivery)!.Role); // Delivery lo cubre Ground
        Assert.Equal("Concepcion Tower", s.For(AtcRole.Tower)!.Callsign);
        Assert.Null(s.For(AtcRole.Approach));
    }

    [Theory]
    [InlineData(118100, "118.1")]
    [InlineData(118350, "118.35")]
    [InlineData(121900, "121.9")]
    [InlineData(136700, "136.7")]
    public void Frequency_text(int khz, string expected) => Assert.Equal(expected, RadioFormat.Frequency(khz));

    [Theory]
    [InlineData(10000, "10000 feet")] // en la TA misma: altitud
    [InlineData(13000, "flight level 130")]
    [InlineData(5000, "5000 feet")]
    public void Level_text_with_ta_10000(double ft, string expected) => Assert.Equal(expected, RadioFormat.Level(ft, 10000));

    [Fact]
    public void Squawks_are_valid_unique_and_never_special()
    {
        var g = new SquawkGenerator(new Random(7));
        var codes = Enumerable.Range(0, 500).Select(_ => g.Next()).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c =>
        {
            Assert.True(SquawkGenerator.IsValid(c), c.ToString());
            Assert.DoesNotContain(c, new[] { 7500, 7600, 7700, 7000, 2000, 1200, 0 });
        });
    }
}
