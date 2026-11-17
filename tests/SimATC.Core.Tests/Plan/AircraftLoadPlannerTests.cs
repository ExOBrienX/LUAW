using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Plan;

/// <summary>Reparto del OFP en el avión (ADR-079): alas primero, el resto al central; la carga fuera de la tripulación.</summary>
public class AircraftLoadPlannerTests
{
    // TEST DATA: tanques y estaciones inventados.
    private static readonly string[] Names = ["Pilot", "Copilot", "Forward cabin", "Aft cabin", "Forward cargo", "Aft cargo"];

    [Fact]
    public void Small_fuel_goes_to_the_wings_only_and_payload_skips_the_crew()
    {
        var p = AircraftLoadPlanner.Plan(new AircraftLoadRequest(6700 * 2, 4000, "CC-BHA", "SKU", "414"), 6, Names, 2000, 2000, 2000, 6.7);
        Assert.Equal(1000, p.LeftGal, 3);
        Assert.Equal(1000, p.RightGal, 3);
        Assert.Equal(0, p.CenterGal, 3);
        Assert.Null(p.StationLbs[0]);
        Assert.Null(p.StationLbs[1]);
        Assert.Equal(1000, p.StationLbs[2]!.Value, 3);
    }

    [Fact]
    public void Extra_fuel_goes_to_the_center_and_overflow_is_reported()
    {
        var p = AircraftLoadPlanner.Plan(new AircraftLoadRequest(6.7 * 7000, 0, null, null, null), 0, [], 2000, 2000, 2000, 6.7);
        Assert.Equal(2000, p.LeftGal, 3);
        Assert.Equal(2000, p.CenterGal, 3);
        Assert.Contains("no cabe", p.Summary);
    }
}
