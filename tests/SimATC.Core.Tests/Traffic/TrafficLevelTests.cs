using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Traffic;

namespace SimATC.Core.Tests.Traffic;

/// <summary>
/// Niveles de tráfico y llenado por tipo de puesto (ADR-109, pedido de Marco 2026-11-09: "siempre se estacionan 20"). Con
/// los puestos reales de LEMD (grande) y SCIE (chico), aerolíneas y modelos de prueba.
/// </summary>
public class TrafficLevelTests
{
    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private static readonly Lazy<AirportData> Lemd = new(() => Load("LEMD"));
    private static readonly Lazy<AirportData> Scie = new(() => Load("SCIE"));
    private static readonly Lazy<AirportData> Scel = new(() => Load("SCEL"));

    // TEST DATA: aerolíneas y modelos inventados (una de base con muchos destinos, otras chicas, una de carga).
    private static RouteAirline Airline(string icao, int destinations, bool cargo, params string[] types) =>
        new(icao, icao, true, cargo, types, [.. Enumerable.Range(0, destinations).Select(i => $"D{i}")], []);

    private static RouteList Routes() => new("TEST", new RouteSource("test", "", 0, ""), true, null,
    [
        Airline("HUB", 40, false, "A320", "A333", "B788"),
        Airline("AAA", 10, false, "A320"),
        Airline("BBB", 10, false, "A320", "A333"),
        Airline("CCC", 8, false, "E190"),
        Airline("FRT", 3, true, "B77F"),
    ]);

    private static AircraftModel M(string type, string airline, double span) =>
        new($"T_{type}_{airline}", ModelSource.Fsltl, type, airline, WakeCategory.Medium, 2, span, ["GATE"], []);

    private static ModelMatcher Matcher() => new(
    [
        .. new[] { "HUB", "AAA", "BBB", "CCC" }.SelectMany(a => new[] { M("A320", a, 35.8), M("E190", a, 28.7), M("A333", a, 60.3), M("B788", a, 60.1) }),
        new AircraftModel("T_B77F_FRT", ModelSource.Fsltl, "B77F", "FRT", WakeCategory.Heavy, 2, 64.8, ["CARGO"], []),
    ], []);

    private static IReadOnlyList<GaDecorModel> Ga() => GaDecor.Load(Path.Combine(AppContext.BaseDirectory, "Data", "fleet", "ga-decor.json"));

    private static List<ParkedAircraft> Plan(AirportData airport, TrafficLevel level, int keepFree = 6, int seed = 3, int custom = 40) =>
        ParkedFleetPlanner.PlanForLevel(airport, Routes(), Matcher(), _ => [], TrafficPreset.For(level, custom), keepFree, null, seed, ga: Ga());

    [Fact]
    public void The_decor_file_has_the_simulator_models_with_their_wingspan()
    {
        var ga = Ga();
        Assert.True(ga.Count >= 30);
        Assert.All(ga, m => Assert.StartsWith("Asobo PassiveAircraft ", m.Title, StringComparison.Ordinal));
        Assert.All(ga, m => Assert.InRange(m.WingspanM, 9, 23));
        // Solo los que el simulador tiene.
        Assert.Single(GaDecor.Load(Path.Combine(AppContext.BaseDirectory, "Data", "fleet", "ga-decor.json"), new HashSet<string> { "Asobo PassiveAircraft C172" }));
    }

    [Fact]
    public void A_big_airport_gets_far_more_than_twenty_and_levels_go_up()
    {
        var low = Plan(Lemd.Value, TrafficLevel.Low).Count(p => p.Airline.Length > 0);
        var medium = Plan(Lemd.Value, TrafficLevel.Medium).Count(p => p.Airline.Length > 0);
        var high = Plan(Lemd.Value, TrafficLevel.High).Count(p => p.Airline.Length > 0);
        var average = Plan(Lemd.Value, TrafficLevel.Average).Count(p => p.Airline.Length > 0);
        Assert.True(low < medium && medium < high, $"{low} {medium} {high}");
        Assert.True(average > 20, $"promedio {average}");
    }

