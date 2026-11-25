using SimATC.Core.Airports;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Airports;

public sealed class AirportProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SimATC.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static FakeSimBridge NewBridge() =>
        new(default, airports: new Dictionary<string, AirportData> { ["TEST"] = TestAirports.Small() });

    [Fact]
    public async Task GetAsync_SecondCall_ComesFromCache()
    {
        using var bridge = NewBridge();
        bridge.Start();
        var provider = new AirportProvider(bridge, new AirportCache(_dir));

        var first = await provider.GetAsync("TEST", false, TestContext.Current.CancellationToken);
        var second = await provider.GetAsync("TEST", false, TestContext.Current.CancellationToken);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, bridge.AirportLoads);
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_AsksTheSimAgain()
    {
        using var bridge = NewBridge();
        bridge.Start();
        var provider = new AirportProvider(bridge, new AirportCache(_dir));

        await provider.GetAsync("TEST", false, TestContext.Current.CancellationToken);
        var refreshed = await provider.GetAsync("TEST", true, TestContext.Current.CancellationToken);

        Assert.False(refreshed.FromCache);
        Assert.Equal(2, bridge.AirportLoads);
    }

    /// <summary>ADR-169 (KMIA 2026-11-25): con un escenario recién instalado se seguía usando el aeropuerto guardado.</summary>
    [Fact]
    public async Task GetFresh_reads_the_sim_once_per_connection_and_notices_a_new_scenery()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new AirportCache(_dir);
        using (var old = NewBridge())
        {
            old.Start();
            await new AirportProvider(old, cache).GetAsync("TEST", false, ct);
        }
        // TEST DATA: el mismo aeropuerto con un puesto menos.
        var small = TestAirports.Small();
        var changed = small with { Parkings = [.. small.Parkings.Skip(1)] };
        using var bridge = new FakeSimBridge(default, airports: new Dictionary<string, AirportData> { ["TEST"] = changed });
        bridge.Start();
        var log = new List<string>();
        var provider = new AirportProvider(bridge, cache) { Log = log.Add };
        var first = await provider.GetFreshAsync("TEST", ct);
        Assert.False(first.FromCache);
        Assert.Equal(changed.Parkings.Count, first.Airport.Parkings.Count);
        Assert.Single(log);
        Assert.True((await provider.GetFreshAsync("TEST", ct)).FromCache);
        Assert.Equal(1, bridge.AirportLoads);
        Assert.Null(AirportProvider.Differences(small, small));
    }

    [Fact]
    public async Task GetAsync_NotConnected_Throws()
    {
        using var bridge = NewBridge();
        var provider = new AirportProvider(bridge, new AirportCache(_dir));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAsync("TEST", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_UnknownAirport_Throws()
    {
        using var bridge = NewBridge();
        bridge.Start();
        var provider = new AirportProvider(bridge, new AirportCache(_dir));

        await Assert.ThrowsAsync<AirportNotFoundException>(
            () => provider.GetAsync("ZZZZ", false, TestContext.Current.CancellationToken));
    }
}
