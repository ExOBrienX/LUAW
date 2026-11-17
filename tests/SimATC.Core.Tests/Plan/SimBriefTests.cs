using System.Net;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Lector y cliente de SimBrief (RF-29, V-10).</summary>
public class SimBriefTests
{
    // TEST DATA: OFP inventado con la forma del JSON real de SimBrief (V-10): campos vacíos como {}, una lista de un
    // solo alterno como arreglo, y un punto calculado (TOC, tipo "ltlg").
    private const string Ofp = """
        {
          "fetch": { "userid": "123", "static_id": {}, "status": "Success" },
          "params": { "airac": "2510", "time_generated": "1790000000", "units": "kgs" },
          "general": { "icao_airline": "TST", "flight_number": "100", "initial_altitude": "24000",
                       "route": "AAA1A AAAAA UQ1 BBBBB BBB1B", "sid_ident": "AAA1A", "sid_trans": {},
                       "star_ident": "BBB1B", "star_trans": {} },
          "origin": { "icao_code": "SCEL", "name": "TEST ORIGIN", "pos_lat": "-33.39", "pos_long": "-70.79", "plan_rwy": "17L" },
          "destination": { "icao_code": "SCIE", "name": "TEST DEST", "pos_lat": "-36.77", "pos_long": "-73.06", "plan_rwy": "02" },
          "alternate": [ { "icao_code": "SCQP", "name": "TEST ALTN", "pos_lat": "-38.92", "pos_long": "-72.65", "plan_rwy": "01" } ],
          "atc": { "callsign": "TST100" },
          "aircraft": { "icaocode": "A20N", "reg": "CC-TST" },
          "times": { "sched_out": "1790003600" },
          "navlog": { "fix": [
            { "ident": "AAAAA", "type": "wpt", "via_airway": "AAA1A", "is_sid_star": "1", "pos_lat": "-33.6", "pos_long": "-71.0", "altitude_feet": "14000" },
            { "ident": "TOC", "type": "ltlg", "via_airway": "UQ1", "is_sid_star": "0", "pos_lat": "-33.9", "pos_long": "-71.2", "altitude_feet": "24000" },
            { "ident": "SCIE", "type": "apt", "via_airway": "BBB1B", "is_sid_star": "1", "pos_lat": "-36.77", "pos_long": "-73.06", "altitude_feet": "26" }
          ] }
        }
        """;

    [Fact]
    public void Parses_the_fields_RF29_uses()
    {
        var p = SimBriefParser.Parse(Ofp);

        Assert.Equal(PlanSource.SimBrief, p.Source);
        Assert.Equal("TST100", p.Callsign);
        Assert.Equal("A20N", p.AircraftIcao);
        Assert.Equal("CC-TST", p.Registration);
        Assert.Equal("SCEL", p.Origin.Icao);
        Assert.Equal("17L", p.Origin.PlannedRunway);
        Assert.Equal(-33.39, p.Origin.Position!.Value.LatDeg, 6);
        Assert.Equal("SCIE", p.Destination.Icao);
        Assert.Equal("SCQP", p.Alternate!.Icao);
        Assert.Equal(24000, p.CruiseAltitudeFt);
        Assert.Equal("AAA1A", p.SidIdent);
        Assert.Null(p.SidTransition); // llegó como {}
        Assert.Equal("BBB1B", p.StarIdent);
        Assert.Equal("2510", p.Airac);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime, p.GeneratedUtc);
        Assert.Equal(3, p.Fixes.Count);
        Assert.True(p.Fixes[0].IsSidStar);
        Assert.True(p.Fixes[1].IsPseudo);
        Assert.Equal(-71.0, p.Fixes[0].Position.LonDeg, 6);
    }

    [Fact]
    public void A_single_fix_can_arrive_as_an_object()
    {
        // TEST DATA
        var json = """
            { "general": { "initial_altitude": "10000" }, "atc": { "callsign": "TST1" },
              "origin": { "icao_code": "SCEL" }, "destination": { "icao_code": "SCIE" }, "alternate": {},
              "navlog": { "fix": { "ident": "XXXXX", "type": "wpt", "pos_lat": "-34", "pos_long": "-71", "altitude_feet": "10000" } } }
            """;
        var p = SimBriefParser.Parse(json);

        Assert.Single(p.Fixes);
        Assert.Null(p.Alternate);
        Assert.Null(p.Origin.Position);
    }

    [Fact]
    public void Error_status_becomes_a_readable_message()
    {
        var ex = Assert.Throws<SimBriefException>(() =>
            SimBriefParser.Parse("""{"fetch":{"userid":"","static_id":"","status":"Error: Unknown UserID"}}"""));
        Assert.Contains("Unknown UserID", ex.Message);
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        Assert.Throws<SimBriefException>(() => SimBriefParser.Parse("<html>"));
    }

    [Theory]
    [InlineData("1427931", "userid=1427931")]
    [InlineData(" marco ", "username=marco")]
    [InlineData("a b", "username=a%20b")]
    public void Digits_are_a_pilot_id_and_anything_else_a_username(string user, string expected)
    {
        var url = SimBriefClient.UrlFor(user).AbsoluteUri;
        Assert.StartsWith(SimBriefClient.BaseUrl, url);
        Assert.Contains(expected, url);
        Assert.EndsWith("&json=1", url);
    }

    [Fact]
    public async Task Client_returns_the_plan()
    {
        var client = new SimBriefClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, Ofp)));
        var p = await client.FetchLatestAsync("123", CancellationToken.None);
        Assert.Equal("TST100", p.Callsign);
    }

    [Fact]
    public async Task Client_turns_a_400_into_the_simbrief_message()
    {
        var client = new SimBriefClient(new HttpClient(new FakeHandler(HttpStatusCode.BadRequest,
            """{"fetch":{"status":"Error: Unknown UserID"}}""")));
        var ex = await Assert.ThrowsAsync<SimBriefException>(() => client.FetchLatestAsync("999", CancellationToken.None));
        Assert.Contains("Unknown UserID", ex.Message);
    }

    [Fact]
    public async Task Client_requires_a_user()
    {
        var client = new SimBriefClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, Ofp)));
        await Assert.ThrowsAsync<SimBriefException>(() => client.FetchLatestAsync("  ", CancellationToken.None));
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
