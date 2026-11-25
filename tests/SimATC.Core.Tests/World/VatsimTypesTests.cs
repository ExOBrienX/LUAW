using SimATC.Core.World;

namespace SimATC.Core.Tests.World;

/// <summary>ADR-168: el tipo de avión que cada aerolínea usa en cada ruta, aprendido de los planes de VATSIM.</summary>
public class VatsimTypesTests
{
    private static VatsimRouteStore Store()
    {
        // TEST DATA
        var s = VatsimRouteStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "vatsim-routes.json"));
        Assert.True(s.AddType("BAW209", "EGLL", "KMIA", "A388", "I"));
        Assert.True(s.AddType("BAW208", "KMIA", "EGLL", "A388", "I"));
        Assert.True(s.AddType("BAW100", "EGLL", "EGCC", "A320", "I"));
        Assert.True(s.AddType("BAW101", "EGLL", "EGPH", "A320", "I"));
        Assert.True(s.AddType("BAW102", "EGLL", "EGPF", "A320", "I"));
        return s;
    }

    [Fact]
    public void The_type_seen_on_the_route_weighs_the_most_in_both_directions()
    {
        var s = Store();
        Assert.True(s.TypeWeight("BAW", "EGLL", "KMIA", "A388") > 5 * s.TypeWeight("BAW", "EGLL", "KMIA", "B77W"));
        Assert.Equal(s.TypeWeight("BAW", "EGLL", "KMIA", "A388"), s.TypeWeight("BAW", "KMIA", "EGLL", "A388"));
        Assert.True(s.TypeWeight("BAW", "EGLL", "EDDF", "A320") > s.TypeWeight("BAW", "EGLL", "EDDF", "A388"));
        Assert.Equal(1, s.TypeWeight("DLH", "EDDF", "KMIA", "A388"));
        Assert.True(s.TypeWeight("BAW", "KMIA", ["EGLL"], "A388") > s.TypeWeight("BAW", "KMIA", ["EGLL"], "B77W"));
    }

    [Fact]
    public void The_same_flight_counts_once_and_private_callsigns_never()
    {
        var s = Store();
        Assert.False(s.AddType("BAW209", "EGLL", "KMIA", "A388", "I"));
        Assert.False(s.AddType("N123AB", "KMIA", "KORL", "C172", "I"));
        Assert.False(s.AddType("BAW210", "EGLL", "KMIA", "A388", "V"));
        Assert.False(s.AddType("BAW211", "EGLL", "KMIA", "ZZ", "I"));
    }

    [Fact]
    public void It_is_kept_for_the_next_session()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "vatsim-routes.json");
        Store().Save(path);
        Assert.Equal(Store().TypeWeight("BAW", "EGLL", "KMIA", "A388"), VatsimRouteStore.Load(path).TypeWeight("BAW", "EGLL", "KMIA", "A388"));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
