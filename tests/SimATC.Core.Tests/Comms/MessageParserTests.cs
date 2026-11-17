using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Pasos 2 y 3 de §8.8: valores e intención del mensaje del piloto.</summary>
public class MessageParserTests
{
    private static readonly string[] Callsign = ["lan205", "lan 205"]; // TEST DATA

    private static ParsedMessage P(string text) => MessageParser.Parse(text, Callsign);

    [Fact]
    public void Clearance_request_in_several_forms()
    {
        foreach (var t in new[]
                 {
                     "Santiago Delivery, LAN205, request IFR clearance to SCIE, information Alfa",
                     "delivery lan205 requesting clearance to scie with information a",
                     "LAN 205 request clearance destination SCIE",
                 })
        {
            var m = P(t);
            Assert.True(m.Has(PilotIntent.RequestClearance), t);
            Assert.True(m.HasCallsign, t);
            Assert.Equal("SCIE", m.Destination);
        }
        Assert.Equal('A', P("request IFR clearance to SCIE, information Alfa").AtisLetter);
    }

    [Fact]
    public void Clearance_readback_values()
    {
        var m = P("Cleared SCIE, GELU5B departure, runway 17R, climb flight level one zero zero, squawk fower seven two one, LAN205");
        Assert.Equal("SCIE", m.Destination);
        Assert.Equal("GELU5B", m.Procedure);
        Assert.Equal("17R", m.Runway!.ToUpperInvariant());
        Assert.Equal(10000, m.AltitudeFt);
        Assert.True(m.AltitudeIsFlightLevel);
        Assert.Equal(4721, m.Squawk);
        Assert.True(m.HasValues);
    }

    [Theory]
    [InlineData("Santiago Radar, LAN205, request descent")]
    [InlineData("request descent")]
    [InlineData("solicito descenso")]
    public void Descent_request(string text) => Assert.True(P(text).Has(PilotIntent.RequestDescent), $"{P(text).Normalized} -> {P(text).Intents}");

    // ADR-097: el piloto pide otra pista, en inglés o en español.
    [Theory]
    [InlineData("request runway 17R", "17R")]
    [InlineData("requesting runway two zero", "20")]
    [InlineData("solicito pista 35L", "35L")]
    public void Runway_request(string text, string runway)
    {
        var m = P(text);
        Assert.True(m.Has(PilotIntent.RequestRunway), m.Intents.ToString());
        Assert.Equal(runway, m.Runway, ignoreCase: true);
    }

    [Theory]
    [InlineData("climb 5000 feet", 5000)]
    [InlineData("climbing five thousand", 5000)]
    [InlineData("maintain 7000", 7000)]
    [InlineData("passing 3000 ft", 3000)]
    [InlineData("FL240", 24000)]
    public void Altitudes(string text, double ft) => Assert.Equal(ft, P(text).AltitudeFt);

    [Fact]
    public void Requests_and_short_answers()
    {
        Assert.True(P("LAN205 stand 14 request pushback").Has(PilotIntent.RequestPushback));
        Assert.Equal("14", P("LAN205 stand 14 request pushback").Stand);
        Assert.True(P("Santiago Ground LAN205 ready to taxi").Has(PilotIntent.RequestTaxi));
        Assert.True(P("LAN205 holding point runway 17R ready for departure").Has(PilotIntent.ReadyForDeparture));
        Assert.True(P("say again").Has(PilotIntent.SayAgain));
        Assert.True(P("unable, LAN205").Has(PilotIntent.Unable));
        Assert.True(P("wilco LAN205").Has(PilotIntent.Acknowledge));
        Assert.True(P("Santiago Approach LAN205 passing 3000 GELU5B").Has(PilotIntent.CheckIn));
    }

    [Fact]
    public void Frequency_heading_and_qnh()
    {
        var m = P("contact tower one one eight decimal one, heading 180, QNH 1013, LAN205");
        Assert.Equal(118100, m.FrequencyKhz);
        Assert.Equal(180, m.HeadingDeg);
        Assert.Equal(1013, m.QnhHpa);
    }

    [Fact]
    public void Common_words_are_not_taken_as_destination_or_procedure()
    {
        Assert.Null(P("cleared to land runway 17L").Destination);
        Assert.Null(P("LAN205 line up and wait").Procedure);
        Assert.Null(P("LAN205").Procedure); // el indicativo no es una SID
        Assert.False(P("hello there").HasCallsign);
    }

    [Theory]
    [InlineData("LAN205 pido autorización para despege")]
    [InlineData("solicito despege 17R, LAN205")]
    [InlineData("LAN205 solicito despegue")]
    [InlineData("LAN205 request takeoff runway 17R")]
    public void Takeoff_requests_with_common_typos_are_ready_for_departure(string text) =>
        Assert.True(P(text).Has(PilotIntent.ReadyForDeparture));

    [Theory]
    [InlineData("Traffic in sight, LAN205")]
    [InlineData("LAN205 traffic not sighted")]
    [InlineData("Tránsito a la vista, LAN205")]
    public void Traffic_information_acknowledgements(string text)
    {
        var m = P(text);
        Assert.True(m.Has(PilotIntent.Acknowledge), $"{m.Normalized} → {m.Intents}");
    }
}
