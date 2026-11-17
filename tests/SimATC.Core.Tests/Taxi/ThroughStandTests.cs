using SimATC.Core.Airports;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Taxi;

/// <summary>
/// Las rutas de rodaje no atraviesan puestos de estacionamiento (ADR-128). En LEMG, 54 puestos tienen dos enlaces a la red
/// (se entra por un lado y se sale por el otro) y las rutas los usaban de atajo: en el Headless `lemg-busy`, las salidas
/// cruzaban la plataforma de aviación general en línea recta por encima de los puestos ("A-C-A-B-C-A-B-C-A-Q…") y pasaban
/// a 24-35 m de otros aviones. Datos reales de LEMG.
/// </summary>
public class ThroughStandTests
{
    private static readonly Lazy<AirportData> Lemg = new(() =>
        SimAirports.Load("LEMG.json"));
    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Lemg.Value));

    [Fact]
    public void No_route_from_a_stand_to_a_runway_hold_goes_through_another_stand()
    {
        var g = Graph.Value;
        var holds = g.RunwayEntries.Select(e => e.HoldNode).Distinct().ToList();
        var through = new List<string>();
        var routes = 0;
        for (var i = 0; i < Lemg.Value.Parkings.Count; i++)
        {
            foreach (var hold in holds)
            {
                if (g.FindRoute(g.ParkingNode(i), hold) is not { } route)
                    continue;
                routes++;
                if (route.Nodes.Skip(1).SkipLast(1).Any(g.IsParking))
                    through.Add($"{Lemg.Value.Parkings[i].Number}→{hold} ({string.Join("-", route.Taxiways)})");
            }
        }
        Assert.True(routes > 500, $"solo {routes} rutas");
        Assert.True(through.Count == 0, $"{through.Count} de {routes} rutas pasan por un puesto: {string.Join("; ", through.Take(5))}");
    }

    /// <summary>Lo mismo de vuelta: de la pista al puesto (las llegadas).</summary>
    [Fact]
    public void No_route_from_a_runway_exit_to_a_stand_goes_through_another_stand()
    {
        var g = Graph.Value;
        var exits = g.RunwayEntries.Select(e => e.EntryNode).Distinct().ToList();
        var through = 0;
        var routes = 0;
        for (var i = 0; i < Lemg.Value.Parkings.Count; i++)
        {
            foreach (var exit in exits)
            {
                if (g.FindRoute(exit, g.ParkingNode(i)) is not { } route)
                    continue;
                routes++;
                if (route.Nodes.Skip(1).SkipLast(1).Any(g.IsParking))
                    through++;
            }
        }
        Assert.True(routes > 500, $"solo {routes} rutas");
        Assert.Equal(0, through);
    }

    /// <summary>
    /// El último trecho de una llegada (del último cruce a su puesto) queda en sus marcas, para que el coordinador lo reserve
    /// (ADR-128). Antes el último cruce no llevaba tramos y una salida podía empujar o rodar de frente por esa calle:
    /// `lemg-busy`, la llegada doblaba a su puesto a 22 m de una salida detenida.
    /// </summary>
    [Fact]
    public void The_last_stretch_of_an_arrival_to_its_stand_is_part_of_its_marks()
    {
        var g = Graph.Value;
        var checkedRoutes = 0;
        foreach (var exit in g.RunwayEntries.Select(e => e.EntryNode).Distinct().Take(6))
        {
            for (var i = 0; i < Lemg.Value.Parkings.Count; i += 9)
            {
                if (g.FindRoute(exit, g.ParkingNode(i)) is not { } route || route.Nodes.Count < 4)
                    continue;
                var taxi = SimATC.Core.Engine.Polyline.Smooth([.. route.Nodes.Select(g.Position)], 20);
                var plain = SimATC.Core.Engine.DepartureAgent.TaxiMarks(g, taxi, taxi.Length);
                var withTail = SimATC.Core.Engine.DepartureAgent.TaxiMarks(g, taxi, taxi.Length, tailEdges: true);
                if (plain.Count == 0 || taxi.Length - plain[^1].S < 30)
                    continue;
                checkedRoutes++;
                Assert.Empty(plain[^1].Edges);
                Assert.NotEmpty(withTail[^1].Edges);
                // Lo demás no cambia.
                Assert.Equal(plain.Count, withTail.Count);
                for (var k = 0; k + 1 < plain.Count; k++)
                    Assert.Equal(plain[k].Edges, withTail[k].Edges);
            }
        }
        Assert.True(checkedRoutes > 10, $"solo {checkedRoutes} rutas");
    }
}
