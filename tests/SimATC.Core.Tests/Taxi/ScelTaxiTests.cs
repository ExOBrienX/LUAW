using SimATC.Core.Airports;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Taxi;

/// <summary>Pruebas sobre la caché real de SCEL leída del simulador (Data/SCEL.json, etapa 1).</summary>
public class ScelTaxiTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    public static TheoryData<int, bool> RunwayEnds()
    {
        var data = new TheoryData<int, bool>();
        for (var r = 0; r < Scel.Value.Runways.Count; r++)
        {
            data.Add(r, true);
            data.Add(r, false);
        }
        return data;
    }

    [Fact]
    public void Graph_HasNoInvalidPaths()
    {
        Assert.Equal(0, Graph.Value.InvalidPathCount);
        Assert.NotEmpty(Graph.Value.RunwayEntries);
    }

    [Theory]
    [MemberData(nameof(RunwayEnds))]
    public void EveryRunwayEnd_HasADepartureEntry(int runway, bool primaryEnd)
    {
        Assert.NotNull(Graph.Value.DepartureEntry(runway, primaryEnd));
    }

    [Theory]
    [MemberData(nameof(RunwayEnds))]
    public void FromEveryStand_ThereIsARouteToEachRunwayEnd_WithoutSteppingOnARunway(int runway, bool primaryEnd)
    {
        var g = Graph.Value;
        var hold = g.DepartureEntry(runway, primaryEnd)!.HoldNode;
        var missing = new List<string>();

        for (var i = 0; i < Scel.Value.Parkings.Count; i++)
        {
            var p = Scel.Value.Parkings[i];
            if (p.Type == ParkingType.Vehicle)
                continue;
            var route = g.FindRoute(g.ParkingNode(i), hold);
            if (route is null)
            {
                missing.Add($"{p.Name} {p.Number} ({p.Type})");
                continue;
            }
            Assert.All(route.Nodes, n => Assert.Equal(-1, g.RunwayAt(n)));
        }

        var end = primaryEnd ? Scel.Value.Runways[runway].Primary : Scel.Value.Runways[runway].Secondary;
        Assert.True(missing.Count == 0, $"Sin ruta a {end.Ident} desde: {string.Join(", ", missing)}");
    }
}
