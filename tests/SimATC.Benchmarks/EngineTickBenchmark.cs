using BenchmarkDotNet.Attributes;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.Benchmarks;

/// <summary>
/// Tick del motor (TESTING.md §3): 10 salidas en SCEL en distintas fases (pushback, rodaje, cruces, pista). Meta: 0 B
/// asignados por tick y p99 bajo 2 ms. Necesita la caché de SCEL (se genera abriendo la app con MSFS).
/// </summary>
[MemoryDiagnoser]
[InvocationCount(1)]
public class EngineTickBenchmark
{
    private TrafficEngine _engine = null!;
    private readonly FrameInfo _oneTick = new(SimClock.TickSeconds, 1, false);

    /// <summary>Cada iteración parte del mismo estado: 10 salidas escalonadas, todas todavía en tierra.</summary>
    [IterationSetup]
    public void Setup()
    {
        var airport = AirportCache.Deserialize(File.ReadAllText(new AirportCache(AppPaths.AirportCache).PathFor("SCEL")))!.Airport;
        var graph = TaxiGraph.Build(airport);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        _engine = new TrafficEngine(bridge, _ => { });
        (string, int)[] stands = [("Gate", 14), ("GateE", 3), ("Gate", 22), ("GateD", 3), ("GateC", 3), ("Gate", 25), ("GateF", 3), ("GateW", 4), ("Gate", 11), ("GateE", 22)];
        uint id = 1;
        foreach (var (name, number) in stands)
        {
            var stand = airport.Parkings.ToList().FindIndex(p => p.Name.ToString() == name && p.Number == number);
            var pk = airport.Parkings[stand];
            var agent = DepartureAgent.Plan(id++, $"B{id}", airport, graph, stand, 1, false, AircraftPerformance.MediumJet,
                DepartureTimings.Default, 2, null, 0, 36, GeoMath.Destination(pk.Position, pk.HeadingTrueDeg, 6), pk.HeadingTrueDeg)!;
            _engine.Add(agent);
            for (var t = 0; t < 300; t++)
                _engine.OnFrame(_oneTick); // escalonadas cada 30 s
        }
    }

    /// <summary>Un minuto simulado (600 ticks); el resultado se expresa por tick.</summary>
    [Benchmark(OperationsPerInvoke = 600)]
    public void Tick()
    {
        for (var t = 0; t < 600; t++)
            _engine.OnFrame(_oneTick);
    }
}
