using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Engine;

/// <summary>Salida de pista y rodaje de las llegadas con los datos reales de SCEL (prueba de Marco, 2026-11-03).</summary>
public class ArrivalTaxiTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    [Theory]
    [InlineData("17L")]
    [InlineData("17R")]
    [InlineData("35L")]
    [InlineData("35R")]
    public void Runway_exits_are_described_and_never_point_backwards(string runway)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet)!;
        Assert.NotNull(l.ExitList);
        Assert.All(l.ExitList!, e =>
        {
            Assert.InRange(e.AngleDeg, 0, 100);
            Assert.True(e.TurnStartM < e.NodeAlongM, "el viraje empieza antes del nodo");
            Assert.InRange(e.SpeedKt, 15, 30);
        });
        Assert.True(l.ExitEntry >= 0);
        Assert.InRange(l.ExitAlongM, FlightPlanner.MinRolloutM(PerformanceCategory.MediumJet), l.LengthM);
    }

    [Theory]
    [InlineData("17L")]
    [InlineData("17R")]
    public void Leaving_the_runway_turns_smoothly_without_a_heading_jump(string runway)
    {
        var airport = Scel.Value;
        var graph = Graph.Value;
        var l = FlightPlanner.LandingFor(airport, graph, runway, PerformanceCategory.MediumJet)!;
        var exit = graph.RunwayEntries[l.ExitEntry];
        var stand = Enumerable.Range(0, airport.Parkings.Count).First(i => airport.Parkings[i].Type is ParkingType.GateMedium or ParkingType.GateHeavy
            && graph.FindRoute(exit.EntryNode, graph.ParkingNode(i)) is not null);
        // Donde termina la carrera: en el eje, al inicio del viraje, con el rumbo de la pista y la velocidad de la salida.
        var a = DepartureAgent.PlanArrival(1, "TST1", airport, graph, exit, stand, l.ExitPoint, l.HeadingDeg, l.ExitSpeedKt,
            AircraftPerformance.MediumJet, 2)!;
        Assert.NotNull(a);
        var first = a.Pose(1).HeadingTrueDeg;
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(l.HeadingDeg, first)) < 3, $"arranca con {first:0}° y la pista es {l.HeadingDeg:0}°");
        var prev = first;
        var maxRate = 0.0;
        for (var t = 0.0; t < 60; t += SimClock.TickSeconds)
        {
            a.Step(SimClock.TickSeconds);
            var h = a.Pose(1).HeadingTrueDeg;
            maxRate = Math.Max(maxRate, Math.Abs(GeoMath.AngleDiffDeg(prev, h)) / SimClock.TickSeconds);
            prev = h;
        }
        // Un reactor dobla en tierra a pocos grados por segundo; un salto de rumbo serían cientos.
        Assert.True(maxRate < 25, $"giró a {maxRate:0}°/s");
    }

    private static readonly Lazy<AirportData> Lebl = new(() =>
        SimAirports.Load("LEBL.json"));

    [Fact]
    public void An_arrival_that_must_cross_a_runway_holds_gets_cleared_by_radio_and_reaches_its_stand()
    {
        var airport = Lebl.Value;
        var graph = TaxiGraph.Build(airport);
        // Una salida de pista y un puesto cuyo rodaje cruza otra pista (en LEBL, 14 % de los casos).
        DepartureAgent? agent = null;
        for (var k = 0; k < graph.RunwayEntries.Count && agent is null; k++)
        {
            var exit = graph.RunwayEntries[k];
            var rwy = airport.Runways[exit.RunwayIndex];
            for (var i = 0; i < airport.Parkings.Count && agent is null; i++)
            {
                if (airport.Parkings[i].Type is not (ParkingType.GateMedium or ParkingType.GateHeavy))
                    continue;
                var route = graph.FindRoute(exit.EntryNode, graph.ParkingNode(i));
                if (route is null || !route.Nodes.Any(n => graph.RunwayAt(n) >= 0))
                    continue;
                var start = graph.Projection.ToGeo(graph.Position(exit.RunwayNode));
                var a = DepartureAgent.PlanArrival(7, "TST7", airport, graph, exit, i, start, rwy.Primary.HeadingTrueDeg, 15,
                    AircraftPerformance.MediumJet, 2, wingspanM: 34);
                if (a is { Crossings.Count: > 0 })
                    agent = a;
            }
        }
        Assert.NotNull(agent);
        var crossing = agent!.Crossings[0];
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var log = new List<string>();
        var engine = new TrafficEngine(bridge, log.Add);
        // TEST DATA: frecuencias de Ground y Torre.
        engine.Radio.Frequencies = new RadioFrequencies(121_700, 118_100, 0);
        var lines = new List<RadioLine>();
        engine.Add(agent);
        var waited = false;
        for (var t = 0.0; t < 900 && agent.Phase != DeparturePhase.AtStand; t += 1.0 / 30)
        {
            engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
            while (engine.Radio.Lines.TryDequeue(out var line))
                lines.Add(line);
            // No pisa la pista antes de oír la autorización.
            if (agent.CrossingsHeard == 0 && agent.Phase == DeparturePhase.Taxi)
                Assert.True(agent.TaxiProgress <= crossing.HoldS + 1, $"pasó el punto de espera sin autorización ({agent.TaxiProgress:0} > {crossing.HoldS:0})");
            waited |= agent.Phase == DeparturePhase.Taxi && agent.SpeedKt < 0.5 && Math.Abs(agent.TaxiProgress - crossing.HoldS) < 30;
        }
        var info = string.Join(" | ", lines.Select(l => $"{l.Kind} {l.Detail}")) + " || " + string.Join(" / ", log.TakeLast(5));
        Assert.True(agent.Phase == DeparturePhase.AtStand, $"no llegó al puesto ({agent.Phase}, {agent.TaxiProgress:0}/{agent.TaxiPath.Length:0} m): {info}");
        Assert.Contains(lines, l => l.Kind == RadioLineKind.CrossRunway && l.Detail == crossing.Ident);
        Assert.Contains(lines, l => l.Kind == RadioLineKind.ReadbackCrossRunway);
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.RunwayVacated) >= 2, info); // al librar su pista y al cruzar
        Assert.Equal(0, engine.Control.Tower.AiIncursions);
        Assert.Equal(agent.Crossings.Count, agent.CrossingsDone);
    }

    // PLAN-PULIDO bloque 3 (ADR-120): la pista cuenta como ocupada solo mientras la cola está a menos de 80 m del eje.
    [Theory]
    [InlineData("17L")]
    [InlineData("17R")]
    public void A_vacating_arrival_frees_the_runway_once_its_tail_is_clear(string runway)
    {
        var airport = Scel.Value;
        var graph = Graph.Value;
        var l = FlightPlanner.LandingFor(airport, graph, runway, PerformanceCategory.MediumJet)!;
        var exit = graph.RunwayEntries[l.ExitEntry];
        var stand = Enumerable.Range(0, airport.Parkings.Count).First(i => airport.Parkings[i].Type is ParkingType.GateMedium or ParkingType.GateHeavy
            && graph.FindRoute(exit.EntryNode, graph.ParkingNode(i)) is not null);
        var a = DepartureAgent.PlanArrival(1, "TST1", airport, graph, exit, stand, l.ExitPoint, l.HeadingDeg, l.ExitSpeedKt,
            AircraftPerformance.MediumJet, 2, wingspanM: 36)!;
        Assert.True(a.OnRunwayInbound);
        Assert.True(a.RunwayClearInS > 0);
        var r = airport.Runways[exit.RunwayIndex];
        var c = graph.Projection.ToLocal(r.Center);
        var h = r.HeadingTrueDeg * GeoMath.DegToRad;
        for (var t = 0.0; t < 120; t += SimClock.TickSeconds)
        {
            a.Step(SimClock.TickSeconds);
            var p = a.Position;
            var across = Math.Abs((p.EastM - c.EastM) * Math.Cos(h) - (p.NorthM - c.NorthM) * Math.Sin(h));
            // Con la cola a más de 80 m del eje ya no ocupa la pista, aunque no haya llegado al punto donde pide rodaje.
            if (across - 18 >= DepartureAgent.RunwayClearLateralM + 1)
                Assert.False(a.OnRunwayInbound, $"a {across:0} m del eje sigue contando como en la pista");
        }
        Assert.False(a.OnRunwayInbound);
        Assert.Equal(0, a.RunwayClearInS);
    }

    [Fact]
    public void Approach_speed_depends_on_the_aircraft_type()
    {
        Assert.True(AirborneAgent.TypicalVappKt("AT76", AirborneProfile.Turboprop) < AirborneAgent.TypicalVappKt("A320", AirborneProfile.MediumJet));
        Assert.True(AirborneAgent.TypicalVappKt("A320", AirborneProfile.MediumJet) < AirborneAgent.TypicalVappKt("B77W", AirborneProfile.HeavyJet));
        // Tipo desconocido: la de su categoría.
        Assert.Equal(AirborneProfile.MediumJet.FinalIasKt, AirborneAgent.TypicalVappKt("ZZZZ", AirborneProfile.MediumJet));
    }
}
