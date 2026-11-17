using SimATC.Core.Airports;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Búsqueda de la SID/STAR del simulador que corresponde a la del plan, y su trazado (datos reales de SCEL).</summary>
public class ProcedureMatchTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    [Theory]
    [InlineData("GELU5B", "17L", "GELU5B", 100)] // igual
    [InlineData("GELUS5B", "17L", "GELU5B", 90)] // nombre largo del punto (SimBrief) frente al de 6 caracteres
    [InlineData("GELU4B", "17L", "GELU5B", 80)]  // otra revisión (AIRAC distinto)
    [InlineData("GELU5B", "35L", "GELU5B", 75)]  // el nombre manda aunque no sea para esa pista
    public void Finds_the_sim_procedure_for_the_planned_one(string planned, string runway, string expected, int minScore)
    {
        var ranked = ProcedureMatch.Rank(Scel.Value.Departures, planned, runway);
        var best = ProcedureMatch.Best(ranked);

        Assert.NotNull(best);
        Assert.Equal(expected, best.Name);
        Assert.True(best.Score >= minScore, $"{best.Score} ({best.Reason})");
    }

    [Fact]
    public void Unknown_procedure_gives_no_automatic_choice()
    {
        Assert.Null(ProcedureMatch.Best(ProcedureMatch.Rank(Scel.Value.Departures, "ZZZZ1A", "17L")));
        Assert.Empty(ProcedureMatch.Rank(Scel.Value.Departures, null, "17L"));
    }

    [Fact]
    public void Prefers_the_one_that_serves_the_planned_runway()
    {
        var ranked = ProcedureMatch.Rank(Scel.Value.Departures, "GELU2X", "35L");
        Assert.Equal("GELU3C", ranked[0].Name); // la única GELU para la 35L
    }

    [Theory]
    [InlineData("GELU5B", "GELU", "5", "B")]
    [InlineData("SABLA8", "SABLA", "8", "")]
    [InlineData("AMB5", "AMB", "5", "")]
    public void Splits_names(string name, string baseName, string number, string suffix)
    {
        Assert.Equal((baseName, number, suffix), ProcedureMatch.Split(name));
    }

    [Fact]
    public void Sid_path_has_only_fixes_with_coordinates()
    {
        var sid = Scel.Value.Departures.First(p => p.Name == "GELU5B");
        var path = ProcedureMatch.SidPath(sid, "17L", null);

        Assert.NotEmpty(path);
        Assert.All(path, p => Assert.False(p.Position.LatDeg == 0 && p.Position.LonDeg == 0));
        for (var i = 1; i < path.Count; i++)
            Assert.NotEqual(path[i - 1].Ident, path[i].Ident);
    }

    [Fact]
    public void Approach_names_and_paths()
    {
        var ils = Scel.Value.Approaches.First(a => a.Type == ApproachType.Ils && a.Suffix == "Z");
        Assert.Equal("ILS Z 17L", ProcedureMatch.ApproachName(ils));
        var path = ProcedureMatch.ApproachPath(ils, ils.Transitions[0].Name);
        Assert.Contains(path, p => p.Ident == ils.Transitions[0].Name);
        Assert.True(path.Count > ProcedureMatch.ApproachPath(ils, null).Count);
    }
}
