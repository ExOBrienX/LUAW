using SimATC.Core.Airports;

namespace SimATC.Core.Tests.Airports;

public sealed class AirportCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SimATC.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SaveThenLoad_SameSimVersion_RoundTrips()
    {
        var cache = new AirportCache(_dir);
        var airport = TestAirports.Small();

        cache.Save(airport, "12.2.282174");
        var loaded = cache.TryLoad("test", "12.2.282174");

        Assert.NotNull(loaded);
        // Mismo JSON = mismos datos (los records con listas no comparan por contenido).
        var a = AirportCache.Serialize(new AirportCacheFile(1, "v", DateTime.UnixEpoch, airport));
        var b = AirportCache.Serialize(new AirportCacheFile(1, "v", DateTime.UnixEpoch, loaded));
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryLoad_DifferentSimVersion_ReturnsNull()
    {
        var cache = new AirportCache(_dir);
        cache.Save(TestAirports.Small(), "12.2.282174");

        Assert.Null(cache.TryLoad("TEST", "12.3.300000"));
    }

    [Fact]
    public void TryLoad_MissingOrCorrupt_ReturnsNull()
    {
        var cache = new AirportCache(_dir);
        Assert.Null(cache.TryLoad("TEST", "x"));

        Directory.CreateDirectory(_dir);
        File.WriteAllText(cache.PathFor("TEST"), "{ esto no es json");
        Assert.Null(cache.TryLoad("TEST", "x"));
    }

    [Fact]
    public void Invalidate_RemovesFile()
    {
        var cache = new AirportCache(_dir);
        cache.Save(TestAirports.Small(), "v");

        cache.Invalidate("TEST");

        Assert.Null(cache.TryLoad("TEST", "v"));
    }

    [Fact]
    public void Serialize_WritesEnumsAsText()
    {
        var json = AirportCache.Serialize(new AirportCacheFile(1, "v", DateTime.UnixEpoch, TestAirports.Small()));

        Assert.Contains("\"HoldShort\"", json);
        Assert.Contains("\"GateMedium\"", json);
    }
}
