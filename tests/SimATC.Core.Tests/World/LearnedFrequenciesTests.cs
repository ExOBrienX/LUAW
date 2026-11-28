using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.World;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.World;

/// <summary>ADR-173: EGLL no trae Aproximación ni Salidas en el simulador; se completan con VATSIM o la tabla pública.</summary>
public class LearnedFrequenciesTests
{
    // TEST DATA: un aeropuerto con solo Torre y Ground, como EGLL en MSFS 2024.
    private static AirportData OnlyTower() => SimAirports.Load("SCEL.json") with
    {
        Icao = "EGLL",
        Frequencies = [new Frequency(FrequencyType.Tower, 118_505, "HEATHROW"), new Frequency(FrequencyType.Ground, 121_705, "HEATHROW")],
    };

    [Fact]
    public void The_public_table_fills_the_approach_the_simulator_does_not_have()
    {
        var pub = new PublicFrequencies();
        pub.AddLine("EGLL,Approach,119725,Heathrow Director"); // TEST DATA
        var stations = new AtcStations(LearnedFrequencies.Augment(OnlyTower(), null, pub), null);
        var dep = stations.For(AtcRole.Departure);
        Assert.NotNull(dep);
        Assert.Equal(119_725, dep.Khz);
        Assert.Equal("Heathrow Approach", dep.Callsign);
    }

    [Fact]
    public void Vatsim_goes_before_the_public_table_and_the_simulator_before_both()
    {
        var vatsim = new VatsimCenterStore();
        Assert.True(vatsim.Add("EGLL_N_APP", "119.730", 5)); // TEST DATA
        Assert.True(vatsim.Add("EGLL_N_APP", "119.730", 5));
        Assert.True(vatsim.Add("EGLL_S_APP", "134.980", 5));
        Assert.False(vatsim.Add("EGLL_TWR", "118.500", 4)); // la Torre no se guarda
        var pub = new PublicFrequencies();
        pub.AddLine("EGLL,Approach,119725,Heathrow Director");
        var learned = LearnedFrequencies.Augment(OnlyTower(), vatsim, pub);
        Assert.Equal(119_730, learned.Frequencies.Single(f => f.Type == FrequencyType.Approach).Khz);
        // Con Aproximación en el simulador, no se agrega nada.
        var withApp = OnlyTower() with { Frequencies = [.. OnlyTower().Frequencies, new Frequency(FrequencyType.Approach, 120_400, "HEATHROW")] };
        Assert.Same(withApp, LearnedFrequencies.Augment(withApp, vatsim, pub));
    }

    [Fact]
    public void An_approach_callsign_is_not_taken_as_a_center_sector()
    {
        var vatsim = new VatsimCenterStore();
        vatsim.Add("LEMD_APP", "127.500", 5); // TEST DATA: el prefijo de VATSpy de LECM es "LEMD"
        Assert.Null(vatsim.KhzFor(new Fir("LECM", "Madrid", "LEMD", "Control")));
        Assert.Equal(127_500, vatsim.KhzForAirport("LEMD", "APP"));
    }
}
