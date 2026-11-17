using SimATC.Core.Airports;
using SimATC.Core.Atc;

namespace SimATC.Core.Tests.Atc;

/// <summary>
/// Pistas para aterrizar según las aproximaciones publicadas (ADR-129, vuelo LEMG-LEMD): en LEMD las 14 no tienen ninguna
/// (solo se despega por ellas) y a Marco se le ofrecía la 14L para aterrizar. Datos reales de LEMD.
/// </summary>
public class ArrivalRunwayTests
{
    private static readonly Lazy<AirportData> Lemd = new(() =>
        SimAirports.Load("LEMD.json"));

    [Fact]
    public void Runways_without_a_published_approach_are_not_offered_for_landing()
    {
        Assert.Equal(["18L"], RunwayPlanner.WithApproach(Lemd.Value, ["14L", "18L"]));
        // Ninguna tiene: quedan como están, y las alternativas son las del mismo sentido que sí tienen.
        IReadOnlyList<string> south = ["14L", "14R"];
        Assert.Same(south, RunwayPlanner.WithApproach(Lemd.Value, south));
        var alternatives = RunwayPlanner.ArrivalAlternatives(Lemd.Value, south);
        Assert.Equal(["18L", "18R"], alternatives.Order());
        // Las 18 ya sirven: sin alternativas.
        Assert.Empty(RunwayPlanner.ArrivalAlternatives(Lemd.Value, ["18L", "18R"]));
    }
}
