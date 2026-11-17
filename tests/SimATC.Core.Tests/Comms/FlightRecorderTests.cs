using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Comms;

public class FlightRecorderTests
{
    private static UserAircraftState At(double lat, bool ground, double kt = 0, double alt = 1500, bool? engines = true, bool brake = false) =>
        new(new GeoPoint(lat, -70.8), alt, ground ? 0 : alt, 180, kt, kt, 0, ground, 118100, 1200, brake, false, null, null, engines);

    private static FlightRecord Blank() => new("SKU414", "A21N", "SCEL", "SCIE", "17L", "20", "11", null, null, null, default, 0, 0);

    /// <summary>
    /// Pedido de Marco (2026-11-06): el vuelo termina en el puesto con los motores apagados, con horas, distancia y altitud
    /// máxima; antes no pasaba nada y la ruta quedaba en el radar.
    /// </summary>
    [Fact]
    public void A_flight_ends_at_the_stand_with_engines_off()
    {
        var r = new FlightRecorder();
        var t0 = new DateTime(2026, 11, 6, 18, 50, 0, DateTimeKind.Utc);
        Assert.Null(r.Update(At(-33.39, true, 3), PilotPhase.PushbackApproved, t0, Blank));
        Assert.Null(r.Update(At(-33.39, true, 140), PilotPhase.TakeoffCleared, t0.AddMinutes(15), Blank));
        Assert.Null(r.Update(At(-33.5, false, 250, 10000), PilotPhase.EnRoute, t0.AddMinutes(16), Blank));
        Assert.Null(r.Update(At(-36.0, false, 400, 36000), PilotPhase.EnRoute, t0.AddMinutes(40), Blank));
        Assert.Null(r.Update(At(-36.77, false, 140, 2000), PilotPhase.ArrivalTower, t0.AddMinutes(60), Blank));
        Assert.Null(r.Update(At(-36.78, true, 120), PilotPhase.ArrivalTower, t0.AddMinutes(61), Blank));
        // En el puesto con motores encendidos todavía no termina.
        Assert.Null(r.Update(At(-36.78, true, 0), PilotPhase.Arrived, t0.AddMinutes(66), Blank));
        var done = r.Update(At(-36.78, true, 0, engines: false), PilotPhase.Arrived, t0.AddMinutes(68), Blank);
        Assert.NotNull(done);
        Assert.Equal(t0, done.OffBlockUtc);
        Assert.Equal(t0.AddMinutes(16), done.TakeoffUtc);
        Assert.Equal(t0.AddMinutes(61), done.LandingUtc);
        Assert.Equal(TimeSpan.FromMinutes(45), done.AirTime);
        Assert.Equal(TimeSpan.FromMinutes(68), done.BlockTime);
        Assert.InRange(done.DistanceNm, 140, 250);
        Assert.Equal(36000, done.MaxAltitudeFt);
        Assert.Equal("SCIE", done.Destination);
        // Una sola vez.
        Assert.Null(r.Update(At(-36.78, true, 0, engines: false), PilotPhase.Arrived, t0.AddMinutes(69), Blank));
    }

    /// <summary>ADR-089: sin puesto (Ground no encontró ninguno), apagar motores detenido después de aterrizar cierra el vuelo.</summary>
    [Fact]
    public void Engines_off_after_landing_ends_the_flight_without_a_stand()
    {
        var r = new FlightRecorder();
        var t = new DateTime(2026, 11, 6, 22, 0, 0, DateTimeKind.Utc);
        r.Update(At(-36.5, false, 140, 2000), PilotPhase.ArrivalTower, t, Blank);
        r.Update(At(-36.78, true, 100), PilotPhase.ArrivalTower, t.AddMinutes(1), Blank);
        Assert.Null(r.Update(At(-36.78, true, 0), PilotPhase.TaxiIn, t.AddMinutes(3), Blank));
        Assert.NotNull(r.Update(At(-36.78, true, 0, engines: false), PilotPhase.TaxiIn, t.AddMinutes(5), Blank));
    }

    [Fact]
    public void Without_engine_data_the_parking_brake_ends_the_flight()
    {
        var r = new FlightRecorder();
        var t = DateTime.UtcNow;
        Assert.Null(r.Update(At(-36.78, true, 0, engines: null), PilotPhase.Arrived, t, Blank));
        Assert.NotNull(r.Update(At(-36.78, true, 0, engines: null, brake: true), PilotPhase.Arrived, t.AddSeconds(1), Blank));
    }

    [Fact]
    public void The_logbook_keeps_the_newest_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simatc-flights-{Guid.NewGuid():N}.json");
        try
        {
            FlightLogbook.Add(path, Blank() with { Callsign = "A1" });
            var all = FlightLogbook.Add(path, Blank() with { Callsign = "B2" });
            Assert.Equal(["B2", "A1"], all.Select(f => f.Callsign));
            Assert.Equal(["B2", "A1"], FlightLogbook.Load(path).Select(f => f.Callsign));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
