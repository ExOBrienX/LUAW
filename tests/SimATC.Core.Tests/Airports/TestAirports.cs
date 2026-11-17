using SimATC.Core.Airports;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Airports;

/// <summary>
/// TEST DATA: aeropuerto inventado y pequeño. NO es un aeropuerto real.
/// Una pista 18/36 de 2000 m (rumbo verdadero 180) con su centro en el origen. Una calle paralela al este, a 150 m,
/// con dos puntos de espera (uno en cada extremo) que conectan con la pista. Dos puestos al este de la paralela.
/// Un tramo de vehículos que no debe usarse.
/// </summary>
internal static class TestAirports
{
    public static readonly GeoPoint Origin = new(-10.0, -60.0);
    public static readonly LocalProjection Projection = new(Origin);

    public static GeoPoint At(double eastM, double northM) => Projection.ToGeo(new LocalPoint(eastM, northM));

    public static AirportData Small()
    {
        // Pista 18/36: extremo primario 18 al norte (se despega hacia el sur, 180°).
        var runway = Runway.FromCenter(Origin, 180, 2000, 45, 1000, 18, RunwayDesignator.None, 36, RunwayDesignator.None);

        var points = new List<TaxiPoint>
        {
            P(0, 950),       // 0: en pista, cerca del extremo 18
            P(0, -950),      // 1: en pista, cerca del extremo 36
            Hold(75, 950),   // 2: punto de espera norte
            Hold(75, -950),  // 3: punto de espera sur
            P(150, 950),     // 4: paralela norte
            P(150, 0),       // 5: paralela centro
            P(150, -950),    // 6: paralela sur
            P(200, 0),       // 7: entrada a puestos
            P(400, 400),     // 8: punto suelto de vehículos
        };

        var parkings = new List<Parking>
        {
            new(ParkingType.GateMedium, ParkingName.Gate, ParkingName.None, 1, At(260, 30), 270, 20, ["XYZ"]),
            new(ParkingType.GateMedium, ParkingName.Gate, ParkingName.None, 2, At(260, -30), 270, 20, []),
        };

        var paths = new List<TaxiPath>
        {
            Path(TaxiPathType.Runway, 0, 1, 0),
            Path(TaxiPathType.Taxi, 0, 2, 1),    // A
            Path(TaxiPathType.Taxi, 2, 4, 1),    // A
            Path(TaxiPathType.Taxi, 1, 3, 2),    // B
            Path(TaxiPathType.Taxi, 3, 6, 2),    // B
            Path(TaxiPathType.Taxi, 4, 5, 3),    // C
            Path(TaxiPathType.Taxi, 5, 6, 3),    // C
            Path(TaxiPathType.Path, 5, 7, 0),
            Path(TaxiPathType.Parking, 7, 0, 0), // a puesto 0
            Path(TaxiPathType.Parking, 7, 1, 0), // a puesto 1
            Path(TaxiPathType.Vehicle, 7, 8, 0),
        };

        return new AirportData("TEST", "Test Field", Origin, 1000, 0, 5000, 6000,
            [runway],
            [new Frequency(FrequencyType.Tower, 118100, "TEST TOWER")],
            points, parkings, paths,
            ["", "A", "B", "C"],
            [], [], []);

        static TaxiPoint P(double e, double n) => new(TaxiPointType.Normal, HoldShortOrientation.Forward, At(e, n));
        static TaxiPoint Hold(double e, double n) => new(TaxiPointType.HoldShort, HoldShortOrientation.Forward, At(e, n));
        static TaxiPath Path(TaxiPathType type, int start, int end, int name) =>
            new(type, 20, 0, RunwayDesignator.None, start, end, name);
    }
}
