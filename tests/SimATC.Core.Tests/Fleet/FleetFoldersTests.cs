using SimATC.Core.Fleet;

namespace SimATC.Core.Tests.Fleet;

/// <summary>Carpetas de modelos que agrega el usuario (Configuración → Modelos de tráfico).</summary>
public sealed class FleetFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "simatc-fleet-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void A_folder_can_be_a_container_or_a_package_itself()
    {
        // TEST DATA: carpetas vacías con nombres de paquete.
        var container = Directory.CreateDirectory(Path.Combine(_root, "MisAddons")).FullName;
        Directory.CreateDirectory(Path.Combine(container, "fsltl-traffic-base"));
        Directory.CreateDirectory(Path.Combine(container, "otro-addon"));
        var package = Directory.CreateDirectory(Path.Combine(_root, "Otro", "aig-aitraffic-oci")).FullName;

        var found = FleetScanner.FindTrafficPackages([container, package, Path.Combine(_root, "no-existe")]);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.EndsWith("fsltl-traffic-base", StringComparison.Ordinal));
        Assert.Contains(found, p => p.EndsWith("aig-aitraffic-oci", StringComparison.Ordinal));
    }
}
