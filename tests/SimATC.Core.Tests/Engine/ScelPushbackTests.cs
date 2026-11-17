using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Engine;

/// <summary>Pushback sobre la caché real de SCEL: orientación según la pista y forma del recorrido.</summary>
public class ScelPushbackTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    private static DepartureAgent PlanFrom(string name, int number, string runwayIdent)
    {
        var a = Scel.Value;
        var stand = a.Parkings.ToList().FindIndex(p => p.Name.ToString() == name && p.Number == number);
        Assert.True(stand >= 0, $"no está {name} {number}");
        for (var r = 0; r < a.Runways.Count; r++)
        {
            foreach (var primary in new[] { true, false })
            {
                if ((primary ? a.Runways[r].Primary : a.Runways[r].Secondary).Ident != runwayIdent)
                    continue;
                return DepartureAgent.Plan(1, "TST", a, Graph.Value, stand, r, primary, AircraftPerformance.MediumJet,
                    DepartureTimings.Default, 2)!;
            }
        }
        throw new InvalidOperationException(runwayIdent);
    }

    /// <summary>Rumbo de la nariz al terminar el pushback (el avión retrocede: rumbo del recorrido + 180).</summary>
    private static double NoseAtEnd(DepartureAgent agent) => agent.PushbackPath.HeadingAt(agent.PushbackPath.Length) + 180;

    // Caso visto en el simulador: puestos al sur de la calle; hacia 35L el rodaje va al oeste y hacia 17L al este.
    // El avión debe quedar mirando hacia donde va a rodar (la cola hacia el otro lado).
    [Theory]
    [InlineData(16, "35L", 270)]
    [InlineData(20, "35L", 270)]
    [InlineData(22, "35L", 270)]
    [InlineData(16, "17L", 90)]
    [InlineData(20, "17L", 90)]
    [InlineData(22, "17L", 90)]
    public void Pushback_LeavesTheNosePointingWhereTheTaxiGoes(int gate, string runway, double expectedNose)
    {
        var agent = PlanFrom("Gate", gate, runway);

        Assert.InRange(GeoMath.AngleDiffDeg(expectedNose, NoseAtEnd(agent)), -20, 20);
        Assert.InRange(GeoMath.AngleDiffDeg(NoseAtEnd(agent), agent.TaxiPath.HeadingAt(0)), -3, 3);
    }

    // Puesto al fondo de una calle que solo sale hacia un lado: la cola va hacia el fondo y la nariz hacia la salida.
    [Theory]
    [InlineData("GateC", 1)]
    [InlineData("GateE", 2)]
    public void DeadEndStand_TurnsTheTailIntoTheDeadEnd(string name, int number)
    {
        foreach (var runway in new[] { "35L", "17L" })
        {
            var agent = PlanFrom(name, number, runway);
            // Después del pushback no hay giros en el lugar ni media vuelta: sigue derecho por donde quedó mirando.
            Assert.InRange(GeoMath.AngleDiffDeg(NoseAtEnd(agent), agent.TaxiPath.HeadingAt(0)), -3, 3);
            Assert.InRange(GeoMath.AngleDiffDeg(agent.TaxiPath.HeadingAt(0), agent.TaxiPath.HeadingAt(30)), -45, 45);
        }
    }

    [Fact]
    public void Pushback_FromGates_TurnsToOneSideOnly()
    {
        var a = Scel.Value;
        var bad = new List<string>();
        for (var i = 0; i < a.Parkings.Count; i++)
        {
            if (a.Parkings[i].Type is not (ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy))
                continue;
            for (var r = 0; r < a.Runways.Count; r++)
            {
                foreach (var primary in new[] { true, false })
                {
                    var path = DepartureAgent.Plan(1, "TST", a, Graph.Value, i, r, primary, AircraftPerformance.MediumJet,
                        DepartureTimings.Default, 2)!.PushbackPath;
                    // Giro acumulado hacia cada lado en la parte final (el arco); sin "S".
                    double left = 0, right = 0;
                    for (var s = path.Length * 0.4; s < path.Length - 1; s += 1)
                    {
                        var d = GeoMath.AngleDiffDeg(path.HeadingAt(s), path.HeadingAt(s + 1));
                        if (d > 0) right += d; else left -= d;
                    }
                    if (Math.Min(left, right) > 5)
                        bad.Add($"{a.Parkings[i].Name} {a.Parkings[i].Number}");
                }
            }
        }
        Assert.True(bad.Count == 0, $"Pushback en S: {string.Join(", ", bad.Distinct())}");
    }

    [Fact]
    public void PushbackFillet_GoesStraightBack_ThenOneArc_EndingOnTheTaxiway()
    {
        // Puesto 60 m al sur de una calle este-oeste, nariz al sur; rodaje hacia el oeste ⇒ la cola va al este.
        var stand = new LocalPoint(0, -60);
        var curve = DepartureAgent.PushbackFillet(stand, backBearingDeg: 0, ahead: new LocalPoint(-100, 0), behind: new LocalPoint(100, 0), out var r)!;

        Assert.NotNull(curve);
        Assert.Equal(DepartureAgent.PushbackTurnRadiiM[0], r);
        Assert.Equal(0, curve[1].EastM, 3);                       // primero recto hacia atrás
        Assert.Equal(0, curve[^1].NorthM, 3);                     // termina sobre la calle
        Assert.True(curve[^1].EastM > 0);                         // con la cola hacia el este
        var path = new Polyline(curve);
        Assert.Equal(0, GeoMath.AngleDiffDeg(90, path.HeadingAt(path.Length)), 1); // retrocediendo al este = nariz al oeste
    }

    [Theory]
    [InlineData("Gate", 16, "35L")]
    [InlineData("Gate", 16, "17L")]
    [InlineData("GateC", 2, "35L")]
    [InlineData("GateW", 4, "17R")]
    public void TaxiRoute_IsNamedByItsTaxiways(string name, int number, string runway)
    {
        var agent = PlanFrom(name, number, runway);

        Assert.NotEmpty(agent.Taxiways);
        Assert.All(agent.Taxiways, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        for (var i = 1; i < agent.Taxiways.Count; i++)
            Assert.NotEqual(agent.Taxiways[i - 1], agent.Taxiways[i]); // sin repetir seguidas
    }
}
