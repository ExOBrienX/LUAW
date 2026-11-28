using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>
/// ADR-173 (vuelo EGLL-EDDF, 2026-11-27): EGLL no tiene Salidas ni Aproximación en el simulador y no se conocía el Centro de
/// Londres. El piloto quedó con la Torre todo el vuelo y llegó a Frankfurt sin ATC. Ahora el vuelo sigue solo y llama al
/// primer Centro que se conozca; desde ahí, el resto normal hasta el puesto.
/// </summary>
public class UncontrolledFlightTests
{
    private const double Nm = 1852;
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static readonly Lazy<(SimATC.Core.World.FirMap Map, SimATC.Core.World.CenterData Official)> Centers = new(() =>
        (SimATC.Core.World.FirMap.Load(SimATC.Core.World.FirMap.DatPathFor(DataDir), SimATC.Core.World.FirMap.BoundariesPathFor(DataDir)),
         SimATC.Core.World.CenterData.Load(SimATC.Core.World.CenterData.PathFor(DataDir))));

    [Fact]
    public void Without_departure_or_center_the_flight_goes_on_and_calls_the_first_center_it_finds()
    {
        // TEST DATA: SCEL sin Salidas, Aproximación ni Centro (como EGLL en el simulador) y sin archivo ATC.
        var full = SimAirports.Load("SCEL.json");
        var scel = full with
        {
            Frequencies = [.. full.Frequencies.Where(f => f.Type is not (FrequencyType.Approach or FrequencyType.Departure or FrequencyType.Center))],
        };
        var scie = SimAirports.Load("SCIE.json");
        var phrases = Phraseology.Load(Phraseology.PathFor(DataDir, AtcLanguage.English), new Random(3));
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", Position: scel.Reference, PlannedRunway: "17R"),
            new PlanAirport("SCIE", Position: scie.Reference, PlannedRunway: "02"), 24000, "GELU5B GELUS UQ809 ARUNI",
            [
                new PlanFix("GELUS", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.15), 24000),
                new PlanFix("ARUNI", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.55), 24000),
            ]) { SidIdent = "GELU5B" };
        var arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), TaxiGraph.Build(scie), null, "02", null);
        var landing = FlightPlanner.LandingFor(scie, null, "02", SimATC.Core.Engine.PerformanceCategory.MediumJet)!;
        var r17R = PilotTaxiRoute.RunwayIndex(scel, "17R")!.Value.Index;
        var position = scel.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == 14).Position;
        var onGround = true;
        var altitude = 1600.0;
        var speed = 0.0;
        var heading = 0.0;
        var centerKnown = false;
        var ctx = new DepartureContext(plan, scel, null, new AtcStations(scel, null), TaxiGraph.Build(scel), phrases, () => ["17R"], () => 'A',
            () => position, () => ReadbackStrictness.Realistic,
            RunwayStatus: () => new PilotRunwayStatus(r17R, null, 0, null, true),
            OnGround: () => onGround, HeadingDeg: () => heading, AltitudeFt: () => altitude, GroundSpeedKt: () => speed,
            Wind: () => "020 degrees 8 knots", Arrival: () => arrival, QnhHpa: () => 1015,
            // El Centro se conoce recién a mitad de camino (como Langen en el vuelo a Frankfurt).
            CenterAt: p => centerKnown && Centers.Value.Map.At(p) is { } fir ? Centers.Value.Official.StationFor(fir.Icao, p) : null);
        var s = new PilotSession(ctx, new Random(5));
        Assert.Null(s.DepartureStation);

        var finalStart = GeoMath.Destination(landing.Threshold, GeoMath.NormalizeDeg(landing.HeadingDeg + 180), 12 * Nm);
        var leg1 = GeoMath.DistanceM(scel.Reference, finalStart);
        var leg2 = GeoMath.DistanceM(finalStart, landing.Threshold);
        var heard = new List<string>();
        var copilot = new Copilot(new Random(2));
        var settings = new CopilotSettings(CopilotReadbacks.Always, RoutineCalls: true, Rescue: true, Tune: false);
        var flown = 0.0;
        var sawUncontrolled = false;
        var landed = false;
        for (var t = 0.0; t < 3 * 3600 && s.Phase != PilotPhase.Arrived; t += 2)
        {
            switch (s.Phase)
            {
                case PilotPhase.Taxiing or PilotPhase.TowerHandoffGiven or PilotPhase.WithTower or PilotPhase.HoldShortGiven when s.TaxiRoute is { } tr:
                    position = tr.HoldShort;
                    break;
                case PilotPhase.TakeoffCleared:
                    onGround = false;
                    altitude = 2000;
                    speed = 180;
                    break;
                case >= PilotPhase.Airborne and < PilotPhase.Landed when !onGround:
                {
                    flown += speed * 0.514444 * 2;
                    var remainingNm = (leg1 + leg2 - flown) / Nm;
                    position = flown <= leg1
                        ? GeoMath.Intermediate(scel.Reference, finalStart, flown / leg1)
                        : GeoMath.Intermediate(finalStart, landing.Threshold, Math.Min(1, (flown - leg1) / leg2));
                    heading = flown <= leg1 ? GeoMath.InitialBearingDeg(position, finalStart) : landing.HeadingDeg;
                    speed = remainingNm < 20 ? 160 : 420;
                    altitude = Math.Min(Math.Min(altitude + 2500.0 / 30, 24000), scie.ElevationFt + 50 + remainingNm * 318);
                    centerKnown |= flown > leg1 * 0.4;
                    if (s.Uncontrolled)
                    {
                        sawUncontrolled = true;
                        Assert.Null(s.NextStep().Station);
                    }
                    if (remainingNm < 0.3 && s.LandingCleared)
                    {
                        onGround = true;
                        landed = true;
                        speed = 40;
                        position = landing.Threshold;
                    }
                    break;
                }
                case PilotPhase.Landed or PilotPhase.TaxiIn when landed:
                    speed = 10;
                    position = GeoMath.Destination(landing.Threshold, landing.HeadingDeg, 1500);
                    if (s.TaxiInRoute is { Count: > 0 } tin && !s.AwaitingReadback)
                    {
                        position = tin[^1];
                        speed = 0;
                    }
                    break;
            }
            foreach (var x in s.Tick(t))
                heard.Add($"[{x.Station.Callsign}] {x.Text}");
            if (copilot.Tick(s, settings, t, false) is { } act)
            {
                foreach (var x in s.OnPilotMessage(act.Text, act.Station.Khz, t))
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
            }
            if (s.Phase == PilotPhase.PushbackApproved && !s.PushedBack)
                s.MarkPushedBack();
        }

        var all = string.Join(" | ", heard.TakeLast(12));
        Assert.True(sawUncontrolled, "nunca quedó sin ATC: " + all);
        Assert.True(s.Phase == PilotPhase.Arrived, $"terminó en {s.Phase} ({s.NextStep().Text}, colación {s.AwaitingReadback}, alt {altitude:0}, quedan {(leg1 + leg2 - flown) / Nm:0.0} NM): {all}");
        // Después de despegar la Torre no lo pasa a nadie; el primer contacto es con el Centro cuando aparece.
        Assert.Contains(heard, h => h.StartsWith("[Santiago Radar]", StringComparison.Ordinal));
        Assert.Contains(heard, h => h.Contains("cleared to land"));
        Assert.Contains(heard, h => h.Contains("taxi to stand"));
    }
}
