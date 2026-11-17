using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Atc;

/// <summary>
/// La pista del plan del piloto decide el sentido con viento calmo (ADR-127). Prueba de Marco 2026-11-11 en LEMG: el EFB
/// del simulador y SimBrief daban la 13 para salir (la que elige el viento) y LUAW repartía "llegadas 13, salidas 12".
/// Datos reales de LEMG (12/30 y 13/31) y SCEL.
/// </summary>
public class PlanRunwayTests
{
    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private static readonly Lazy<AirportData> Lemg = new(() => Load("LEMG"));
    private static readonly Lazy<TaxiGraph> LemgGraph = new(() => TaxiGraph.Build(Lemg.Value));

    /// <summary>
    /// El reparto de los IA no mira el plan: en el Headless (`lemg-busy`), mandar las salidas a la 13 por el plan dejó 3 a 7
    /// despegues de 15 a 18 y cientos de bloqueos. La pista del plan es solo para el piloto.
    /// </summary>
    [Fact]
    public void The_split_for_ai_traffic_stays_by_taxi_distance()
    {
        var plan = RunwayPlanner.Plan(Lemg.Value, LemgGraph.Value, ["13"], 130, 8, arrivalsPerHour: 15, departuresPerHour: 15);
        Assert.Equal(["13"], plan.Arrival);
        Assert.Equal(["12"], plan.Departure);
    }

    [Fact]
    public void In_calm_wind_the_plan_runway_decides_the_direction()
    {
        var s = new ActiveRunwaySelector(Lemg.Value) { Planned = "31" };
        s.Update(0, 0, 0);
        Assert.Equal("31", s.Current!.Label);
        Assert.Contains("tu plan", s.Current.Reason);
    }

    /// <summary>El plan se carga después: la pista de viento calmo era arbitraria y pasa a la del plan.</summary>
    [Fact]
    public void A_plan_loaded_later_replaces_an_arbitrary_calm_choice()
    {
        var s = new ActiveRunwaySelector(Lemg.Value);
        s.Update(0, 0, 0);
        var arbitrary = s.Current!.Label;
        var other = arbitrary == "13" ? "31" : "13";
        s.Planned = other;
        Assert.True(s.Update(0, 1, 5));
        Assert.Equal(other, s.Current!.Label);
    }

    /// <summary>La pista la eligió el viento; después calma: no se invierte el aeropuerto por el plan.</summary>
    [Fact]
    public void A_runway_chosen_by_the_wind_is_not_reversed_when_it_calms()
    {
        var s = new ActiveRunwaySelector(Lemg.Value) { Planned = "31" };
        s.Update(130, 8, 0);
        Assert.Equal("13", s.Current!.Label);
        Assert.False(s.Update(0, 0, 5));
        Assert.Equal("13", s.Current!.Label);
    }

    [Fact]
    public void With_wind_the_plan_runway_does_not_override_the_headwind()
    {
        var s = new ActiveRunwaySelector(Lemg.Value) { Planned = "31" };
        s.Update(130, 8, 0);
        Assert.Equal("13", s.Current!.Label);
    }

    /// <summary>Con pista preferente del AIP (SCEL: 17), el plan no la cambia con viento calmo.</summary>
    [Fact]
    public void The_aip_preferred_runway_wins_over_the_plan()
    {
        var s = new ActiveRunwaySelector(Load("SCEL"), ["17L", "17R"]) { Planned = "35L" };
        s.Update(0, 0, 0);
        Assert.Equal("17L/17R", s.Current!.Label);
    }

    /// <summary>El selector del plan: todas las cabeceras, las que están en uso con el punto verde, su rol y el viento.</summary>
    [Fact]
    public void The_options_mark_the_runways_in_use_with_their_role_and_wind()
    {
        var options = RunwayOptions.For(Lemg.Value, ["13"], ["13"], ["12"], 130, 8);
        Assert.Equal(["12", "13", "30", "31"], options.Select(o => o.Ident));
        Assert.Equal([true, true, false, false], options.Select(o => o.InUse));
        Assert.StartsWith("en uso · salidas · frente", options[0].Note);
        Assert.StartsWith("en uso · llegadas · frente 8 kt", options[1].Note);
        Assert.StartsWith("cola", options[3].Note);
    }

    [Fact]
    public void Without_wind_or_runway_in_use_no_option_is_marked()
    {
        var options = RunwayOptions.For(Lemg.Value, [], [], [], null, null);
        Assert.All(options, o => Assert.False(o.InUse));
        Assert.All(options, o => Assert.Equal("", o.Note));
    }

    /// <summary>La pista del plan sirve si está en uso, aunque sea la de llegadas de los IA; nunca en sentido contrario.</summary>
    [Theory]
    [InlineData("13", true)]  // la del viento (llegadas de los IA): el EFB y SimBrief la dan para salir
    [InlineData("12", true)]  // la de salidas de los IA
    [InlineData("31", false)] // contra el tráfico
    [InlineData("09", false)] // no existe
    public void The_plan_runway_is_granted_only_if_it_is_in_use(string planned, bool expected) =>
        Assert.Equal(expected, RunwayOptions.PlanRunwayInUse(Lemg.Value, planned, ["13"], ["13"], ["12"]));

    [Fact]
    public void Before_there_is_a_runway_in_use_any_existing_plan_runway_is_fine() =>
        Assert.True(RunwayOptions.PlanRunwayInUse(Lemg.Value, "31", [], [], []));
}
