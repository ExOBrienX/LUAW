using SimATC.Core.Airports;
using SimATC.Core.Atc;

namespace SimATC.Core.Tests.Atc;

/// <summary>Datos ATC por aeropuerto (data/atc/SCEL.json, revisado por Marco contra el AIP) y su uso.</summary>
public class AirportAtcDataTests
{
    private static string ScelPath => AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL");
    private static readonly Lazy<AirportAtcData> Scel = new(() => AirportAtcData.Load(ScelPath)!);
    private static readonly Lazy<AirportData> ScelAirport = new(() =>
        SimAirports.Load("SCEL.json"));

    [Fact]
    public void Scel_file_loads_with_the_reviewed_aip_data()
    {
        var d = Scel.Value;

        Assert.Equal("SCEL", d.Airport);
        Assert.False(d.Reviewed); // lo cambia Marco
        Assert.Equal(10000, d.TransitionAltitudeFt);
        Assert.Equal(3, d.AdditionalSources.Count);
        Assert.NotEmpty(d.Pending);
        Assert.Equal(["17L", "17R"], d.PreferredRunways);
        Assert.Equal(10, d.PreferredMaxTailwindKt);
        Assert.True(d.ModeFor(["17R", "17L"])!.Verified);
        Assert.False(d.ModeFor(["35L", "35R"])!.Verified);
        Assert.Equal(["35L"], d.ModeFor(["35L", "35R"])!.Departure!);
        var app = d.Position(AtcRole.Approach)!;
        Assert.Equal([119700, 129700], app.FrequenciesKhz);
        Assert.Equal([135400], app.UnverifiedFrequenciesKhz); // nunca se usa
        Assert.Equal(AtcRole.Approach, d.DepartureHandoffTo);
        Assert.Null(d.Position(AtcRole.Departure));
        Assert.Contains(d.Airspaces, a => a.Name == "CTR Santiago" && a.UpperFt == 5000);
    }

    // ENR 1.7-4, fila TA 10000 ft: una fila por caso más los bordes.
    [Theory]
    [InlineData(950.0, 13000)]
    [InlineData(970.0, 12500)]
    [InlineData(990.0, 12000)]
    [InlineData(1005.0, 11500)]
    [InlineData(1020.0, 11000)]
    [InlineData(1040.0, 10500)]
    [InlineData(1013.2, 11500)] // borde
    [InlineData(1013.3, 11000)] // borde
    [InlineData(1013.24, 11500)] // se redondea a 1013.2
    [InlineData(1013.25, 11000)] // se redondea a 1013.3
    [InlineData(942.2, 13000)]
    [InlineData(1050.3, 10500)]
    public void Transition_level_follows_the_qnh_table(double qnh, double expectedFt)
    {
        Assert.Equal(expectedFt, Scel.Value.TransitionLevelTable!.LevelFt(qnh, out var outOfRange));
        Assert.False(outOfRange);
    }

    [Theory]
    [InlineData(930.0, 13000)]
    [InlineData(1060.0, 10500)]
    public void Out_of_range_qnh_uses_the_nearest_end_and_says_so(double qnh, double expectedFt)
    {
        Assert.Equal(expectedFt, Scel.Value.TransitionLevelTable!.LevelFt(qnh, out var outOfRange));
        Assert.True(outOfRange);
    }

    [Theory]
    [InlineData("GELU5B", "17L", 10000, "GELUS5B")] // nombre largo en la tabla, corto en el simulador
    [InlineData("ALBA7A", "17R", 13000, "ALBAL7A")]
    [InlineData("NEBE7A", "17L", 13000, "NEBEG7A")]
    [InlineData("GELU3C", "35L", 15000, "GELUS3C")]
    public void Cleared_level_comes_from_the_sid_table(string simName, string runway, double expectedFt, string key)
    {
        var sid = ScelAirport.Value.Departures.First(p => p.Name == simName);
        var level = SidClearance.For(sid, runway, Scel.Value)!;
        Assert.Equal(expectedFt, level.AltitudeFt);
        Assert.Contains(key, level.Source);
    }

    [Fact]
    public void Sid_without_table_level_uses_the_sim_constraints()
    {
        // ANGOD8B tiene clearedLevel null en la tabla; DONT5B no está en la tabla.
        foreach (var name in new[] { "ANGO8B", "DONT5B" })
        {
            var sid = ScelAirport.Value.Departures.First(p => p.Name == name);
            var runway = ProcedureMatchRunway(sid);
            var level = SidClearance.For(sid, runway, Scel.Value);
            Assert.NotNull(level);
            Assert.Equal("restricciones de la SID en el simulador", level.Source);
            Assert.Equal(SidClearance.TopOfSid(sid, runway), level.AltitudeFt);
        }
    }

    [Fact]
    public void Sid_without_any_constraint_gives_null()
    {
        var empty = new Procedure("TEST1A", [], [], []); // TEST DATA
        Assert.Null(SidClearance.For(empty, "17L", Scel.Value));
    }

    [Fact]
    public void Missing_or_broken_file_is_null()
    {
        Assert.Null(AirportAtcData.Load(Path.Combine(Path.GetTempPath(), "no-existe-simatc", "XXXX.json")));
        var bad = Path.Combine(Path.GetTempPath(), $"simatc-atc-{Guid.NewGuid():N}.json");
        File.WriteAllText(bad, "{ no");
        try
        {
            string? logged = null;
            Assert.Null(AirportAtcData.Load(bad, m => logged = m));
            Assert.NotNull(logged);
        }
        finally
        {
            File.Delete(bad);
        }
    }

    [Fact]
    public void Scel_keeps_17_with_up_to_10_kt_of_tailwind_then_uses_the_35_mode()
    {
        var d = Scel.Value;
        var s = new ActiveRunwaySelector(ScelAirport.Value, d.PreferredRunways,
            RunwaySelectionLimits.Default with { PreferredMaxTailwindKt = d.PreferredMaxTailwindKt });

        s.Update(0, 9, 0); // 9 kt del norte: cola en la 17, dentro del límite local
        Assert.Equal("17L/17R", s.Current!.Label);
        s.Update(0, 12, 60); // 12 kt: pasa a la 35
        Assert.Equal("35L/35R", s.Current!.Label);
        Assert.False(d.ModeFor(s.Current.Ends)!.Verified);
    }

    private static string ProcedureMatchRunway(Procedure sid) =>
        RunwayEnd.FormatIdent(sid.RunwayTransitions[0].RunwayNumber, sid.RunwayTransitions[0].RunwayDesignator);
}
