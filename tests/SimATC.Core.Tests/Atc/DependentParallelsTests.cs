using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Atc;

/// <summary>
/// Paralelas cercanas en modo dependiente (ADR-118): RKSI tiene dos pares a ~420 m. Con poca demanda, una pista de cada par;
/// con mucha, también la otra del par, pero solo una para llegadas y la otra para salidas. Datos reales de RKSI.
/// </summary>
public class DependentParallelsTests
{
    private static readonly Lazy<AirportData> Rksi = new(() =>
        SimAirports.Load("RKSI.json"));
    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Rksi.Value));

    private static readonly (string A, string B)[] Pairs = [("33L", "33R"), ("34L", "34R")];

    /// <summary>Apagado (por defecto): nunca las dos del par, aunque la demanda sea alta (ADR-118: cruces de pista).</summary>
    [Fact]
    public void Off_by_default_the_close_parallels_are_never_used_together()
    {
        var plan = RunwayPlanner.Plan(Rksi.Value, Graph.Value, ["33L"], 330, 5, arrivalsPerHour: 45, departuresPerHour: 45);
        var used = plan.Arrival.Union(plan.Departure).ToList();
        Assert.All(Pairs, p => Assert.False(used.Contains(p.A) && used.Contains(p.B), plan.Reason));
    }

    [Fact]
    public void Low_demand_uses_one_runway_of_each_pair()
    {
        var plan = RunwayPlanner.Plan(Rksi.Value, Graph.Value, ["33L"], 330, 5, arrivalsPerHour: 10, departuresPerHour: 10, dependentParallels: true);
        // Con poca demanda (ADR-137): como mucho dos pistas, y de cada par cercano, una sola.
        var used = plan.Arrival.Union(plan.Departure).ToList();
        Assert.True(used.Count <= 2, plan.Reason);
        foreach (var (a, b) in Pairs)
            Assert.False(used.Contains(a) && used.Contains(b) && !plan.Segregated, plan.Reason);
    }

    [Fact]
    public void High_demand_opens_the_close_parallel_only_as_arrivals_and_departures_apart()
    {
        var plan = RunwayPlanner.Plan(Rksi.Value, Graph.Value, ["33L"], 330, 5, arrivalsPerHour: 45, departuresPerHour: 45, dependentParallels: true);
        var used = plan.Arrival.Union(plan.Departure).ToList();
        Assert.True(used.Count >= 3, plan.Reason);
        Assert.True(plan.Segregated, plan.Reason);
        foreach (var (a, b) in Pairs)
        {
            // Si se usan las dos del par: una solo llegadas y la otra solo salidas.
            if (used.Contains(a) && used.Contains(b))
                Assert.True(plan.Arrival.Contains(a) != plan.Arrival.Contains(b) && plan.Departure.Contains(a) != plan.Departure.Contains(b), plan.Reason);
        }
    }

    private static readonly Lazy<AirportData> Kewr = new(() =>
        SimAirports.Load("KEWR.json"));

    /// <summary>
    /// En EE. UU. la FAA opera las paralelas cercanas en modo dependiente (ADR-145), con llegadas por la exterior y salidas por
    /// la interior, la de la terminal: KEWR aterriza por la 04R y despega por la 04L (290 m entre ejes). Antes salía una sola
    /// pista mixta y el Headless kewr-busy terminaba con 34 esperas.
    /// </summary>
    [Fact]
    public void Kewr_lands_on_the_outer_runway_and_departs_from_the_inner_one()
    {
        var plan = RunwayPlanner.Plan(Kewr.Value, TaxiGraph.Build(Kewr.Value), ["04L", "04R"], 40, 8, arrivalsPerHour: 30, departuresPerHour: 30);
        Assert.Equal(["04R"], plan.Arrival);
        Assert.Equal(["04L"], plan.Departure);
    }
}
