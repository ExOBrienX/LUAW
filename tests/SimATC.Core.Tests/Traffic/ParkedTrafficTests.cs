using SimATC.Core.Geo;
using SimATC.Core.Fleet;
using SimATC.Core.Simulation;
using SimATC.Core.Traffic;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.Traffic;

public class ParkedTrafficTests
{
    // TEST DATA
    private static readonly RouteList Routes = new("TEST", new RouteSource("test", "", 0, ""), true, null,
        [new RouteAirline("XYZ", "XYZ", true, false, ["A320"], ["D1"], [])]);

    private static readonly ModelMatcher Matcher =
        new([new AircraftModel("T_A320_XYZ", ModelSource.Fsltl, "A320", "XYZ", WakeCategory.Medium, 2, 34, ["GATE"], [])], []);

    [Fact]
    public async Task Populate_SpawnsOnStands_ThenClearRemovesAll()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var traffic = new ParkedTraffic();
        var ct = TestContext.Current.CancellationToken;

        var created = await traffic.PopulateAsync(bridge, TestAirports.Small(), Routes, Matcher, TrafficLimits.Default, 5, null, _ => { }, ct);

        Assert.Equal(2, created);
        Assert.Equal(2, bridge.InjectedCount);
        Assert.All(bridge.Spawned.Values, s => Assert.Equal("T_A320_XYZ", s.Title));
        Assert.Equal(2, traffic.Snapshot().Count);

        traffic.Clear(bridge);

        Assert.Equal(0, bridge.InjectedCount);
        Assert.Empty(traffic.Snapshot());
    }

    [Fact]
    public async Task Populate_ReplacesPreviousAircraft_AndRespectsGroundLimit()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var traffic = new ParkedTraffic();
        var ct = TestContext.Current.CancellationToken;

        await traffic.PopulateAsync(bridge, TestAirports.Small(), Routes, Matcher, TrafficLimits.Default, 2, null, _ => { }, ct);
        var created = await traffic.PopulateAsync(bridge, TestAirports.Small(), Routes, Matcher, new TrafficLimits(MaxGround: 1), 2, null, _ => { }, ct);

        Assert.Equal(1, created);
        Assert.Equal(1, bridge.InjectedCount);
    }
    [Fact]
    public async Task Populate_PrefersParkedAtc_AndTakesTheStandTheSimChose()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var airport = TestAirports.Small();
        // El "simulador" elige el segundo puesto, a 2 m de su centro.
        bridge.ParkedAtcPlacement = _ => (GeoMath.Destination(airport.Parkings[1].Position, 0, 2), airport.Parkings[1].HeadingTrueDeg);
        var traffic = new ParkedTraffic();

        var created = await traffic.PopulateAsync(bridge, airport, Routes, Matcher, TrafficLimits.Default, 1, null, _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, created);
        var item = Assert.Single(traffic.Snapshot());
        Assert.True(item.IsParkedAtc);
        Assert.Equal(1, item.Aircraft.ParkingIndex);
        Assert.Contains(item.ObjectId, bridge.ParkedAtc);
    }

    [Fact]
    public async Task Populate_FallsBackToNonAtc_WhenTheSimLeavesItOffAStand()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var airport = TestAirports.Small();
        bridge.ParkedAtcPlacement = _ => (GeoMath.Destination(airport.Parkings[0].Position, 90, 200), 0);
        var traffic = new ParkedTraffic();

        await traffic.PopulateAsync(bridge, airport, Routes, Matcher, TrafficLimits.Default, 1, null, _ => { },
            TestContext.Current.CancellationToken);

        var item = Assert.Single(traffic.Snapshot());
        Assert.False(item.IsParkedAtc);
        Assert.Empty(bridge.ParkedAtc);          // el Parked ATC mal ubicado se borró
        Assert.Equal(1, bridge.InjectedCount);
    }

    // ADR-172: de a uno, 95 aviones con pasarela tardaban 3 minutos (cada uno espera ~2 s a que el simulador lo ubique).
    [Fact]
    public async Task Populate_AsksParkedAtcInParallel_AndNeverRepeatsAStand()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var airport = TestAirports.Small();
        // El "simulador" tarda y deja a los dos en el mismo puesto; pedido otra vez, el segundo va al otro puesto.
        var calls = 0;
        bridge.ParkedAtcPlacement = _ => ++calls <= 2
            ? (airport.Parkings[1].Position, airport.Parkings[1].HeadingTrueDeg)
            : (airport.Parkings[0].Position, airport.Parkings[0].HeadingTrueDeg);
        bridge.ParkedAtcDelay = TimeSpan.FromMilliseconds(300);
        var traffic = new ParkedTraffic();

        var created = await traffic.PopulateAsync(bridge, airport, Routes, Matcher, TrafficLimits.Default, 2, null, _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, created);
        Assert.Equal(2, bridge.MaxParkedAtcInFlight);
        var items = traffic.Snapshot();
        Assert.Equal([0, 1], items.Select(i => i.Aircraft.ParkingIndex).Order());
        Assert.All(items, i => Assert.True(i.IsParkedAtc));
        Assert.Equal(3, calls);
        Assert.Equal(2, bridge.InjectedCount);  // el Parked ATC repetido se borró
    }

    // Visto en el simulador: estacionar más aviones borraba también los que estaban saliendo.
    [Fact]
    public async Task Populate_KeepsAircraftThatAreAlreadyDeparting_AndTheirStands()
    {
        using var bridge = new FakeSimBridge(default);
        bridge.Start();
        var airport = TestAirports.Small();
        var ct = TestContext.Current.CancellationToken;
        var departing = await bridge.SpawnParkedAsync(new ParkedSpawnRequest("T_A320_XYZ", "DEP1", airport.Parkings[0].Position,
            airport.ElevationFt, 270, 2), ct);
        var traffic = new ParkedTraffic();

        await traffic.PopulateAsync(bridge, airport, Routes, Matcher, TrafficLimits.Default, 5, null, _ => { }, ct, busyStands: new HashSet<int> { 0 }, movingCount: 1);
        await traffic.PopulateAsync(bridge, airport, Routes, Matcher, TrafficLimits.Default, 5, null, _ => { }, ct, busyStands: new HashSet<int> { 0 }, movingCount: 1);

        Assert.True(bridge.Spawned.ContainsKey(departing));                   // el que sale sigue en el simulador
        var parked = Assert.Single(traffic.Snapshot());                       // solo queda el otro puesto
        Assert.Equal(1, parked.Aircraft.ParkingIndex);
        Assert.Equal(2, bridge.InjectedCount);
    }
}
