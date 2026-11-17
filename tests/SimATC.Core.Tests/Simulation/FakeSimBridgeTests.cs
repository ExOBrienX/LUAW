using SimATC.Core.Geo;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Simulation;

public class FakeSimBridgeTests
{
    // TEST DATA
    private static readonly UserAircraftState Sample = new(
        new GeoPoint(-33.39, -70.79), 1555, 0, 170, 0, 0, 0, true, 118100, 2000);

    [Fact]
    public void Start_RaisesConnected_AndEmitPublishesState()
    {
        using var bridge = new FakeSimBridge(Sample);
        bool? connected = null;
        UserAircraftState? received = null;
        bridge.ConnectionChanged += c => connected = c;
        bridge.UserStateUpdated += s => received = s;

        bridge.Start();
        bridge.Emit();

        Assert.True(connected);
        Assert.True(bridge.IsConnected);
        Assert.Equal(Sample, received);
    }

    [Fact]
    public void Dispose_RaisesDisconnected()
    {
        var bridge = new FakeSimBridge(Sample);
        bool? connected = null;
        bridge.Start();
        bridge.ConnectionChanged += c => connected = c;

        bridge.Dispose();

        Assert.False(connected);
        Assert.False(bridge.IsConnected);
    }
}