    [Fact]
    public void Stands_stay_free_for_arrivals_and_a_small_airport_gets_few()
    {
        var census = StandCensus.Of(Scie.Value);
        var airlineStands = census.Where(kv => kv.Key is not (StandCategory.GeneralAviation or StandCategory.None)).Sum(kv => kv.Value);
        var plan = Plan(Scie.Value, TrafficLevel.High, keepFree: 3);
        Assert.True(plan.Count(p => p.Airline.Length > 0) <= Math.Max(0, airlineStands - 3), $"{plan.Count} de {airlineStands}");
        // Personalizado con 100 en un aeropuerto chico: solo lo que cabe.
        var custom = Plan(Scie.Value, TrafficLevel.Custom, keepFree: 3, custom: 100);
        Assert.True(custom.Count(p => p.Airline.Length > 0) <= airlineStands);
    }

    [Fact]
    public void Aircraft_fit_their_stands_widebodies_go_to_heavy_gates_and_no_stand_is_used_twice()
    {
        var plan = Plan(Lemd.Value, TrafficLevel.High);
        Assert.Equal(plan.Count, plan.Select(p => p.ParkingIndex).Distinct().Count());
        Assert.All(plan, p => Assert.True(p.Parking.RadiusM >= p.Match.Model.WingspanM / 2 - ParkedFleetPlanner.FitToleranceM - 0.01,
            $"{p.Type} en {p.Parking.Type} r={p.Parking.RadiusM}"));
        var heavy = plan.Where(p => StandCensus.CategoryOf(p.Parking) == StandCategory.GateHeavy).ToList();
        if (heavy.Count > 0)
            Assert.True(heavy.Count(p => p.Match.Model.WingspanM >= 44) >= heavy.Count / 2, "las puertas grandes, con fuselaje ancho");
        // Carga en plataformas de carga, con la aerolínea de carga.
        Assert.All(plan.Where(p => StandCensus.CategoryOf(p.Parking) == StandCategory.Cargo), p => Assert.Equal("FRT", p.Airline));
    }

    [Fact]
    public void Decor_is_remote_and_general_aviation_and_the_hub_may_exceed_the_airline_cap()
    {
        var plan = Plan(Lemd.Value, TrafficLevel.High);
        Assert.All(plan.Where(p => p.Decor), p => Assert.Contains(StandCensus.CategoryOf(p.Parking), new[] { StandCategory.Remote, StandCategory.GeneralAviation }));
        Assert.All(plan.Where(p => p.Airline.Length == 0), p => Assert.True(p.Decor));
        var airline = plan.Where(p => p.Airline.Length > 0).ToList();
        var cap = Math.Max(2, (int)Math.Ceiling(ParkedFleetPlanner.AirlineCapShare * airline.Count)) + 1;
        Assert.All(airline.GroupBy(p => p.Airline).Where(g => g.Key != "HUB"), g => Assert.True(g.Count() <= cap, $"{g.Key} {g.Count()}"));
    }

    // TEST DATA: como EGCC: dos de corto radio con muchos destinos y dos de largo radio con uno.
    private static RouteList ShortAndLongHaul() => new("TEST", new RouteSource("test", "", 0, ""), true, null,
    [
        Airline("EZY", 40, false),
        Airline("RYR", 30, false),
        Airline("QTR", 1, false),
        Airline("UAE", 1, false),
    ]);

