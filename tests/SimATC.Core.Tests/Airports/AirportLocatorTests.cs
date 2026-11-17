using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;

namespace SimATC.Core.Tests.Airports;

/// <summary>En qué aeropuerto está el piloto (ADR-030).</summary>
public class AirportLocatorTests
{
    // TEST DATA: referencias aproximadas de SCEL, SCTB (Eulogio Sánchez, ~16 km al este) y SCIE.
    private static readonly NearbyAirport Scel = new("SCEL", new GeoPoint(-33.3944, -70.7938), 474);
    private static readonly NearbyAirport Sctb = new("SCTB", new GeoPoint(-33.4561, -70.5467), 795);
    private static readonly NearbyAirport Scie = new("SCIE", new GeoPoint(-36.7726, -73.0631), 4);
    private static readonly NearbyAirport[] List = [Sctb, Scie, Scel];

    [Fact]
    public void On_the_ground_picks_the_nearest_airport_within_range()
    {
        var gate = GeoMath.Destination(Scel.Position, 90, 1200); // en la plataforma
        Assert.Equal("SCEL", AirportLocator.WhereIs(gate, true, 0, List)!.Icao);
        var runwayEnd = GeoMath.Destination(Scel.Position, 177, 2000); // cabecera 35 de SCEL
        Assert.Equal("SCEL", AirportLocator.WhereIs(runwayEnd, true, 0, List)!.Icao);
    }

    [Fact]
    public void In_flight_or_far_away_there_is_no_airport()
    {
        Assert.Null(AirportLocator.WhereIs(Scel.Position, false, 3000, List)); // en vuelo sobre SCEL
        var between = GeoMath.Destination(Scel.Position, 90, 8000);
        Assert.Null(AirportLocator.WhereIs(between, true, 0, List)); // en tierra, pero lejos de todos
        Assert.Null(AirportLocator.WhereIs(Scel.Position, true, 0, []));
    }

    [Fact]
    public void Just_after_landing_counts_as_on_the_ground()
    {
        Assert.Equal("SCIE", AirportLocator.WhereIs(Scie.Position, false, 50, List)!.Icao);
    }
    [Fact]
    public void In_flight_with_a_plan_the_nearest_plan_airport_wins_at_any_distance()
    {
        // A mitad de un vuelo largo (prueba de Marco 2026-11-09: se cargaba un aeródromo chico al lado).
        var midway = GeoMath.Destination(Scel.Position, 180, 300 * 1852); // más allá de SCIE, a más de 150 NM de ambos
        var planned = new List<(string, GeoPoint)> { ("SCEL", Scel.Position), ("SCIE", Scie.Position) };
        var nearby = new List<NearbyAirport> { new("XXXX", GeoMath.Destination(midway, 90, 3000), 100) }; // TEST DATA
        Assert.Equal("SCIE", AirportLocator.Fallback(midway, planned, nearby));
        Assert.Equal("XXXX", AirportLocator.Fallback(midway, [], nearby)); // sin plan: el más cercano
        Assert.Null(AirportLocator.Fallback(GeoMath.Destination(midway, 90, 400 * 1852), [], nearby)); // sin plan y lejos de todo
    }
}
