using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Atc;

/// <summary>
/// Reparto automático de pistas (ADR-091), con los datos del simulador de LEMD, SCEL y SCIE. Prueba de Marco en LEMD
/// (2026-11-07): con 18L/18R para llegadas y salidas a la vez hubo un bloqueo de rodaje y 12 motor y al aire.
/// </summary>
public class RunwayPlannerTests
{
    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private static readonly Lazy<AirportData> Lemd = new(() => Load("LEMD"));
    private static readonly Lazy<TaxiGraph> LemdGraph = new(() => TaxiGraph.Build(Lemd.Value));

    [Fact]
    public void Lemd_south_flow_lands_on_the_18s_and_departs_from_the_14s()
    {
        // Viento del sur, hora punta (unas 40 llegadas y 40 salidas por hora).
        var plan = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["18L", "18R"], 180, 6, arrivalsPerHour: 40, departuresPerHour: 40);
        Assert.True(plan.Segregated, plan.Reason);
        Assert.Equal(["18L", "18R"], plan.Arrival);
        Assert.Equal(["14L", "14R"], plan.Departure);
    }

    [Fact]
    public void During_a_runway_change_never_land_and_depart_head_on()
    {
        // EBBR 2026-11-14: en el cambio progresivo quedaron llegadas por la 01 y salidas por la 19 (la misma pista).
        Assert.False(RunwayPlanner.CanMixDuringChange(Lemd.Value, LemdGraph.Value, ["18L"], ["36R"]));
        Assert.False(RunwayPlanner.CanMixDuringChange(Lemd.Value, LemdGraph.Value, ["18L", "18R"], ["36L", "36R"]));
        // La configuración sur real de LEMD sí se puede usar a la vez.
        Assert.True(RunwayPlanner.CanMixDuringChange(Lemd.Value, LemdGraph.Value, ["18L", "18R"], ["14L", "14R"]));
        Assert.True(RunwayPlanner.CanMixDuringChange(Lemd.Value, LemdGraph.Value, ["18L"], ["18L"]));
    }

    [Fact]
    public void With_several_runways_and_real_demand_there_is_never_mixed_mode()
    {
        foreach (var demand in new[] { 20.0, 60, 120 })
        {
            var plan = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["18L", "18R"], 180, 6, arrivalsPerHour: demand, departuresPerHour: demand);
            Assert.True(plan.Segregated, $"{demand}/h: {plan.Reason}");
            Assert.NotEmpty(plan.Arrival);
            Assert.NotEmpty(plan.Departure);
        }
    }

    /// <summary>
    /// ADR-137: con poca demanda, una o dos pistas mixtas (como se opera de verdad); nunca llegadas por una cabecera sin
    /// aproximación publicada (en LEMD las 14 son solo de despegue).
    /// </summary>
    [Fact]
    public void Low_demand_allows_mixed_mode_and_never_lands_without_an_approach()
    {
        foreach (var demand in new[] { 0.0, 6 })
        {
            var plan = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["18L", "18R"], 180, 6, arrivalsPerHour: demand, departuresPerHour: demand);
            Assert.True(plan.Arrival.Union(plan.Departure).Count() <= 2, $"{demand}/h: {plan.Reason}");
            Assert.All(plan.Arrival, a => Assert.StartsWith("18", a));
        }
    }

    private static readonly Lazy<AirportData> LemgData = new(() =>
        SimAirports.Load("LEMG.json"));

    /// <summary>
    /// ADR-137 (Málaga real: la 13 para todo): con la salida del plan por la 13 y demanda normal, la 13 queda mixta; el plan
    /// nunca manda las llegadas a la 12, que el viento no eligió (así caían los despegues en el Headless).
    /// </summary>
    [Fact]
    public void The_plan_runway_becomes_mixed_instead_of_moving_arrivals_off_the_wind_runway()
    {
        var lemg = LemgData.Value;
        var graph = TaxiGraph.Build(lemg);
        var plan = RunwayPlanner.Plan(lemg, graph, ["13"], 130, 8, arrivalsPerHour: 14, departuresPerHour: 14, planDeparture: "13");
        Assert.Contains("13", plan.Departure);
        Assert.All(plan.Arrival, a => Assert.Equal("13", a));
    }

    private static readonly Lazy<AirportData> ScelData = new(() =>
        SimAirports.Load("SCEL.json"));

    /// <summary>
    /// ADR-138: en un pico de salidas (muchas más salidas que llegadas), una pista de salidas y la otra mixta; con la demanda
    /// pareja, separadas como siempre.
    /// </summary>
    [Fact]
    public void A_departure_peak_uses_one_departure_runway_and_one_mixed()
    {
        var scel = ScelData.Value;
        var graph = TaxiGraph.Build(scel);
        var peak = RunwayPlanner.Plan(scel, graph, ["17L", "17R"], 180, 6, arrivalsPerHour: 10, departuresPerHour: 48);
        Assert.Equal(2, peak.Departure.Count);
        Assert.Single(peak.Arrival);
        Assert.Contains(peak.Arrival[0], peak.Departure);
        var even = RunwayPlanner.Plan(scel, graph, ["17L", "17R"], 180, 6, arrivalsPerHour: 20, departuresPerHour: 20);
        Assert.True(even.Segregated, even.Reason);
    }

    /// <summary>ADR-137: la pista de salida del plan pesa a favor de que sea de salidas.</summary>
    [Fact]
    public void The_plan_departure_runway_is_preferred_for_departures()
    {
        var without = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["32L", "32R"], 330, 8, arrivalsPerHour: 20, departuresPerHour: 20);
        foreach (var wanted in new[] { "36L", "36R" })
        {
            var with = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["32L", "32R"], 330, 8, arrivalsPerHour: 20, departuresPerHour: 20,
                planDeparture: wanted);
            Assert.True(with.Departure.Contains(wanted), $"plan {wanted}: {with.Reason} (sin plan: {without.Reason})");
        }
    }

    /// <summary>
    /// ADR-092: con viento norte y mucha demanda, dos pistas para cada rol (como Barajas); y las calles de salida de las
    /// pistas de llegada quedan con sentido (alejándose de la pista).
    /// </summary>
    [Fact]
    public void Lemd_north_flow_opens_two_runways_per_role_and_one_way_exits()
    {
        var plan = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["32L", "32R"], 330, 8, arrivalsPerHour: 45, departuresPerHour: 45);
        Assert.Equal(["32L", "32R"], plan.Arrival);
        Assert.Equal(["36L", "36R"], plan.Departure);
        var g = TaxiGraph.Build(Lemd.Value);
        var ends = new List<(int, bool)>();
        for (var r = 0; r < Lemd.Value.Runways.Count; r++)
        {
            if (plan.Arrival.Contains(Lemd.Value.Runways[r].Primary.Ident))
                ends.Add((r, true));
            if (plan.Arrival.Contains(Lemd.Value.Runways[r].Secondary.Ident))
                ends.Add((r, false));
        }
        g.SetArrivalExitFlows(ends);
        Assert.True(g.ExitFlowEdgeCount > 20, $"{g.ExitFlowEdgeCount} tramos");
        // Con poca demanda alcanza una de cada una.
        var quiet = RunwayPlanner.Plan(Lemd.Value, LemdGraph.Value, ["32L", "32R"], 330, 8, arrivalsPerHour: 10, departuresPerHour: 10);
        Assert.Single(quiet.Arrival);
        Assert.Single(quiet.Departure);
    }

    [Fact]
    public void A_single_runway_airport_uses_it_for_everything()
    {
        var scie = Load("SCIE");
        var plan = RunwayPlanner.Plan(scie, TaxiGraph.Build(scie), ["20"], 200, 10);
        Assert.Equal(["20"], plan.Arrival);
        Assert.Equal(["20"], plan.Departure);
    }

    [Fact]
    public void Scel_parallels_are_segregated()
    {
        var scel = Load("SCEL");
        var plan = RunwayPlanner.Plan(scel, TaxiGraph.Build(scel), ["17L", "17R"], 180, 8, arrivalsPerHour: 16, departuresPerHour: 16);
        Assert.True(plan.Segregated, plan.Reason);
        Assert.Single(plan.Arrival);
        Assert.Single(plan.Departure);
    }
}
