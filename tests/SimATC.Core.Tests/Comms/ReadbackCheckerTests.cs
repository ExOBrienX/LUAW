using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Colaciones y tabla de fallbacks (§8.4, §8.7, RF-33, RF-33b).</summary>
public class ReadbackCheckerTests
{
    private static readonly string[] Cs = ["lan205", "lan 205"]; // TEST DATA

    // TEST DATA: autorización "cleared to SCIE via GELU5B, runway 17R, climb FL100, squawk 4721".
    private static readonly ExpectedReadback Clearance = new("LAN205")
    {
        Destination = "SCIE", Sid = "GELU5B", Runway = "17R", LevelFt = 10000, Squawk = 4721,
    };

    private static ReadbackResult Check(string text, ReadbackStrictness s = ReadbackStrictness.Realistic) =>
        ReadbackChecker.Check(MessageParser.Parse(text, Cs), Clearance, s);

    [Theory]
    [InlineData("Cleared to SCIE via GELU5B departure, runway 17R, climb flight level 100, squawk 4721, LAN205")]
    [InlineData("cleared scie gelu5b runway one seven right FL100 squawk fower seven two one lan205")] // abreviada, con todo
    [InlineData("Cleared SCIE, GELUS5B departure, runway 17R, climb FL100, squawk 4721, LAN205")] // nombre largo de la SID
    [InlineData("eh cleared to SCIE via GELU5B uh runway 17R, climb flight level 100 squawk 4721, thanks a lot, LAN205")] // enredada
    public void Correct_readbacks(string text) => Assert.Equal(ReadbackVerdict.Correct, Check(text).Verdict);

    /// <summary>STAR sin número (CANDYA de RJBB, prueba de Marco 2026-11-09): "colacione salida" en bucle.</summary>
    [Theory]
    [InlineData("Autorizado llegada CANDYA, desciendo vía STAR a 10000 pies, LAN878", ReadbackVerdict.Correct)]
    [InlineData("Cleared CANDYA arrival, descending via STAR to 10000 feet, LAN878", ReadbackVerdict.Correct)]
    [InlineData("Desciendo vía STAR a 10000 pies, LAN878", ReadbackVerdict.Missing)]
    [InlineData("Autorizado llegada CANDYAB, desciendo a 10000 pies, LAN878", ReadbackVerdict.Missing)]
    public void Star_without_number_is_accepted_when_said(string text, ReadbackVerdict verdict)
    {
        var e = new ExpectedReadback("LAN878") { Sid = "CANDYA", LevelFt = 10000 }; // TEST DATA
        var r = ReadbackChecker.Check(MessageParser.Parse(text, ["lan878", "lan 878"]), e, ReadbackStrictness.Realistic);
        Assert.Equal(verdict, r.Verdict);
    }

    [Fact]
    public void Wrong_value_is_corrected_before_anything_missing()
    {
        var r = Check("Cleared to SCIE via GELU5B, runway 17R, climb flight level 100, squawk 4712, LAN205");
        Assert.Equal(new ReadbackResult(ReadbackVerdict.Wrong, ReadbackElement.Squawk), r);
        Assert.Equal(ReadbackElement.Runway, Check("cleared SCIE GELU5B runway 17L FL100 squawk 4721 LAN205").Element);
        Assert.Equal(ReadbackElement.Level, Check("cleared SCIE GELU5B runway 17R climb 6000 squawk 4721 LAN205").Element);
    }

    [Fact]
    public void Missing_element_asks_only_for_that_element()
    {
        var r = Check("Cleared to SCIE via GELU5B departure, runway 17R, climb flight level 100, LAN205");
        Assert.Equal(new ReadbackResult(ReadbackVerdict.Missing, ReadbackElement.Squawk), r);
        Assert.Equal(ReadbackElement.Callsign, Check("Cleared to SCIE via GELU5B, runway 17R, climb FL100, squawk 4721").Element);
    }

    [Fact]
    public void Permissive_only_requires_runway_squawk_and_frequency()
    {
        var p = ReadbackStrictness.Permissive;
        Assert.Equal(ReadbackVerdict.Correct, Check("runway 17R squawk 4721", p).Verdict);
        Assert.Equal(ReadbackElement.Squawk, Check("runway 17R, wilco", p).Element);
        // Aun en permisivo, un valor equivocado se corrige.
        Assert.Equal(ReadbackVerdict.Wrong, Check("runway 17R squawk 4721 FL120", p).Verdict);
    }

    [Theory]
    [InlineData("say again", ReadbackVerdict.PilotSaysAgain)]
    [InlineData("unable, LAN205", ReadbackVerdict.Unable)]
    [InlineData("LAN205 request taxi", ReadbackVerdict.OtherRequest)]
    [InlineData("banana potato", ReadbackVerdict.NotUnderstood)]
    public void Fallback_table(string text, ReadbackVerdict expected) => Assert.Equal(expected, Check(text).Verdict);

    [Theory]
    [InlineData("GELUS5B", "GELU5B", true)]
    [InlineData("gelu5b", "GELU5B", true)]
    [InlineData("GELU6A", "GELU5B", false)]
    [InlineData("ALBA7C", "ALBA7A", false)]
    public void Same_procedure(string a, string b, bool expected) => Assert.Equal(expected, ReadbackChecker.SameProcedure(a, b));
}
