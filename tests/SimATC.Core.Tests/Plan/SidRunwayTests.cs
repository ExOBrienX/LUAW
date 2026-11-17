using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Plan;

namespace SimATC.Core.Tests.Plan;

/// <summary>
/// SID desde el extremo de despegue (ADR-117, prueba de Marco 2026-11-09 en RKPK: todas las salidas por la 36L daban media
/// vuelta de 180° porque la transición empieza en "RW36L", el umbral). Datos reales de RKPK.
/// </summary>
public class SidRunwayTests
{
    private static readonly Lazy<AirportData> Rkpk = new(() =>
        SimAirports.Load("RKPK.json"));

    [Theory]
    [InlineData("ENGO1S")]
    [InlineData("KALO1S")]
    public void The_sid_starts_ahead_from_the_departure_end_not_back_at_the_threshold(string name)
    {
        var a = Rkpk.Value;
        var sid = a.Departures.First(d => d.Name == name);
        var end = ProcedureGeometry.FindRunwayEnd(a, "36L")!.Value;
        var shape = ProcedureGeometry.Sid(sid, a, "36L", null);
        var pts = shape.Segments.SelectMany(s => s.Points).ToList();
        // El primer tramo (2 km) va hacia adelante, no hacia el umbral que quedó atrás.
        var ahead = pts.First(p => GeoMath.DistanceM(p, end.LiftoffEnd) > 2000);
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(end.HeadingDeg, GeoMath.InitialBearingDeg(end.LiftoffEnd, ahead))) < 60,
            $"sale hacia {GeoMath.InitialBearingDeg(end.LiftoffEnd, ahead):0}° con la pista a {end.HeadingDeg:0}°");
        Assert.True(ProcedureGeometry.SidFitsRunway(sid, a, "36L"));
        Assert.False(ProcedureGeometry.SidFitsRunway(sid, a, "18R"));
    }
}
