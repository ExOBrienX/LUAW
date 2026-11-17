using SimATC.Core.Comms;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Comms;

/// <summary>Retomar el vuelo después de un cierre inesperado de LUAW (prueba de Marco 2026-11-09).</summary>
public class FlightResumeTests
{
    [Theory]
    [InlineData(PilotPhase.Parked, true, true, false, null)]
    [InlineData(PilotPhase.ClearanceGiven, true, true, false, PilotPhase.Cleared)]
    [InlineData(PilotPhase.PushbackApproved, true, true, false, PilotPhase.Cleared)]       // en el puesto: vuelve a pedirlo
    [InlineData(PilotPhase.PushbackApproved, true, false, true, PilotPhase.PushbackApproved)] // ya empujó: pide el rodaje
    [InlineData(PilotPhase.TaxiGiven, true, false, true, PilotPhase.Taxiing)]
    [InlineData(PilotPhase.HoldShortGiven, true, false, true, PilotPhase.WithTower)]
    [InlineData(PilotPhase.LinedUp, true, false, true, PilotPhase.WithTower)]
    [InlineData(PilotPhase.Taxiing, false, false, true, PilotPhase.Airborne)]                // despegó con LUAW cerrado
    [InlineData(PilotPhase.EnRoute, false, false, true, PilotPhase.EnRoute)]
    [InlineData(PilotPhase.ArrivalTower, true, false, true, PilotPhase.Landed)]             // aterrizó con LUAW cerrado
    [InlineData(PilotPhase.TaxiIn, true, false, true, PilotPhase.Landed)]
    [InlineData(PilotPhase.Arrived, true, true, true, null)]
    public void The_phase_fits_where_the_aircraft_is_now(PilotPhase saved, bool onGround, bool atStand, bool pushedBack, PilotPhase? expected) =>
        Assert.Equal(expected, FlightResume.Phase(saved, onGround, atStand, pushedBack));

    [Fact]
    public void Old_broken_or_finished_files_are_not_offered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "simatc-resume-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "flight-resume.json");
        var plan = new FlightPlan(PlanSource.Manual, "LAN878", "B789", new PlanAirport("RJTT"), new PlanAirport("SCEL"),
            39000, "DCT", []);
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            Assert.Null(FlightResume.Load(path, now));
            FlightResume.Save(path, new FlightSnapshot(plan, PilotPhase.EnRoute, null, true, null, now) { ClearedFt = 39000 });
            var back = FlightResume.Load(path, now.AddHours(1))!;
            Assert.Equal(PilotPhase.EnRoute, back.Phase);
            Assert.Equal(39000, back.ClearedFt);
            Assert.Equal("LAN878", back.Plan.Callsign);
            Assert.Null(FlightResume.Load(path, now.AddHours(FlightResume.MaxAgeH + 1)));
            File.WriteAllText(path, "{ roto");
            Assert.Null(FlightResume.Load(path, now));
            FlightResume.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
