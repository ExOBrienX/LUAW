using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>Plan manual (RF-29) y avisos del plan.</summary>
public class ManualPlanTests
{
    [Theory]
    [InlineData("FL240", 24000)]
    [InlineData("240", 24000)]
    [InlineData("24000", 24000)]
    [InlineData("fl350", 35000)]
    [InlineData("5000", 5000)]
    [InlineData("FL700", null)]
    [InlineData("5", null)]
    [InlineData("abc", null)]
    public void Cruise_accepts_levels_and_feet(string text, int? expected)
    {
        Assert.Equal(expected, ManualPlan.ParseCruise(text));
    }

    [Fact]
    public void Valid_fields_make_a_plan_in_upper_case()
    {
        var (plan, errors) = ManualPlan.Create("lan205", "a20n", "scel", "scie", "FL240", " gelu5b  gelus uq809 ");

        Assert.Empty(errors);
        Assert.Equal(PlanSource.Manual, plan!.Source);
        Assert.Equal("LAN205", plan.Callsign);
        Assert.Equal("SCEL", plan.Origin.Icao);
        Assert.Equal(24000, plan.CruiseAltitudeFt);
        Assert.Equal("GELU5B GELUS UQ809", plan.Route);
        Assert.Empty(plan.Fixes);
    }

    [Fact]
    public void Each_bad_field_gets_its_own_error()
    {
        var (plan, errors) = ManualPlan.Create("L", "A320XX", "SC1", "", "x", "");

        Assert.Null(plan);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void Warns_when_origin_is_not_the_loaded_airport_or_the_plan_is_old()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var plan = new FlightPlan(PlanSource.SimBrief, "TST1", "A20N", new PlanAirport("SCIE"), new PlanAirport("SCEL"), 24000, "", [])
        {
            GeneratedUtc = now.AddDays(-3),
        };

        var warnings = PlanChecks.Warnings(plan, "SCEL", now);

        Assert.Equal(3, warnings.Count); // origen distinto, viejo, sin puntos
        Assert.Empty(PlanChecks.Warnings(ManualPlan.Create("TST1", "A20N", "SCEL", "SCIE", "240", "").Plan!, "SCEL", now));
        // Indicativo sin aerolínea (solo el número de vuelo).
        Assert.Contains(PlanChecks.Warnings(ManualPlan.Create("TST1", "A20N", "SCEL", "SCIE", "240", "").Plan! with { Callsign = "878" }, "SCEL", now),
            w => w.Contains("no trae aerolínea"));
    }
}
