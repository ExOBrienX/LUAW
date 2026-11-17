using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Taxi;
using SimATC.Core.Traffic;

namespace SimATC.Core.Tests.Taxi;

/// <summary>
/// La entrada a la pista del piloto es la misma regla que la de los IA (ADR-116, prueba de Marco 2026-11-09 en RKSI): la de
/// rodaje más corto cerca de la cabecera que no cruce la propia pista. Datos reales de RKSI (caché del simulador).
/// </summary>
public class PilotRouteEntryTests
{
    private static readonly Lazy<AirportData> Rksi = new(() =>
        SimAirports.Load("RKSI.json"));
    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Rksi.Value));

    [Theory]
    [InlineData("33L")]
    [InlineData("34R")]
    [InlineData("15R")]
    public void From_the_gates_the_pilot_route_does_not_cross_its_own_runway_and_matches_the_ai(string runway)
    {
        var airport = Rksi.Value;
        var graph = Graph.Value;
        var rwy = PilotTaxiRoute.RunwayIndex(airport, runway)!.Value;
        var gates = Enumerable.Range(0, airport.Parkings.Count).Where(i => ParkedFleetPlanner.Gate(airport.Parkings[i])).Take(40).ToList();
        var checkedCount = 0;
        foreach (var g in gates)
        {
            if (PilotTaxiRoute.From(graph, airport, airport.Parkings[g].Position, runway) is not { } route)
                continue;
            var ai = DepartureAgent.Plan(1, "TST1", airport, graph, g, rwy.Index, rwy.Primary, AircraftPerformance.MediumJet,
                DepartureTimings.Default, 2);
            if (ai is null)
                continue;
            checkedCount++;
            Assert.True(SimATC.Core.Geo.GeoMath.DistanceM(route.HoldShort, graph.Projection.ToGeo(graph.Position(ai.HoldNode))) < 1,
                $"puesto {airport.Parkings[g].DisplayName}: el piloto espera en otro punto que los IA");
        }
        Assert.True(checkedCount > 5, $"solo {checkedCount} puestos comparados");
    }
}
