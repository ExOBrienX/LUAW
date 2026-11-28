using SimATC.Core.Fleet;

namespace SimATC.Core.Tests.Fleet;

public class AircraftShapeTests
{
    [Theory]
    [InlineData("C172", 11.0, 1, AircraftShape.Light)]
    [InlineData("PA28", 10.7, 1, AircraftShape.Light)]
    [InlineData("AT76", 27.1, 2, AircraftShape.Turboprop)]
    [InlineData("DH8D", 28.4, 2, AircraftShape.Turboprop)]
    [InlineData("LJ45", 14.6, 2, AircraftShape.Bizjet)]
    [InlineData("GLF6", 30.4, 2, AircraftShape.Bizjet)]
    [InlineData("C25A", 15.5, 2, AircraftShape.Bizjet)]
    [InlineData("A320", 35.8, 2, AircraftShape.Narrow)]
    [InlineData("CRJ9", 24.9, 2, AircraftShape.Narrow)]
    [InlineData("A35K", 64.8, 2, AircraftShape.Wide)]
    [InlineData("B77W", 64.8, 2, AircraftShape.Wide)]
    [InlineData("A388", 79.8, 4, AircraftShape.Quad)]
    [InlineData("B744", 64.4, 4, AircraftShape.Quad)]
    [InlineData(null, 0.0, 2, AircraftShape.Narrow)]
    public void Shape_by_type_engines_and_span(string? type, double span, int engines, AircraftShape expected) =>
        Assert.Equal(expected, AircraftShapes.Of(type, span, engines));
}
