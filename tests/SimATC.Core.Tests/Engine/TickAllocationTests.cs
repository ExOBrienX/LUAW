using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Engine;

/// <summary>TESTING.md §3: el tick del motor no asigna memoria (CLAUDE.md, rendimiento).</summary>
public class TickAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // con los IA hablando en la frecuencia (hito 4e-1)
    public void EngineTick_WithTenDepartures_AllocatesNothing(bool radio)
    {
        var airport = SimAirports.Load("SCEL.json");
        var graph = TaxiGraph.Build(airport);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        if (radio)
            engine.Radio.Frequencies = new SimATC.Core.Atc.RadioFrequencies(122200, 118100, 119700);
        var tick = new FrameInfo(SimClock.TickSeconds, 1, false);
        (string, int)[] stands = [("Gate", 14), ("GateE", 3), ("Gate", 22), ("GateD", 3), ("GateC", 3), ("Gate", 25), ("GateF", 3), ("GateW", 4), ("Gate", 11), ("GateE", 22)];
        uint id = 1;
        foreach (var (name, number) in stands)
        {
            var stand = airport.Parkings.ToList().FindIndex(p => p.Name.ToString() == name && p.Number == number);
            var pk = airport.Parkings[stand];
            engine.Add(DepartureAgent.Plan(id++, $"A{id}", airport, graph, stand, 1, false, AircraftPerformance.MediumJet,
                DepartureTimings.Default, 2, null, 0, 36, GeoMath.Destination(pk.Position, pk.HeadingTrueDeg, 6), pk.HeadingTrueDeg)!);
            for (var t = 0; t < 300; t++)
            {
                engine.OnFrame(tick);
                while (engine.Radio.Lines.TryDequeue(out _))
                {
                }
            }
        }

        // Después de calentar: ticks con aviones en todas las fases en tierra. Se cuentan los ticks que asignan algo: los
        // que asignan por un evento (un cambio de fase que se registra en el log) son pocos; uno que asignara en cada tick
        // aparecería en casi todos.
        var allocatingTicks = 0;
        var withLine = 0;
        const int ticks = 1200;
        for (var t = 0; t < ticks; t++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            engine.OnFrame(tick);
            if (GC.GetAllocatedBytesForCurrentThread() > before)
            {
                allocatingTicks++;
                if (!engine.Radio.Lines.IsEmpty)
                    withLine++;
            }
            // La interfaz vacía las líneas de radio (en la app, en su hilo); fuera de la medición.
            while (engine.Radio.Lines.TryDequeue(out _))
            {
            }
        }

        Assert.True(allocatingTicks <= ticks / 50, $"{allocatingTicks} de {ticks} ticks asignaron memoria ({withLine} con una línea de radio)");
    }
}
