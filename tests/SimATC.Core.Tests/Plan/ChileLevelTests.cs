using SimATC.Core.Geo;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Niveles de crucero en Chile (AIP-CHILE ENR 1.7-3): 030°-209° impares, 210°-029° pares (ADR-077).</summary>
public class ChileLevelTests
{
    // TEST DATA: posiciones aproximadas de SCEL, SCIE (Concepción) y SCFA (Antofagasta).
    private static readonly PlanAirport Scel = new("SCEL", Position: new GeoPoint(-33.393, -70.786));
    private static readonly PlanAirport Scie = new("SCIE", Position: new GeoPoint(-36.773, -73.063));
    private static readonly PlanAirport Scfa = new("SCFA", Position: new GeoPoint(-23.444, -70.445));
    private static readonly PlanAirport Scte = new("SCTE", Position: new GeoPoint(-41.439, -73.094));

    private static FlightPlan Plan(PlanAirport from, PlanAirport to, int ft) =>
        new(PlanSource.SimBrief, "LAN205", "A20N", from, to, ft, "", []);

    [Fact]
    public void Southbound_uses_odd_levels()
    {
        Assert.Null(PlanChecks.ChileLevelWarning(Plan(Scel, Scte, 27000)));
        Assert.Contains("impares", PlanChecks.ChileLevelWarning(Plan(Scel, Scte, 28000)));
    }

    /// <summary>SCEL→SCIE va a ~209°, en el borde de las dos bandas: sin aviso (depende de la variación y de la ruta).</summary>
    [Fact]
    public void Scel_to_scie_is_on_the_border_and_not_warned()
    {
        Assert.Null(PlanChecks.ChileLevelWarning(Plan(Scel, Scie, 27000)));
        Assert.Null(PlanChecks.ChileLevelWarning(Plan(Scel, Scie, 28000)));
    }

    [Fact]
    public void Northbound_uses_even_levels()
    {
        Assert.Null(PlanChecks.ChileLevelWarning(Plan(Scel, Scfa, 36000)));
        Assert.Contains("pares", PlanChecks.ChileLevelWarning(Plan(Scel, Scfa, 35000)));
    }
}
