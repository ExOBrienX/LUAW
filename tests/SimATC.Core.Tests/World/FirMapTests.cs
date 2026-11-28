using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.Core.Tests.World;

/// <summary>FIR de VATSpy (ADR-071) y frecuencias de Centro vistas en VATSIM.</summary>
public class FirMapTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly Lazy<FirMap> Map = new(() => FirMap.Load(FirMap.DatPathFor(DataDir), FirMap.BoundariesPathFor(DataDir)));

    [Theory]
    [InlineData(-33.393, -70.786, "SCEZ", "Santiago")]      // SCEL
    [InlineData(-36.773, -73.063, "SCEZ", "Santiago")]      // SCIE (Concepción): misma FIR
    [InlineData(-23.444, -70.445, "SCFZ", "Antofagasta")]   // SCFA
    [InlineData(-41.439, -73.094, "SCTZ", "Puerto Montt")]  // SCTE
    [InlineData(-53.003, -70.855, "SCCZ", "Punta Arenas")]  // SCCI
    public void Finds_the_fir_of_a_point(double lat, double lon, string icao, string name)
    {
        var fir = Map.Value.At(new GeoPoint(lat, lon));
        Assert.NotNull(fir);
        Assert.Equal(icao, fir.Icao);
        Assert.Equal(name, fir.Name);
    }

    [Fact]
    public void Inside_a_tma_the_most_specific_area_comes_first_and_then_its_fir()
    {
        var list = new List<Fir>();
        Map.Value.AllAt(new GeoPoint(-34.822, -58.536), list); // SAEZ (Ezeiza)
        Assert.Equal("BAIRES", list[0].Icao);
        Assert.Contains(list, f => f.Icao == "SAEF");
        Assert.True(list.FindIndex(f => f.Icao == "SAEF") > 0);
    }

    [Fact]
    public void The_callsign_uses_the_country_suffix_from_vatspy_and_control_in_spanish()
    {
        var fir = Map.Value.At(new GeoPoint(-33.393, -70.786))!;
        Assert.Equal("Santiago Radar", fir.Callsign(AtcLanguage.English));
        Assert.Equal("Santiago Control", fir.Callsign(AtcLanguage.Spanish));
    }

    [Theory]
    [InlineData("London TMA South (Up to FL195) - London", "London")]
    [InlineData("Langen Radar (Central+South combined) - Langen", "Langen")]
    [InlineData("Kano (West)", "Kano")]
    [InlineData("Santiago", "Santiago")]
    public void The_radio_name_drops_the_vatspy_sector_description(string vatspyName, string radio)
    {
        Assert.Equal(radio, Fir.RadioName(vatspyName));
        Assert.Equal($"{radio} Control", new Fir("XXXX", vatspyName, "", "Control").Callsign(AtcLanguage.English));
    }

    [Fact]
    public void Over_heathrow_the_center_is_called_london_control()
    {
        var fir = Map.Value.At(new GeoPoint(51.4775, -0.461389))!;
        Assert.StartsWith("London ", fir.Callsign(AtcLanguage.English));
        Assert.DoesNotContain("(", fir.Callsign(AtcLanguage.English));
    }

    [Theory]
    [InlineData(-33.393, -70.786, "Santiago Radar", 126_300)]   // SCEL: al sur de 33°23'08"
    [InlineData(-36.773, -73.063, "Santiago Radar", 126_300)]   // SCIE (Concepción)
    [InlineData(-29.916, -71.200, "Santiago Radar", 129_100)]   // SCSE (La Serena): al norte
    [InlineData(-20.535, -70.181, "Iquique Radar", 128_700)]    // SCDA (Iquique): sector norte
    [InlineData(-23.444, -70.445, "Iquique Radar", 128_300)]    // SCFA (Antofagasta): sector sur
    [InlineData(-41.439, -73.094, "Puerto Montt Radar", 120_100)] // SCTE: al norte de 42°45'
    [InlineData(-45.916, -71.689, "Puerto Montt Radar", 128_300)] // SCBA (Balmaceda): al sur
    [InlineData(-53.003, -70.855, "Punta Arenas Centro", 128_100)] // SCCI
    public void Official_centers_from_the_aip_with_their_sectors(double lat, double lon, string callsign, int khz)
    {
        var official = CenterData.Load(CenterData.PathFor(DataDir));
        var p = new GeoPoint(lat, lon);
        var s = official.StationFor(Map.Value.At(p)!.Icao, p);
        Assert.NotNull(s);
        Assert.Equal(callsign, s.Callsign);
        Assert.Equal(khz, s.Khz);
    }

    // ADR-129: VATSpy da "LEMD" como prefijo del Centro de Madrid, pero se conecta como LECM_CTR: se busca por los dos.
    [Fact]
    public void Center_frequency_is_found_by_the_fir_code_when_the_callsign_prefix_differs()
    {
        var fir = Map.Value.At(new GeoPoint(39.5, -3.7))!; // La Mancha: FIR de Madrid
        Assert.StartsWith("LECM", fir.Icao);
        var store = new VatsimCenterStore();
        // TEST DATA: controlador del feed.
        store.Add("LECM_CTR", "132.980", 6);
        Assert.Equal(132_980, store.KhzFor(fir));
    }

    [Fact]
    public void Center_frequency_is_the_most_seen_one_and_falls_back_to_sectors()
    {
        var fir = Map.Value.At(new GeoPoint(-33.393, -70.786))!;
        var store = new VatsimCenterStore();
        Assert.Null(store.KhzFor(fir));
        // TEST DATA: controladores del feed.
        Assert.True(store.Add("SCEZ_N_CTR", "125.900", 6));
        Assert.Equal(125_900, store.KhzFor(fir));
        store.Add("SCEZ_CTR", "128.300", 6);
        store.Add("SCEZ_CTR", "128.300", 6);
        store.Add("SCEZ_CTR", "120.000", 6);
        Assert.Equal(128_300, store.KhzFor(fir));
        // ADR-173: la Aproximación también se guarda, pero no cuenta como Centro.
        Assert.True(store.Add("SCEL_APP", "119.700", 5));
        Assert.Equal(128_300, store.KhzFor(fir));
        Assert.Equal(119_700, store.KhzForAirport("SCEL", "APP"));
        Assert.False(store.Add("SCEZ_CTR", "199.998", 6));
    }
}
