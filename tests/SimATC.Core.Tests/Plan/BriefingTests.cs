using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Briefing del OFP de SimBrief: datos importantes, OFP en texto y PDF.</summary>
public class BriefingTests
{
    // TEST DATA: OFP inventado con la forma real de SimBrief (V-10): TLR con una pista como objeto y otra en arreglo.
    private const string Ofp = """
        {
          "fetch": { "status": "Success" },
          "params": { "units": "kgs", "airac": "2510" },
          "general": { "icao_airline": "TST", "flight_number": "1", "initial_altitude": "24000", "costindex": "21",
                       "stepclimb_string": "SCEL/0240", "avg_wind_comp": "36", "avg_wind_dir": "357", "avg_wind_spd": "045",
                       "route_distance": "266", "route": "DCT" },
          "origin": { "icao_code": "SCEL", "plan_rwy": "17L", "trans_alt": "10000", "metar": "SCEL 202200Z 33004KT 9999", "taf": {} },
          "destination": { "icao_code": "SCIE", "plan_rwy": "02", "trans_level": "11000", "metar": "SCIE 202200Z 02017KT", "taf": "SCIE TAF" },
          "atc": { "callsign": "TST1" },
          "aircraft": { "icaocode": "A20N" },
          "fuel": { "taxi": "230", "enroute_burn": "1703", "contingency": "498", "alternate_burn": "1128", "reserve": "837",
                    "extra": "0", "min_takeoff": "4166", "plan_takeoff": "4166", "plan_ramp": "4396", "plan_landing": "2463" },
          "weights": { "est_zfw": "54272", "max_zfw": "62800", "est_tow": "58438", "max_tow": "68003", "est_ldw": "56735",
                       "max_ldw": "66300", "payload": "11772", "pax_count": "114", "cargo": "2082" },
          "times": { "est_time_enroute": "3077", "est_block": "4757", "taxi_out": "1200" },
          "tlr": { "takeoff": { "runway": [
            { "identifier": "17L", "flap_setting": "1", "speeds_v1": "136", "speeds_vr": "136", "speeds_v2": "137",
              "flex_temperature": "70", "thrust_setting": "FLEX" },
            { "identifier": "17R", "flap_setting": "2" } ] } },
          "text": { "plan_html": "<div><pre><b>[ OFP ]</b>\nTST1 &amp; SCEL-SCIE<br/>FUEL\t4396</pre></div>" },
          "files": { "directory": "https://www.simbrief.com/ofp/flightplans/", "pdf": { "name": "PDF", "link": "SCELSCIE_PDF_1.pdf" } }
        }
        """;

    [Fact]
    public void Reads_the_key_data()
    {
        var b = SimBriefParser.Parse(Ofp).Briefing!;

        Assert.Equal("kgs", b.Units);
        Assert.Equal(4396, b.FuelRamp);
        Assert.Equal(4166, b.FuelTakeoff);
        Assert.Equal(1703, b.FuelTrip);
        Assert.Equal(54272, b.Zfw);
        Assert.Equal(68003, b.MaxTow);
        Assert.Equal(114, b.Passengers);
        Assert.Equal(3077, b.EnrouteS);
        Assert.Equal("21", b.CostIndex);
        Assert.Equal(10000, b.TransitionAltitudeFt);
        Assert.Equal(11000, b.TransitionLevelFt);
        Assert.Equal(new TakeoffPerformance("17L", "1", 136, 136, 137, "FLEX", 70), b.Takeoff);
        Assert.Equal("https://www.simbrief.com/ofp/flightplans/SCELSCIE_PDF_1.pdf", b.PdfUrl);
        Assert.Equal(2, b.Weather.Count);
        Assert.Null(b.Weather[0].Taf); // llegó como {}
    }

    [Fact]
    public void Ofp_text_has_no_tags_and_decoded_entities()
    {
        var text = SimBriefParser.Parse(Ofp).Briefing!.OfpText;

        Assert.StartsWith("[ OFP ]", text);
        Assert.Contains("TST1 & SCEL-SCIE", text);
        Assert.Contains("FUEL    4396", text);
        Assert.DoesNotContain("<", text);
    }

    /// <summary>Retomar vuelo: el plan de SimBrief con su briefing se guarda y se lee igual (prueba de Marco 2026-11-09).</summary>
    [Fact]
    public void A_simbrief_plan_survives_the_resume_file()
    {
        var plan = SimBriefParser.Parse(Ofp);
        var snap = new SimATC.Core.Comms.FlightSnapshot(plan, SimATC.Core.Comms.PilotPhase.EnRoute, null, true, null, DateTime.UtcNow);
        var back = SimATC.Core.Comms.FlightResume.Deserialize(SimATC.Core.Comms.FlightResume.Serialize(snap))!.Plan;
        Assert.Equal(plan.Callsign, back.Callsign);
        Assert.Equal(plan.Fixes.Count, back.Fixes.Count);
        Assert.Equal(plan.Fixes, back.Fixes);
        Assert.Equal(plan.SidIdent, back.SidIdent);
        Assert.NotNull(back.Briefing);
        Assert.Equal(plan.Briefing!.OfpText, back.Briefing!.OfpText);
    }
}
