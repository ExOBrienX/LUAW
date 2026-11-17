using SimATC.Core.Airports;
using SimATC.Core.Taxi;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.Taxi;

public class TaxiGraphTests
{
    [Fact]
    public void Build_FindsRunwayEntriesWithMarkedHolds()
    {
        var g = TaxiGraph.Build(TestAirports.Small());

        Assert.Equal(2, g.RunwayEntries.Count);
        Assert.All(g.RunwayEntries, e => Assert.True(e.HoldIsMarked));
        Assert.Equal(0, g.RunwayAt(0));
        Assert.Equal(-1, g.RunwayAt(2));
        Assert.Equal(0, g.InvalidPathCount);
    }

    [Fact]
    public void DepartureEntry_PicksTheEntryNearEachRunwayEnd()
    {
        var g = TaxiGraph.Build(TestAirports.Small());

        // Pista 18/36 (rumbo 180): el extremo primario 18 está al norte → punto de espera 2.
        Assert.Equal(2, g.DepartureEntry(0, primaryEnd: true)?.HoldNode);
        Assert.Equal(3, g.DepartureEntry(0, primaryEnd: false)?.HoldNode);
    }

    [Fact]
    public void FindRoute_FromStandToHold_UsesTaxiwaysInOrder()
    {
        var g = TaxiGraph.Build(TestAirports.Small());

        var route = g.FindRoute(g.ParkingNode(0), 2);

        Assert.NotNull(route);
        Assert.Equal([g.ParkingNode(0), 7, 5, 4, 2], route.Nodes);
        Assert.Equal(["C", "A"], route.Taxiways);
    }

    [Fact]
    public void Build_JoinsCoincidentPoints()
    {
        // TEST DATA: el caso de SCEL D1/D3/D5 reducido: un puesto cuyo punto (9) coincide con la paralela (5) sin tramo.
        var a = TestAirports.Small();
        var points = a.TaxiPoints.ToList();
        points.Add(points[5] with { });                           // 9: duplicado de 5
        var paths = a.TaxiPaths.Where(p => !(p.Type == TaxiPathType.Path && p.Start == 5)).ToList();
        paths.Add(paths[0] with { Type = TaxiPathType.Path, Start = 9, End = 7, NameIndex = 0 });
        var g = TaxiGraph.Build(a with { TaxiPoints = points, TaxiPaths = paths });

        Assert.Equal(1, g.JoinedCoincidentCount);
        Assert.NotNull(g.FindRoute(g.ParkingNode(0), 2));
    }

    [Fact]
    public void Build_LinksStandWithoutItsParkingPath()
    {
        // TEST DATA: el puesto 1 sin su tramo de entrada (costura del escenario): se une al punto de rodaje más cercano (ADR-093).
        var a = TestAirports.Small();
        var paths = a.TaxiPaths.Where(p => !(p.Type == TaxiPathType.Parking && p.End == 1)).ToList();
        var g = TaxiGraph.Build(a with { TaxiPaths = paths });

        Assert.Equal(1, g.StandsLinked);
        Assert.True(g.StandReachable(1));
        Assert.NotNull(g.FindRoute(g.ParkingNode(1), 2));
        Assert.True(g.Health.Healthy);
    }

    [Fact]
    public void Build_JoinsLoosePieceWithinRepairDistance()
    {
        // TEST DATA: un tramo suelto (9-10) a 15 m de la entrada a puestos, con el puesto 1 colgando de él.
        var a = TestAirports.Small();
        var points = a.TaxiPoints.ToList();
        points.Add(new TaxiPoint(TaxiPointType.Normal, HoldShortOrientation.Forward, TestAirports.At(215, 0)));   // 9
        points.Add(new TaxiPoint(TaxiPointType.Normal, HoldShortOrientation.Forward, TestAirports.At(215, -60))); // 10
        var paths = a.TaxiPaths.Where(p => !(p.Type == TaxiPathType.Parking && p.End == 1)).ToList();
        paths.Add(new TaxiPath(TaxiPathType.Taxi, 20, 0, RunwayDesignator.None, 9, 10, 3));
        paths.Add(new TaxiPath(TaxiPathType.Parking, 20, 0, RunwayDesignator.None, 10, 1, 0));
        var g = TaxiGraph.Build(a with { TaxiPoints = points, TaxiPaths = paths });

        Assert.Equal(1, g.RepairedJoins);
        Assert.Equal(0, g.StandsLinked);
        Assert.NotNull(g.FindRoute(g.ParkingNode(1), 2));
    }

    [Fact]
    public void Build_StandFarFromTheNetworkIsUnreachable()
    {
        // TEST DATA: un puesto a ~500 m de todo, sin tramos: no se puede unir y no se asigna.
        var a = TestAirports.Small();
        var parkings = a.Parkings.ToList();
        parkings.Add(parkings[1] with { Number = 3, Position = TestAirports.At(700, 700) });
        var g = TaxiGraph.Build(a with { Parkings = parkings });

        Assert.False(g.StandReachable(2));
        Assert.True(g.StandReachable(0));
        Assert.Equal(1, g.Health.UnreachableStands);
        Assert.False(g.Health.Healthy);
    }

    [Fact]
    public void FindRoute_IgnoresVehiclePaths()
    {
        var g = TaxiGraph.Build(TestAirports.Small());

        Assert.Null(g.FindRoute(g.ParkingNode(0), 8)); // el nodo 8 solo se alcanza por un tramo de vehículos
    }

    [Fact]
    public void FindRoute_NeverStepsOnARunway()
    {
        var g = TaxiGraph.Build(TestAirports.Small());

        Assert.Null(g.FindRoute(g.ParkingNode(0), 0)); // destino en pista
        var route = g.FindRoute(2, 3); // de una cabecera a la otra: debe ir por la paralela, no por la pista
        Assert.NotNull(route);
        Assert.All(route.Nodes, n => Assert.Equal(-1, g.RunwayAt(n)));
        Assert.Equal(["A", "C", "B"], route.Taxiways);
    }
}
