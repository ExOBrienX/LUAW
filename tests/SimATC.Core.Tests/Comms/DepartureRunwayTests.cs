using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>
/// La pista de salida del piloto (ADR-106, pedido de Marco 2026-11-09): la del plan si está activa; si no, la activa con el
/// rodaje más corto; y el cambio de pista antes de despegar solo si la suya dejó de usarse y está lejos del punto de espera.
/// </summary>
public class DepartureRunwayTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));
    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    private const int Delivery = 136700;
    private const int Ground = 122200;

    private sealed class Live
    {
        public GeoPoint Position;
        public List<string> Ends = ["17R", "17L"];
        public bool? Open;
    }

    private static (PilotSession S, Live Live) Session(string plannedRunway, IEnumerable<string> ends)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(dataDir, "SCEL"));
        // TEST DATA: plan SCEL → SCIE.
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", PlannedRunway: plannedRunway),
            new PlanAirport("SCIE", PlannedRunway: "02"), 24000, "", []);
        var live = new Live
        {
            Position = Scel.Value.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == 14).Position, Ends = [.. ends],
        };
        var ctx = new DepartureContext(plan, Scel.Value, atc, new AtcStations(Scel.Value, atc), Graph.Value,
            Phraseology.Load(Phraseology.PathFor(dataDir, AtcLanguage.English), new Random(3)), () => live.Ends, () => 'A', () => live.Position,
            () => ReadbackStrictness.Permissive, OnGround: () => true, GroundSpeedKt: () => 0,
            DepartureRunwayOpen: r => live.Open ?? live.Ends.Contains(r));
        return (new PilotSession(ctx, new Random(5)), live);
    }

    private static void Cleared(PilotSession s)
    {
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage(s.Suggestions()[0].Text, Delivery, 2);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
    }

    [Fact]
    public void The_planned_runway_is_used_when_it_is_active_even_if_it_is_not_the_first()
    {
        var (s, _) = Session("17L", ["17R", "17L"]);
        Cleared(s);
        Assert.Equal("17L", s.Clearance!.Runway);
    }

    [Fact]
    public void Without_the_planned_runway_the_active_one_with_the_shortest_taxi_is_used()
    {
        var (s, live) = Session("35R", ["17R", "17L"]);
        Cleared(s);
        var here = live.Position;
        double Length(string rw)
        {
            var r = PilotTaxiRoute.From(Graph.Value, Scel.Value, here, rw)!;
            return r.Points.Zip(r.Points.Skip(1), GeoMath.DistanceM).Sum();
        }
        var shortest = Length("17R") <= Length("17L") ? "17R" : "17L";
        Assert.Equal(shortest, s.Clearance!.Runway);
    }

    [Fact]
    public void A_closed_runway_before_taxi_is_changed_and_the_taxi_goes_to_the_new_one()
    {
        var (s, live) = Session("17L", ["17L"]);
        Cleared(s);
        live.Ends = ["35R"];
        var said = s.Tick(10);
        Assert.Contains(said, x => x.Text.Contains("runway change, new departure runway 35R") && x.Station.Role == AtcRole.Ground);
        Assert.Equal("35R", s.Clearance!.Runway);
        Assert.StartsWith("Runway 35R", s.Suggestions()[0].Text, StringComparison.Ordinal);
        Assert.Empty(s.OnPilotMessage(s.Suggestions()[0].Text, Ground, 12));
        Assert.False(s.AwaitingReadback);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 14);
        var taxi = s.OnPilotMessage("LAN205 request taxi", Ground, 16);
        Assert.Contains(taxi, x => x.Text.Contains("runway 35R"));
    }

    [Fact]
    public void A_closed_runway_while_taxiing_far_from_it_gives_the_new_runway_and_a_new_taxi()
    {
        var (s, live) = Session("17L", ["17L"]);
        Cleared(s);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage(s.Suggestions()[0].Text, Ground, 8);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
        live.Position = s.TaxiRoute!.Points[1];
        live.Ends = ["35R"];
        var said = s.Tick(10);
        Assert.Contains(said, x => x.Text.Contains("runway change, new departure runway 35R"));
        var reply = s.OnPilotMessage(s.Suggestions()[0].Text, Ground, 12);
        Assert.Contains(reply, x => x.Text.Contains("taxi") && x.Text.Contains("runway 35R"));
        Assert.Equal("35R", s.TaxiRoute!.Runway);
        Assert.Equal(PilotPhase.TaxiGiven, s.Phase);
    }

    [Fact]
    public void Near_the_holding_point_or_during_the_gradual_change_the_runway_is_kept()
    {
        var (s, live) = Session("17L", ["17L"]);
        Cleared(s);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage(s.Suggestions()[0].Text, Ground, 8);
        // Cambio gradual: la vieja sigue sirviendo a los que ya van a ella.
        live.Ends = ["35R"];
        live.Open = true;
        Assert.DoesNotContain(s.Tick(10), x => x.Text.Contains("runway change"));
        // Terminado el cambio, pero a unos 400 m del punto de espera: se queda con la suya.
        live.Open = null;
        var pts = s.TaxiRoute!.Points;
        var back = 0.0;
        var i = pts.Count - 1;
        while (i > 0 && back < 400)
        {
            back += GeoMath.DistanceM(pts[i - 1], pts[i]);
            i--;
        }
        live.Position = pts[i];
        Assert.DoesNotContain(s.Tick(20), x => x.Text.Contains("runway change"));
        Assert.Equal("17L", s.Clearance!.Runway);
    }

    [Fact]
    public void Another_runway_opening_does_not_move_the_pilot()
    {
        var (s, live) = Session("17L", ["17L"]);
        Cleared(s);
        live.Ends = ["17L", "17R"];
        Assert.Empty(s.Tick(10));
        Assert.Equal("17L", s.Clearance!.Runway);
    }
}
