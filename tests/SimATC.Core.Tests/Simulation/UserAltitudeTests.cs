using SimATC.Core.Geo;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Simulation;

/// <summary>La altitud del piloto como en el PFD: nivel sobre la altitud de transición, QNH por debajo.</summary>
public class UserAltitudeTests
{
    private static UserAircraftState At(double trueFt, double? pressureFt, double? qnhFt) =>
        new(new GeoPoint(-33, -70), trueFt, 0, 0, 0, 0, 0, false, 118100, 1000, PressureAltitudeFt: pressureFt, QnhAltitudeFt: qnhFt);

    [Theory]
    [InlineData(27252.0, 27001.0, 27080.0, 10000.0, "FL270")] // crucero: lo que mostraba el PFD en la prueba de Marco
    [InlineData(5180.0, 4950.0, 5010.0, 10000.0, "5010 ft")]  // bajo la altitud de transición: QNH
    [InlineData(1567.0, null, null, 10000.0, "1567 ft")]  // sin datos de presión: altitud real
    public void Like_the_pfd(double trueFt, double? pressureFt, double? qnhFt, double ta, string expected) =>
        Assert.Equal(expected, At(trueFt, pressureFt, qnhFt).AltitudeText(ta));
}