    private static ModelMatcher LongHaulMatcher() => new(
    [
        M("A320", "EZY", 35.8), M("A20N", "EZY", 35.8), M("B738", "RYR", 35.8),
        M("B77W", "QTR", 64.8), M("A359", "QTR", 64.75), M("B77W", "UAE", 64.8), M("A388", "UAE", 79.75),
    ], []);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Long_haul_airlines_get_the_heavy_gates_but_only_a_few_per_destination(int seed)
    {
        var matcher = LongHaulMatcher();
        var plan = ParkedFleetPlanner.PlanForLevel(Lemd.Value, ShortAndLongHaul(), matcher, matcher.TypesFor,
            TrafficPreset.For(TrafficLevel.High, 40), 6, null, seed);
        // Antes, las de corto radio ganaban casi todas las puertas grandes por tener más destinos (EGCC sin Qatar ni Emirates).
        foreach (var airline in new[] { "QTR", "UAE" })
        {
            var count = plan.Count(p => p.Airline == airline);
            Assert.InRange(count, 1, 2 * ParkedFleetPlanner.WidebodyPerDestination);
        }
        // Lo que sobra de las puertas grandes sigue llenándose como antes.
        Assert.Contains(plan, p => StandCensus.CategoryOf(p.Parking) == StandCategory.GateHeavy && p.Airline is "EZY" or "RYR");
    }

    // TEST DATA: la base mundial no dice qué aerolínea es de carga (todas llegan como de pasajeros).
    private static RouteList WithUnflaggedCargo() => new("TEST", new RouteSource("test", "", 0, ""), true, null,
    [
        Airline("EZY", 20, false),
        Airline("BCS", 6, false),
        Airline("FDX", 6, false),
        Airline("CPA", 2, false),
    ]);

    private static AircraftModel Freighter(string type, string airline, double span) =>
        new($"T_{type}_{airline}_Cargo", ModelSource.Fsltl, type, airline, WakeCategory.Medium, 2, span, ["CARGO"], []);

    private static ModelMatcher CargoMatcher() => new(
    [
        M("A320", "EZY", 35.8), Freighter("B752", "BCS", 38), Freighter("B763", "FDX", 47.6), Freighter("AT76", "FDX", 27),
        M("B77W", "CPA", 64.8), Freighter("B748", "CPA", 68.4),
    ], []);

    [Fact]
    public void Airlines_with_only_cargo_liveries_are_cargo_airlines()
    {
        var matcher = CargoMatcher();
        Assert.True(matcher.IsCargoAirline("BCS"));
        Assert.True(matcher.IsCargoAirline("fdx"));
        Assert.False(matcher.IsCargoAirline("CPA")); // pasajeros y carga: sigue siendo de pasajeros
        Assert.False(matcher.IsCargoAirline("EZY"));
        Assert.False(matcher.IsCargoAirline("XXX")); // sin liveries
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Cargo_airlines_from_the_world_routes_never_park_at_passenger_gates(int seed)
    {
        var matcher = CargoMatcher();
        // SCEL tiene plataformas de carga (LEMD no); personalizado con 200 para que se llenen (van después de las puertas).
        var plan = ParkedFleetPlanner.PlanForLevel(Scel.Value, WithUnflaggedCargo(), matcher, matcher.TypesFor,
            TrafficPreset.For(TrafficLevel.Custom, 200), 6, null, seed);
        Assert.All(plan.Where(p => p.Airline is "BCS" or "FDX"),
            p => Assert.Equal(StandCategory.Cargo, StandCensus.CategoryOf(p.Parking)));
        Assert.Contains(plan, p => p.Airline is "BCS" or "FDX");
    }

    [Fact]
    public void Arrivals_prefer_a_stand_of_their_kind()
    {
        var gate = new Parking(ParkingType.GateHeavy, default, default, 1, new(0, 0), 0, 40, []);
        var ramp = new Parking(ParkingType.RampCargo, default, default, 2, new(0, 0), 0, 40, []);
        Assert.True(ParkedFleetPlanner.KindPenalty(gate, cargo: true) > ParkedFleetPlanner.KindPenalty(ramp, cargo: true));
        Assert.True(ParkedFleetPlanner.KindPenalty(ramp, cargo: false) > ParkedFleetPlanner.KindPenalty(gate, cargo: false));
    }

    [Fact]
    public void Each_fill_is_different()
    {
        var a = Plan(Lemd.Value, TrafficLevel.Average, seed: 1).Select(p => p.ParkingIndex).Order().ToList();
        var b = Plan(Lemd.Value, TrafficLevel.Average, seed: 2).Select(p => p.ParkingIndex).Order().ToList();
        Assert.NotEqual(a, b);
    }
}
