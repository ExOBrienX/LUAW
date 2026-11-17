using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Plan;

/// <summary>Comparación "tu avión vs OFP" del briefing.</summary>
public class BriefingCheckTests
{
    private static readonly GeoPoint Scel = new(-33.3944, -70.7938); // TEST DATA

    // TEST DATA: valores con la forma del OFP real (kg).
    private static FlightPlan Plan() => new(PlanSource.SimBrief, "TST205", "A20N", new PlanAirport("SCEL", "", Scel, "17L"),
        new PlanAirport("SCIE"), 24000, "", [])
    {
        Registration = "CC-TST",
        Briefing = new Briefing("kgs")
        {
            FuelRamp = 4396, FuelTaxi = 230, FuelMinTakeoff = 4166,
            Zfw = 54272, MaxZfw = 62800, Tow = 58438, MaxTow = 68003,
        },
    };

    private static UserAircraftLoad Load(double fuelKg, double totalKg, string atcId = "CCTST", string title = "Airbus A320neo A20N") =>
        new(fuelKg / BriefingCheck.KgPerLb, totalKg / BriefingCheck.KgPerLb, 0, 0, atcId, "", "", title);

    [Fact]
    public void Matching_aircraft_is_all_ok()
    {
        var rows = BriefingCheck.Compare(Plan(), Load(4396, 58668), GeoMath.Destination(Scel, 90, 800));
        Assert.All(rows, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public void Too_little_fuel_warns_with_the_minimum()
    {
        var rows = BriefingCheck.Compare(Plan(), Load(3000, 57272), Scel);
        var fuel = rows.First(r => r.Item == "Combustible a bordo");
        Assert.Equal(CheckStatus.Warn, fuel.Status);
        Assert.Contains("mínimo", fuel.Note);
    }

    [Fact]
    public void Extra_fuel_is_information_and_overweight_is_a_warning()
    {
        var rows = BriefingCheck.Compare(Plan(), Load(5400, 69000), Scel);
        Assert.Equal(CheckStatus.Info, rows.First(r => r.Item == "Combustible a bordo").Status);
        var total = rows.First(r => r.Item == "Peso total");
        Assert.Equal(CheckStatus.Warn, total.Status);
        Assert.Contains("máximo", total.Note);
    }

    [Fact]
    public void Unknown_type_registration_or_position_are_reported()
    {
        var rows = BriefingCheck.Compare(Plan(), Load(4396, 58668, "N123AB", "Boeing 737"), GeoMath.Destination(Scel, 0, 50_000));
        Assert.Equal(CheckStatus.Info, rows.First(r => r.Item == "Tipo de avión").Status);
        Assert.Equal(CheckStatus.Info, rows.First(r => r.Item == "Matrícula").Status);
        Assert.Equal(CheckStatus.Warn, rows.First(r => r.Item == "Posición").Status);
    }

    [Fact]
    public void Manual_plan_has_nothing_to_compare()
    {
        var manual = ManualPlan.Create("TST1", "A20N", "SCEL", "SCIE", "240", "").Plan!;
        Assert.Empty(BriefingCheck.Compare(manual, Load(1000, 50000), Scel));
    }

    [Fact]
    public void In_flight_the_position_is_not_a_warning()
    {
        var rows = BriefingCheck.Compare(Plan(), Load(4396, 58668), GeoMath.Destination(Scel, 90, 96000), onGround: false);
        var pos = rows.Single(r => r.Item == "Posición");
        Assert.Equal(CheckStatus.Info, pos.Status);
        Assert.StartsWith("en vuelo", pos.Actual);
    }
}
