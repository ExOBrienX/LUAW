using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.Taxi;
using SimATC.Core.Traffic;

namespace SimATC.Core.Tests.Taxi;

/// <summary>ADR-171 (KMIA 2026-11-25): AAL1335 dejó la pista hacia el este y dio media vuelta en la misma calle.</summary>
public class UTurnTests
{
    private static AirportData Load(string icao) =>
        AirportCache.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", icao + ".json")))!.Airport;

    [Theory]
    [InlineData("SCEL")]
    [InlineData("LEMD")]
    [InlineData("RKSI")]
    public void Arrivals_hardly_ever_turn_back_on_a_taxiway(string icao)
    {
        var airport = Load(icao);
        var graph = TaxiGraph.Build(airport);
        var roll = FlightPlanner.MinRolloutM(PerformanceCategory.MediumJet);
        var gates = Enumerable.Range(0, airport.Parkings.Count).Where(i => ParkedFleetPlanner.Gate(airport.Parkings[i]) && graph.StandReachable(i)).ToList();
        var (total, turns) = (0, 0);
        for (var r = 0; r < airport.Runways.Count; r++)
        {
            foreach (var primary in new[] { true, false })
            {
                var end = primary ? airport.Runways[r].Primary : airport.Runways[r].Secondary;
                graph.SetFlows([(r, primary)]);
                graph.SetArrivalExitFlows([(r, primary)]);
                if (FlightPlanner.LandingFor(airport, graph, end.Ident, PerformanceCategory.MediumJet) is not { } landing)
                    continue;
                for (var k = 0; k < gates.Count; k += Math.Max(1, gates.Count / 10))
                {
                    var l = FlightPlanner.BestExit(graph, landing.TowardSide(airport.Parkings[gates[k]].Position, roll), gates[k], roll);
                    if (l.ExitEntry < 0 || graph.FindRoute(graph.RunwayEntries[l.ExitEntry].EntryNode, graph.ParkingNode(gates[k])) is not { } route)
                        continue;
                    total++;
                    if (graph.HasUTurn(route, graph.RunwayEntries[l.ExitEntry].RunwayNode))
                        turns++;
                }
            }
        }
        Assert.True(total > 0 && turns <= total * 0.1, $"{turns} de {total}");
    }
}
