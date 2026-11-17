using SimATC.Core.Engine;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.Engine;

/// <summary>Herramientas de diagnóstico del motor: congelar el tráfico, quitar un avión y medir el tiempo por cuadro.</summary>
public class EngineToolsTests
{
    private static readonly FrameInfo Tick = new(1.0 / 30, 1, false);

    private static (TrafficEngine Engine, FakeSimBridge Bridge, DepartureAgent Agent) Setup()
    {
        var airport = TestAirports.Small();
        var graph = TaxiGraph.Build(airport);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        var agent = DepartureAgent.Plan(1000, "TST001", airport, graph, parkingIndex: 0, runwayIndex: 0, primaryEnd: true,
            AircraftPerformance.MediumJet, DepartureTimings.Default, engineCount: 2)!;
        bridge.Spawned[1000] = new ParkedSpawnRequest("T", "TST001", airport.Parkings[0].Position, 1000, 270, 2);
        engine.Add(agent);
        return (engine, bridge, agent);
    }

    private static void Run(TrafficEngine engine, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 30)
            engine.OnFrame(Tick);
    }

    [Fact]
    public void Paused_engine_freezes_aircraft_but_keeps_writing_a_still_pose()
    {
        var (engine, bridge, agent) = Setup();
        Run(engine, 300); // ya rodando o más allá
        engine.Paused = true;
        Run(engine, 1);
        var phase = agent.Phase;
        var pose = bridge.Poses[1000];
        Run(engine, 30);

        Assert.Equal(phase, agent.Phase);
        Assert.Equal(pose.Position, bridge.Poses[1000].Position);
        Assert.Equal(0, bridge.Poses[1000].LongitudinalSpeedKt);

        engine.Paused = false;
        Run(engine, 30);
        Assert.NotEqual(pose.Position, bridge.Poses[1000].Position);
    }

    [Fact]
    public void RequestRemove_takes_the_aircraft_out_of_the_sim_on_the_next_frame()
    {
        var (engine, bridge, _) = Setup();
        uint? removed = null;
        engine.AgentRemoved += id => removed = id;
        Run(engine, 5);

        engine.RequestRemove(1000);
        engine.OnFrame(Tick);

        Assert.Equal(0, engine.ActiveCount);
        Assert.Equal(1000u, removed);
        Assert.False(bridge.Spawned.ContainsKey(1000));
    }

    [Fact]
    public void Stats_measure_each_frame()
    {
        var (engine, _, _) = Setup();
        Run(engine, 2);
        var stats = engine.Stats;

        Assert.InRange(stats.Frames, 59, 61);
        Assert.True(stats.MaxFrameUs >= stats.LastFrameUs && stats.AvgFrameUs > 0);
        engine.ResetMaxFrameTime();
        Assert.Equal(0, engine.Stats.MaxFrameUs);
    }
}
