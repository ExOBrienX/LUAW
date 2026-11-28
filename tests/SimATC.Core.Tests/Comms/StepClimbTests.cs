using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>
/// ADR-175 (vuelo EGLL-EDDF, 2026-11-27): apenas pasaba a Aproximación le daban FL350 de una. Ahora Salidas/Aproximación
/// sube hasta FL150, lo pasa al Centro y el Centro da el crucero. Sin Centro conocido, el crucero de una como antes.
/// </summary>
public class StepClimbTests
{
    private const double Nm = 1852;
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static readonly Lazy<(SimATC.Core.World.FirMap Map, SimATC.Core.World.CenterData Official)> Centers = new(() =>
        (SimATC.Core.World.FirMap.Load(SimATC.Core.World.FirMap.DatPathFor(DataDir), SimATC.Core.World.FirMap.BoundariesPathFor(DataDir)),
         SimATC.Core.World.CenterData.Load(SimATC.Core.World.CenterData.PathFor(DataDir))));

    private static (List<string> Heard, PilotSession Session) Fly(bool withCenter)
    {
        // TEST DATA: SCEL → SCIE a FL350 (el plan real va más bajo: es solo para tener un crucero sobre el escalón).
        var scel = SimAirports.Load("SCEL.json");
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(DataDir, "SCEL"));
        var scie = SimAirports.Load("SCIE.json");
        var phrases = Phraseology.Load(Phraseology.PathFor(DataDir, AtcLanguage.English), new Random(3));
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", Position: scel.Reference, PlannedRunway: "17R"),
            new PlanAirport("SCIE", Position: scie.Reference, PlannedRunway: "02"), 35000, "GELU5B GELUS UQ809 ARUNI",
            [
                new PlanFix("GELUS", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.15), 35000),
                new PlanFix("ARUNI", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.55), 35000),
            ]) { SidIdent = "GELU5B" };
        var arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), TaxiGraph.Build(scie), null, "02", null);
        var r17R = PilotTaxiRoute.RunwayIndex(scel, "17R")!.Value.Index;
        var position = scel.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == 14).Position;
        var onGround = true;
        var altitude = 1600.0;
        var speed = 0.0;
        var ctx = new DepartureContext(plan, scel, atc, new AtcStations(scel, atc), TaxiGraph.Build(scel), phrases, () => ["17R"], () => 'A',
            () => position, () => ReadbackStrictness.Realistic,
            RunwayStatus: () => new PilotRunwayStatus(r17R, null, 0, null, true),
            OnGround: () => onGround, HeadingDeg: () => 180, AltitudeFt: () => altitude, GroundSpeedKt: () => speed,
            Wind: () => "180 degrees 8 knots", Arrival: () => arrival, QnhHpa: () => 1015,
            CenterAt: p => withCenter && Centers.Value.Map.At(p) is { } fir ? Centers.Value.Official.StationFor(fir.Icao, p) : null);
        var s = new PilotSession(ctx, new Random(5));
        var heard = new List<string>();
        var copilot = new Copilot(new Random(2));
        var settings = new CopilotSettings(CopilotReadbacks.Always, RoutineCalls: true, Rescue: true, Tune: false);
        var flown = 0.0;
        var leg = GeoMath.DistanceM(scel.Reference, scie.Reference);
        for (var t = 0.0; t < 3600 && altitude < 34_900; t += 2)
        {
            switch (s.Phase)
            {
                case PilotPhase.Taxiing or PilotPhase.TowerHandoffGiven or PilotPhase.WithTower or PilotPhase.HoldShortGiven when s.TaxiRoute is { } tr:
                    position = tr.HoldShort;
                    break;
                case PilotPhase.TakeoffCleared:
                    onGround = false;
                    altitude = 2000;
                    speed = 250;
                    break;
                case >= PilotPhase.Airborne when !onGround:
                    flown += speed * 0.514444 * 2;
                    position = GeoMath.Intermediate(scel.Reference, scie.Reference, Math.Min(0.5, flown / leg));
                    // Sube 2500 ft/min hasta lo autorizado.
                    var cleared = s.ClearedAltitudeFt ?? 10000; // la autorización de SCEL: 10000 ft
                    altitude = Math.Min(altitude + 2500.0 / 30, cleared);
                    break;
            }
            foreach (var x in s.Tick(t))
                heard.Add($"[{x.Station.Role}] {x.Text}");
            if (copilot.Tick(s, settings, t, false) is { } act)
            {
                foreach (var x in s.OnPilotMessage(act.Text, act.Station.Khz, t))
                    heard.Add($"[{x.Station.Role}] {x.Text}");
            }
            if (s.Phase == PilotPhase.PushbackApproved && !s.PushedBack)
                s.MarkPushedBack();
        }
        return (heard, s);
    }

    [Fact]
    public void Departure_climbs_to_fl150_and_the_center_gives_the_cruise_level()
    {
        var (heard, s) = Fly(withCenter: true);
        var all = string.Join(" | ", heard.TakeLast(14));
        var step = heard.FindIndex(h => h.Contains("flight level 150", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("[Center]", StringComparison.Ordinal));
        var cruise = heard.FindIndex(h => h.StartsWith("[Center]", StringComparison.Ordinal) && h.Contains("flight level 350", StringComparison.OrdinalIgnoreCase));
        Assert.True(step >= 0, "Salidas no dio FL150: " + all);
        Assert.True(cruise > step, "el Centro no dio FL350 después: " + all);
        Assert.DoesNotContain(heard.Take(cruise), h => h.Contains("flight level 350", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("[Center]", StringComparison.Ordinal) && h.Contains("climb", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(35000, s.ClearedAltitudeFt);
    }

    [Fact]
    public void Without_a_known_center_departure_gives_the_cruise_level_at_once()
    {
        var (heard, s) = Fly(withCenter: false);
        var all = string.Join(" | ", heard.TakeLast(14));
        Assert.DoesNotContain(heard, h => h.Contains("flight level 150", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(heard, h => h.Contains("flight level 350", StringComparison.OrdinalIgnoreCase));
        Assert.True(s.ClearedAltitudeFt == 35000, all);
    }
}
