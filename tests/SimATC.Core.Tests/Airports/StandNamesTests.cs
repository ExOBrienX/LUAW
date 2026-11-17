using SimATC.Core.Airports;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Airports;

/// <summary>Nombres de puesto (prueba de Marco 2026-11-09 en RKSI: puertas que salían como "None 225").</summary>
public class StandNamesTests
{
    // TEST DATA: puestos de ejemplo.
    private static Parking P(ParkingType type, ParkingName name, int number, ParkingName suffix = ParkingName.None) =>
        new(type, name, suffix, number, new GeoPoint(0, 0), 0, 20, []);

    [Theory]
    [InlineData(ParkingType.GateMedium, ParkingName.None, 225, ParkingName.None, "Gate 225", "225")]
    [InlineData(ParkingType.RampGaLarge, ParkingName.None, 412, ParkingName.None, "Parking 412", "412")]
    [InlineData(ParkingType.RampCargo, ParkingName.None, 7, ParkingName.None, "Cargo 7", "7")]
    [InlineData(ParkingType.GateMedium, ParkingName.Gate, 14, ParkingName.None, "Gate 14", "14")]
    [InlineData(ParkingType.GateHeavy, ParkingName.GateA, 12, ParkingName.None, "Gate A12", "A12")]
    [InlineData(ParkingType.GateMedium, ParkingName.Gate, 12, ParkingName.GateL, "Gate 12L", "12L")]
    [InlineData(ParkingType.RampGaSmall, ParkingName.NParking, 3, ParkingName.None, "N Parking 3", "3")]
    public void Names(ParkingType type, ParkingName name, int number, ParkingName suffix, string display, string spoken)
    {
        var p = P(type, name, number, suffix);
        Assert.Equal(display, p.DisplayName);
        Assert.Equal(spoken, StandNames.Spoken(p));
    }
}
