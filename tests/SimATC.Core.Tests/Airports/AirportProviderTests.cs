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
