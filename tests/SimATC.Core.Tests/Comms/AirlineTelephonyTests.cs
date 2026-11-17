using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Telefonía de aerolíneas (hito 4e-3) con la base real de <c>data/airlines.json</c>.</summary>
public class AirlineTelephonyTests
{
    private static readonly Lazy<AirlineTelephony> Airlines = new(() =>
        AirlineTelephony.Load(AirlineTelephony.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"))));

    [Theory]
    [InlineData("LAN", "LAN CHILE")]
    [InlineData("SKU", "AEROSKY")]
    [InlineData("JAT", "ROCKSMART")]
    [InlineData("LXP", "LANEX")]
    [InlineData("BAW", "SPEEDBIRD")]
    [InlineData("DLH", "LUFTHANSA")]
    public void Known_airlines_have_their_telephony_verified(string icao, string telephony)
    {
        var a = Airlines.Value.Find(icao);
        Assert.NotNull(a);
        Assert.Equal(telephony, a.Telephony);
        Assert.True(a.Verified);
    }

    [Fact]
    public void The_database_is_worldwide_and_clean()
    {
        Assert.True(Airlines.Value.Count > 4000, $"{Airlines.Value.Count} aerolíneas");
        Assert.Null(Airlines.Value.Find("ZZZ"));
    }

    [Theory]
    [InlineData("LAN2045", "LAN CHILE two zero fower fife")]
    [InlineData("jat123", "ROCKSMART one two tree")]
    [InlineData("SKU509A", "AEROSKY fife zero niner Alfa")]
    [InlineData("HDL060", "Hotel Delta Lima zero six zero")] // sin telefonía: letra por letra
    [InlineData("CCABC", "CCABC")]                           // matrícula: tal cual
    public void Callsigns_are_spoken_with_the_telephony(string callsign, string spoken) =>
        Assert.Equal(spoken, Airlines.Value.Spoken(callsign));

    [Theory]
    [InlineData("BAW2045, taxi to holding point runway 17R via G, Q", "SPEEDBIRD two zero fower fife")]
    [InlineData("Taxi to holding point runway 17R, BAW2045", "SPEEDBIRD two zero fower fife")]
    [InlineData("Santiago Ground, FDX312, request taxi", "FEDEX tree one two")]
    [InlineData("DAE9208, hold position", "YELLOW niner two zero eight")]
    public void Ai_phrases_say_the_airline_telephony(string written, string expected) =>
        Assert.Contains(expected, SpokenForm.Of(written, Airlines.Value));

    [Fact]
    public void Spoken_form_of_a_phrase_uses_the_telephony()
    {
        Assert.Equal("Holding, LAN CHILE two zero fife", SpokenForm.Of("Holding, LAN205", Airlines.Value));
        // Sin base, como antes.
        Assert.Equal("Holding, LAN two zero fife", SpokenForm.Of("Holding, LAN205"));
    }

    [Theory]
    [InlineData("Santiago Delivery, LAN205, request IFR clearance to SCIE")]
    [InlineData("Santiago Delivery, LAN CHILE 205, request IFR clearance to SCIE")]
    [InlineData("santiago delivery lan chile two zero five request ifr clearance to SCIE")]
    [InlineData("Santiago Delivery, lanchile 205, request IFR clearance to SCIE")]
    public void The_pilot_can_say_the_callsign_with_the_telephony(string text)
    {
        var m = MessageParser.Parse(text, Airlines.Value.Forms("LAN205"));
        Assert.True(m.HasCallsign, InputNormalizer.Normalize(text));
    }

    [Fact]
    public void Another_airline_is_not_the_pilot()
    {
        var m = MessageParser.Parse("Santiago Delivery, AEROSKY 205, request IFR clearance to SCIE", Airlines.Value.Forms("LAN205"));
        Assert.False(m.HasCallsign);
    }
}
