using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;
using SimATC.Core.Units;

namespace SimATC.Core.Tests.Flight;

/// <summary>Vuelos IA en el aire (etapas 6 y 7) con los datos reales de SCEL.</summary>
public class AirborneAgentTests
{
    private const double Nm = UnitConversions.MetersPerNauticalMile;

    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    // TEST DATA: aeropuertos de destino aproximados para los vuelos de prueba.
    private static readonly GeoPoint Scie = new(-36.7727, -73.0631);
    private static readonly GeoPoint North = new(-31.4, -70.9);

    [Fact]
    public void Departure_uses_a_sid_that_heads_to_the_destination()
    {
        var (path, sid) = FlightPlanner.Departure(Scel.Value, "17L", Scie, "SCIE");
        Assert.True(path.Count > 3);
        Assert.Equal(PathSection.Sid, path[0].Section);
        Assert.Equal(PathSection.Enroute, path.End.Section);
        Assert.True(path.LengthM > 200 * Nm, $"{path.LengthM / Nm:0} NM");
        Assert.NotNull(sid);
    }

    [Fact]
    public void Landing_runway_exits_after_the_minimum_rollout()
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        Assert.InRange(l.ExitAlongM, FlightPlanner.MinRolloutM(PerformanceCategory.MediumJet), 3800);
    }

    [Theory]
    [InlineData("17R")]
    [InlineData("35L")]
    public void An_arrival_descends_follows_the_approach_and_lands(string runway)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l, North);
        var heading = GeoMath.InitialBearingDeg(North, Scel.Value.Reference);
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, heading, 420, 0, 35_000, l) { LandingCleared = true, ControlledArrival = true };
        var maxBank = 0.0;
        var minAglBeforeFinal = double.MaxValue;
        var touchdownM = double.NaN;
        var t = 0.0;
        while (a.Phase != FlightPhase.Landed && t < 3 * 3600)
        {
            a.Step(0.1);
            t += 0.1;
            if (a.Phase == FlightPhase.Rollout && double.IsNaN(touchdownM))
                touchdownM = GeoMath.DistanceM(l.Threshold, a.Position);
            var p = a.Pose(1);
            maxBank = Math.Max(maxBank, Math.Abs(p.BankDeg));
            if (a.Phase is FlightPhase.Descent or FlightPhase.Approach)
                minAglBeforeFinal = Math.Min(minAglBeforeFinal, a.AglFt);
        }
        Assert.True(a.Phase == FlightPhase.Landed, $"{a.Phase} a los {t:0} s, {a.AltitudeFt:0} ft, quedan {a.RemainingNm:0.0} NM, {a.Section}");
        Assert.True(maxBank <= 26, $"alabeo {maxBank:0}°");
        // Toca en la zona de toma (≈ 300 m, donde apunta la senda), no al principio de la pista.
        Assert.InRange(touchdownM, 200, 750);
        Assert.True(minAglBeforeFinal > 900, $"bajó a {minAglBeforeFinal:0} ft antes de la final");
        // Salió de la pista donde se eligió, a velocidad de rodaje.
        Assert.True(GeoMath.DistanceM(a.Position, a.Landing!.ExitPoint) < 60);
        Assert.InRange(a.SpeedKt, 20, 35);
    }

    [Theory]
    [InlineData("17R")]
    [InlineData("35L")]
    public void An_arrival_meets_the_chart_altitude_and_speed_constraints(string runway)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l, North);
        var constrained = Enumerable.Range(0, path.Count).Count(i => path[i].MinAltFt > 0 || path[i].MaxAltFt > 0 || path[i].SpeedMaxKt > 0);
        Assert.True(constrained >= 2, $"{constrained} puntos con restricciones");
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, GeoMath.InitialBearingDeg(North, Scel.Value.Reference), 420, 0, 35_000, l) { LandingCleared = true, ControlledArrival = true };
        var problems = new List<string>();
        var last = a.TargetIndex;
        for (var t = 0.0; t < 3 * 3600 && a.Phase != FlightPhase.Landed; t += 0.1)
        {
            a.Step(0.1);
            // Al pasar un punto (el avión apunta al siguiente), se revisa su restricción.
            for (var j = last; j < a.TargetIndex; j++)
            {
                var p = path[j];
                var ias = a.SpeedKt / (1 + 0.02 * a.AltitudeFt / 1000);
                if (p.MinAltFt > 0 && a.AltitudeFt < p.MinAltFt - 150)
                    problems.Add($"{p.Name}: {a.AltitudeFt:0} ft bajo el mínimo {p.MinAltFt:0}");
                if (p.MaxAltFt > 0 && a.AltitudeFt > p.MaxAltFt + 250)
                    problems.Add($"{p.Name}: {a.AltitudeFt:0} ft sobre el máximo {p.MaxAltFt:0}");
                if (p.SpeedMaxKt > 0 && ias > p.SpeedMaxKt + 15)
                    problems.Add($"{p.Name}: {ias:0} kt sobre {p.SpeedMaxKt:0}");
            }
            last = a.TargetIndex;
        }
        Assert.Equal(FlightPhase.Landed, a.Phase);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void A_departure_climbs_to_cruise_and_follows_its_route()
    {
        var (path, _) = FlightPlanner.Departure(Scel.Value, "17L", Scie, "SCIE");
        var start = path[0].Position;
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCEL", "SCIE", null, PerformanceCategory.MediumJet, path, start,
            Scel.Value.ElevationFt + 1500, 172, 180, 2000,
            FlightPlanner.CruiseAltitude(PerformanceCategory.MediumJet, path.LengthM / Nm), null) { OriginElevationFt = Scel.Value.ElevationFt };
        var reachedCruise = false;
        for (var t = 0.0; t < 3600 && a.Phase != FlightPhase.Finished; t += 0.1)
        {
            a.Step(0.1);
            reachedCruise |= a.Phase == FlightPhase.Cruise;
        }
        Assert.True(reachedCruise, $"{a.Phase}, {a.AltitudeFt:0} ft, quedan {a.RemainingNm:0} NM");
        Assert.Equal(FlightPhase.Finished, a.Phase); // llegó a SCIE (no es el aeropuerto cargado)
        Assert.True(GeoMath.DistanceM(a.Position, Scie) < 5 * Nm);
    }

    [Theory]
    [InlineData(0, 25, 0)]     // 25 NM al norte, hacia el sur: directo a la final de 12 NM
    [InlineData(180, 20, 0)]   // 20 NM al sur (detrás del umbral de 17R), hacia el norte: viento en cola y base
    [InlineData(270, 30, 90)]  // 30 NM al oeste, hacia el este
    public void A_nearby_live_arrival_gets_vectors_without_turning_back(double bearingFromAirport, double nm, double trackDeg)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var start = GeoMath.Destination(Scel.Value.Reference, bearingFromAirport, nm * Nm);
        var (path, star, _) = FlightPlanner.Arrival(Scel.Value, l, start, trackDeg);
        Assert.Null(star);
        var a = new AirborneAgent("LAN757", "LAN", "A20N", "SBGR", "SCEL", null, PerformanceCategory.MediumJet, path, start,
            9000, trackDeg, 280, -1000, 9000, l) { LandingCleared = true, ControlledArrival = true };
        var maxFromAirport = 0.0;
        var t = 0.0;
        while (a.Phase != FlightPhase.Landed && t < 3600)
        {
            a.Step(0.1);
            t += 0.1;
            maxFromAirport = Math.Max(maxFromAirport, GeoMath.DistanceM(a.Position, Scel.Value.Reference) / Nm);
        }
        Assert.True(a.Phase == FlightPhase.Landed, $"{a.Phase} a los {t:0} s, {a.AltitudeFt:0} ft, motor y al aire {a.GoArounds} ({a.LastGoAroundReason})");
        Assert.Equal(0, a.GoArounds);
        Assert.True(maxFromAirport < nm + 8, $"se alejó a {maxFromAirport:0} NM");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, 0)]     // desde el sur, volando al norte (prueba de Marco: la entrada de la aproximación al norte)
    [InlineData(90, 270)]
    [InlineData(270, 90)]
    public void An_arrival_from_any_side_never_makes_full_circles(double bearingFromAirport, double trackDeg)
    {
        foreach (var runway in new[] { "17L", "35R" })
        {
            var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet)!;
            var start = GeoMath.Destination(Scel.Value.Reference, bearingFromAirport, 110 * Nm);
            var (path, star, _) = FlightPlanner.Arrival(Scel.Value, l, start, trackDeg);
            var a = new AirborneAgent("LAN1152", "LAN", "B789", "SCIE", "SCEL", null, PerformanceCategory.MediumJet, path, start,
                24_000, trackDeg, 400, -1000, 24_000, l) { LandingCleared = true, ControlledArrival = true };
            var turned = 0.0;
            var last = a.HeadingDeg;
            for (var t = 0.0; t < 2 * 3600 && a.Phase != FlightPhase.Landed; t += 0.1)
            {
                a.Step(0.1);
                turned += Math.Abs(GeoMath.AngleDiffDeg(last, a.HeadingDeg));
                last = a.HeadingDeg;
            }
            Assert.True(a.Phase == FlightPhase.Landed, $"{runway} desde {bearingFromAirport}°: {a.Phase}, motor y al aire {a.GoArounds} ({a.LastGoAroundReason})");
            // Una llegada razonable gira, en total, menos de una vuelta y media (540°).
            Assert.True(turned < 540, $"{runway} desde {bearingFromAirport}° por {star ?? "vectores"}: giró {turned:0}° en total");
        }
    }

    [Theory]
    [InlineData(180, "17L")]   // SKU80: del sur a 17L (la ILS empieza al norte)
    [InlineData(0, "35R")]
    public void An_arrival_never_overflies_the_airport_before_the_final(double bearingFromAirport, string runway)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, runway, PerformanceCategory.MediumJet)!;
        var start = GeoMath.Destination(Scel.Value.Reference, bearingFromAirport, 120 * Nm);
        var (path, star, _) = FlightPlanner.Arrival(Scel.Value, l, start, GeoMath.InitialBearingDeg(start, Scel.Value.Reference));
        for (var i = 0; i < path.Count; i++)
        {
            if (path[i].Section == PathSection.Final)
                break;
            // La carta puede pasar sobre el aeropuerto (SCEL: el VOR AMB está en el aeródromo, a 10.000 ft o más); se revisan
            // los tramos que arma LUAW (vectores y unión con la aproximación).
            if (path[i].Section == PathSection.Star || path[i].IsFix)
                continue;
            var d = GeoMath.DistanceM(path[i].Position, l.Threshold) / Nm;
            Assert.True(d > 2.5 || FlightPlanner.IsOnFinal(l, path[i].Position, 1 * Nm),
                $"{star}: el punto {i} ({path[i].Name}) pasa a {d:0.0} NM del umbral, fuera de la final");
        }
    }

    [Fact]
    public void Go_around_climbs_and_comes_back_to_the_final()
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var start = GeoMath.Destination(l.Threshold, l.HeadingDeg + 180, 1 * Nm);
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet,
            FlightPlanner.Direct(start, l.Threshold, "RW17R"), start, l.ElevationFt + 300, l.HeadingDeg, 140, -700, 35_000, l)
            { ControlledArrival = true };
        a.GoAround(FlightPlanner.GoAround(l, start), l.ElevationFt + 3000);
        var t = 0.0;
        while (a.Phase != FlightPhase.Landed && t < 3600)
        {
            a.Step(0.1);
            t += 0.1;
        }
        Assert.True(a.Phase == FlightPhase.Landed, $"{a.Phase} a los {t:0} s, {a.AltitudeFt:0} ft, quedan {a.RemainingNm:0.0} NM, {a.Section}");
        Assert.Equal(1, a.GoArounds);
    }

    /// <summary>
    /// Espera en circuito (ADR-076, pedido de Marco): sobre el próximo fijo de la ruta, virajes a la derecha y alejamiento
    /// de 1 min; se queda cerca del fijo, vuelve a pasar sobre él y al salir sigue su ruta hasta aterrizar.
    /// </summary>
    [Fact]
    public void A_holding_arrival_flies_a_racetrack_over_the_fix_and_then_lands()
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l, North);
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, GeoMath.InitialBearingDeg(North, Scel.Value.Reference), 420, 0, 35_000, l) { LandingCleared = true, ControlledArrival = true };
        for (var t = 0.0; t < 120; t += 0.1)
            a.Step(0.1);
        a.Hold();
        Assert.Equal(FlightPhase.Holding, a.Phase);
        Assert.NotEqual("", a.HoldFixName);
        var fix = Enumerable.Range(0, path.Count).Select(i => path[i]).First(p => p.Name == a.HoldFixName).Position;
        var passes = 0;
        var near = false;
        var maxNm = 0.0;
        var startNm = GeoMath.DistanceM(a.Position, fix) / 1852;
        for (var t = 0.0; t < 20 * 60; t += 0.1)
        {
            a.Step(0.1);
            var nm = GeoMath.DistanceM(a.Position, fix) / 1852;
            if (t > 300)
                maxNm = Math.Max(maxNm, nm); // ya en el circuito
            if (nm < 1 && !near)
                passes++;
            near = nm < 1;
        }
        Assert.True(passes >= 3, $"pasó {passes} veces sobre {a.HoldFixName} (empezó a {startNm:0.0} NM)");
        // En altura: 1,5 min de alejamiento a ~320 kt reales y virajes anchos (inclinación máx. 25°): hasta ~14 NM del fijo.
        Assert.InRange(maxNm, 2, 16);
        a.ResumeFromHolding();
        for (var t = 0.0; t < 3 * 3600 && a.Phase != FlightPhase.Landed; t += 0.1)
            a.Step(0.1);
        Assert.Equal(FlightPhase.Landed, a.Phase);
    }

    /// <summary>El piloto en la secuencia (ADR-076): el IA detrás reduce por él; con el IA adelante, cuenta "uno antes".</summary>
    [Fact]
    public void The_pilot_is_part_of_the_arrival_sequence()
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l, North);
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, GeoMath.InitialBearingDeg(North, Scel.Value.Reference), 420, 0, 35_000, l) { ControlledArrival = true };
        var seq = new ArrivalSequencer();
        // El piloto 2 NM adelante del IA, en la misma pista.
        var ahead = new PilotArrival(l.RunwayIndex, a.RemainingM - 2 * 1852, 250, 20_000, SimATC.Core.Fleet.WakeCategory.Medium);
        seq.Update([a], (_, _) => false, ahead);
        Assert.True(a.SequenceSpeedKt > 0, "el IA de atrás no redujo por el piloto");
        Assert.Equal(0, seq.PilotAhead);
        // El piloto 20 NM detrás: el IA aterriza antes.
        var behind = ahead with { RemainingM = a.RemainingM + 20 * 1852 };
        seq.Update([a], (_, _) => false, behind);
        Assert.Equal(1, seq.PilotAhead);
        Assert.Equal(0, a.SequenceSpeedKt);
    }

    /// <summary>ADR-082 (AIP SCEL 2.1): 17L y 17R son paralelas; la demanda abre y cierra la segunda pista con histéresis.</summary>
    [Fact]
    public void Parallel_arrivals_at_scel_by_demand()
    {
        Assert.Equal("17R", ParallelRunways.PartnerOf(Scel.Value, "17L"));
        Assert.Equal("35L", ParallelRunways.PartnerOf(Scel.Value, "35R"));
        var parallel = false;
        Assert.Equal(["17L"], ParallelRunways.LandingSet(Scel.Value, ["17L"], 2, ref parallel));
        Assert.Equal(["17L", "17R"], ParallelRunways.LandingSet(Scel.Value, ["17L"], 4, ref parallel));
        Assert.Equal(["17L", "17R"], ParallelRunways.LandingSet(Scel.Value, ["17L"], 2, ref parallel)); // histéresis
        Assert.Equal(["17L"], ParallelRunways.LandingSet(Scel.Value, ["17L"], 1, ref parallel));
        // Solo 2 llegadas, pero a 3 NM una de otra: la secuencia viene comprimida y se abre la paralela.
        Assert.True(ParallelRunways.Compressed([30, 27]));
        Assert.False(ParallelRunways.Compressed([30, 15]));
        // Por tiempo estimado al umbral (ADR-131): menos de 110 s entre dos aterrizajes es demasiado junto.
        Assert.True(ParallelRunways.CompressedEta([600, 520, 900]));
        Assert.False(ParallelRunways.CompressedEta([600, 300, 900]));
        Assert.False(ParallelRunways.CompressedEta([400]));
        Assert.Equal(["17L", "17R"], ParallelRunways.LandingSet(Scel.Value, ["17L"], 2, ref parallel, compressed: true));
    }

    /// <summary>ADR-082: un IA que iba a la 17R recibe en vuelo la llegada a la 17L, sigue sin saltos y aterriza en la 17L.</summary>
    [Fact]
    public void An_arrival_is_rerouted_in_flight_to_another_runway_and_lands_there()
    {
        var l17R = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17R, North);
        var a = new AirborneAgent("LAN205", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, GeoMath.InitialBearingDeg(North, Scel.Value.Reference), 420, 0, 35_000, l17R) { LandingCleared = true, ControlledArrival = true };
        for (var t = 0.0; t < 300; t += 0.1)
            a.Step(0.1);
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (newPath, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, a.Position, a.HeadingDeg);
        Assert.True(a.Reroute(newPath, l17L));
        a.LandingCleared = true;
        var last = a.Position;
        var maxStepM = 0.0;
        for (var t = 0.0; t < 3 * 3600 && a.Phase != FlightPhase.Landed; t += 0.1)
        {
            a.Step(0.1);
            maxStepM = Math.Max(maxStepM, GeoMath.DistanceM(last, a.Position));
            last = a.Position;
        }
        Assert.Equal(FlightPhase.Landed, a.Phase);
        Assert.Equal("17L", a.Landing!.Ident);
        Assert.True(maxStepM < 30, $"salto de {maxStepM:0} m en 0,1 s");
    }

    /// <summary>
    /// ADR-085 (prueba de Marco SCEL→SCIE): un vuelo de paso que va al aeropuerto que quedó activo se toma como llegada
    /// controlada (pista, puesto, nivel retenido) y aterriza; uno que se libera ya no está controlado.
    /// </summary>
    [Fact]
    public void A_pass_through_flight_is_adopted_as_controlled_arrival_and_lands()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var a = new AirborneAgent("JAT691", "JAT", "A21N", "SCSE", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, GeoMath.InitialBearingDeg(North, Scel.Value.Reference), 420, 0, 25_000, null);
        Assert.False(a.ControlledArrival);
        for (var t = 0.0; t < 120; t += 0.1)
            a.Step(0.1);
        var (newPath, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, a.Position, a.HeadingDeg);
        Assert.True(a.Adopt(newPath, l17L, stand: 7));
        Assert.True(a.ControlledArrival);
        Assert.Equal(7, a.StandIndex);
        Assert.False(double.IsNaN(a.ClearedAltitudeFt));
        Assert.False(a.Adopt(newPath, l17L, stand: 8)); // una sola vez
        a.ClearedAltitudeFt = double.NaN;
        a.LandingCleared = true;
        for (var t = 0.0; t < 3 * 3600 && a.Phase is not (FlightPhase.Landed or FlightPhase.Finished); t += 0.1)
            a.Step(0.1);
        Assert.Equal(FlightPhase.Landed, a.Phase);

        var b = new AirborneAgent("SKU1628", "SKU", "A21N", "SCTE", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, 180, 420, 0, 35_000, l17L) { ControlledArrival = true, StandIndex = 3 };
        b.Release();
        Assert.False(b.ControlledArrival);
        Assert.Equal(-1, b.StandIndex);
    }

    /// <summary>
    /// ADR-088: un vuelo de paso que ya va por su llegada a la pista en uso, a menos de 12 NM, al activarse su aeropuerto se
    /// adopta sin cambiar su ruta (una ruta nueva desde ahí daba una vuelta de 35 NM) y aterriza; antes terminaba a 3 NM y
    /// desaparecía.
    /// </summary>
    [Fact]
    public void A_flight_on_final_is_adopted_straight_in_and_lands()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var a = new AirborneAgent("LAN301", "LAN", "A320", "SCFA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            25_000, 180, 420, 0, 35_000, l17L);
        for (var t = 0.0; t < 3 * 3600 && a.RemainingNm > 8; t += 0.1)
            a.Step(0.1);
        Assert.InRange(a.RemainingNm, 7, 8);
        var other = l17L with { ExitAlongM = l17L.ExitAlongM }; // la misma pista, leída del aeropuerto activo
        Assert.False(a.AdoptOnFinal(FlightPlanner.LandingFor(Scel.Value, Graph.Value, "35R", PerformanceCategory.MediumJet)!, 4));
        Assert.True(a.AdoptOnFinal(other, stand: 4));
        Assert.True(a.ControlledArrival);
        Assert.Equal(4, a.StandIndex);
        a.LandingCleared = true;
        var maxTurn = 0.0;
        for (var t = 0.0; t < 900 && a.Phase is not (FlightPhase.Landed or FlightPhase.Finished); t += 0.1)
        {
            a.Step(0.1);
            if (a.Phase is not (FlightPhase.Rollout or FlightPhase.Landed))
                maxTurn = Math.Max(maxTurn, Math.Abs(GeoMath.AngleDiffDeg(a.HeadingDeg, l17L.HeadingDeg)));
        }
        Assert.Equal(FlightPhase.Landed, a.Phase);
        Assert.True(maxTurn < 20, $"giró {maxTurn:0}° fuera del eje");
    }

    /// <summary>
    /// ADR-089 (pedido de Marco): con el piloto en ruta en la frecuencia del Centro, un IA en crucero cerca hace su primer
    /// contacto ahí y el Centro le responde, como en el ATC del simulador; en el descenso, el Centro lo autoriza.
    /// </summary>
    /// <summary>
    /// ADR-097: cuando la pista de llegada cambia (viento, paralelas), Approach se lo dice a la llegada IA ("expect ILS
    /// approach runway 17R") y la IA colaciona. Sin cambio de pista no se dice nada.
    /// </summary>
    [Fact]
    public void Approach_tells_an_ai_arrival_its_new_runway_and_it_reads_back()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var l17R = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var bridge = new Core.Simulation.FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        engine.Radio.Frequencies = new Core.Atc.RadioFrequencies(121_900, 118_100, 120_400); // TEST DATA
        var f = new AirborneAgent("LAN501", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            12_000, 180, 250, 0, 12_000, l17L) { ControlledArrival = true }; // en crucero: se le puede cambiar la ruta
        engine.AddFlight(f);
        var (newPath, _, _) = FlightPlanner.Arrival(Scel.Value, l17R, f.Position, f.HeadingDeg);
        engine.RequestReroute(f, newPath, l17R);
        var lines = new List<Core.Atc.RadioLine>();
        for (var t = 0.0; t < 60 && !lines.Any(l => l.Kind == Core.Atc.RadioLineKind.ReadbackExpectApproach); t += 1.0 / 10)
        {
            engine.OnFrame(new Core.Simulation.FrameInfo(0.1, 1, false));
            while (engine.Radio.Lines.TryDequeue(out var line))
                lines.Add(line);
        }

        Assert.Equal("17R", f.Landing!.Ident);
        var expect = Assert.Single(lines, l => l.Kind == Core.Atc.RadioLineKind.ExpectApproach);
        Assert.Equal("17R", expect.Runway);
        Assert.False(string.IsNullOrEmpty(expect.Detail));
        Assert.Contains(lines, l => l.Kind == Core.Atc.RadioLineKind.ReadbackExpectApproach && l.Callsign == "LAN501");
    }

    [Fact]
    public void Cruise_traffic_near_the_pilot_talks_to_his_center()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var bridge = new Core.Simulation.FakeSimBridge(new Core.Simulation.UserAircraftState(GeoMath.Destination(North, 90, 20 * UnitConversions.MetersPerNauticalMile),
            35_000, 35_000, 180, 280, 450, 0, false, 126_300, 2000));
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        engine.Radio.Frequencies = new Core.Atc.RadioFrequencies(121_900, 118_100, 120_400); // TEST DATA
        engine.Radio.EnrouteKhz = 126_300; // TEST DATA: el Centro del piloto
        bridge.Emit();
        var f = new AirborneAgent("LAN501", "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            34_000, 180, 450, 0, 34_000, l17L);
        engine.AddFlight(f);
        var lines = new List<Core.Atc.RadioLine>();
        for (var t = 0.0; t < 900 && !lines.Any(l => l.Kind == Core.Atc.RadioLineKind.ReadbackDescend); t += 1.0 / 10)
        {
            bridge.RaiseFrame(new Core.Simulation.FrameInfo(0.1, 1, false));
            engine.OnFrame(new Core.Simulation.FrameInfo(0.1, 1, false));
            while (engine.Radio.Lines.TryDequeue(out var line))
                lines.Add(line);
        }
        Assert.Contains(lines, l => l.Kind == Core.Atc.RadioLineKind.EnrouteCheckIn && l.Khz == 126_300 && l.Callsign == "LAN501");
        Assert.Contains(lines, l => l.Kind == Core.Atc.RadioLineKind.EnrouteIdentified && l.FromAtc);
        Assert.True(lines.FindIndex(l => l.Kind == Core.Atc.RadioLineKind.EnrouteCheckIn) < lines.FindIndex(l => l.Kind == Core.Atc.RadioLineKind.EnrouteIdentified));
    }

    /// <summary>
    /// ADR-090 (prueba de Marco: 4 motor y al aire en SCEL en 20 min): la separación en final cubre lo que el de adelante
    /// ocupa la pista. Con una salida lejos (3000 m) hacen falta más de 4 NM; con una cerca, alcanzan los 4 de siempre.
    /// </summary>
    [Fact]
    public void Final_spacing_covers_the_runway_occupancy_of_the_leader()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        AirborneAgent Make(double exitM) => new("LAN1", "LAN", "A320", "SCFA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            10_000, 180, 250, 0, 35_000, l17L with { ExitAlongM = exitM });
        var far = ArrivalSequencer.RotSpacingNm(Make(3000), ArrivalSequencer.RotS(Make(3000)));
        var near = ArrivalSequencer.RotSpacingNm(Make(1500), ArrivalSequencer.RotS(Make(1500)));
        Assert.True(far > ArrivalSequencer.BaseSpacingNm, $"{far:0.0} NM");
        Assert.InRange(far, 4.3, 6.5); // ADR-120: ocupación con la velocidad real de la salida
        Assert.True(near < far);
    }

    /// <summary>
    /// ADR-095 (pista mixta): con la autorización condicional la salida se alinea mientras la llegada frena, así que el hueco
    /// entre dos llegadas para meter una salida es de unas 7 NM (antes ~9, y en Manchester no salía nadie). Y el tiempo al
    /// umbral cuenta que lejos va más rápido: 24 NM a 230 kt no tardan lo mismo que 24 NM a la velocidad de final.
    /// </summary>
    [Fact]
    public void Departure_gap_on_a_mixed_runway_is_about_seven_miles_and_eta_counts_the_slowdown()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var a = new AirborneAgent("LAN1", "LAN", "A320", "SCFA", "SCEL", null, PerformanceCategory.MediumJet, path, North,
            10_000, 180, 230, 0, 35_000, l17L with { ExitAlongM = 1600 });

        Assert.InRange(ArrivalSequencer.DepartureGapNm(a, ArrivalSequencer.RotS(a)), 6, 8);
        var eta = ArrivalSequencer.EtaS(a);
        var atFinalSpeed = a.RemainingNm / 140 * 3600;
        Assert.True(eta < atFinalSpeed, $"{eta:0} s contra {atFinalSpeed:0} s a 140 kt");
        Assert.True(eta > a.RemainingNm / 250 * 3600, $"{eta:0} s");
    }

    /// <summary>ADR-085: un vuelo terminado que sigue a la vista no queda congelado: sigue recto y sube.</summary>
    [Fact]
    public void A_finished_flight_keeps_moving_instead_of_freezing()
    {
        var l17L = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17L", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l17L, North);
        var a = new AirborneAgent("JAT691", "JAT", "A21N", "SCSE", "SCIE", null, PerformanceCategory.MediumJet, path, North,
            1_000, 200, 150, 0, 35_000, null);
        a.MarkFinished();
        var start = a.Position;
        for (var t = 0.0; t < 60; t += 0.1)
            a.Step(0.1);
        Assert.True(GeoMath.DistanceM(start, a.Position) > 3000);
        Assert.True(a.AltitudeFt > 1_000);
    }

    // Vuelo de Marco a KEWR (2026-11-15): dos llegadas por la misma ruta a FL350 quedaron a 0,3 NM una de otra hasta la
    // secuencia de llegadas. Con la separación en ruta, el de atrás se queda atrás.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_flights_to_the_same_destination_on_the_same_route_get_in_trail_spacing(bool spaced)
    {
        var l = FlightPlanner.LandingFor(Scel.Value, Graph.Value, "17R", PerformanceCategory.MediumJet)!;
        var (path, _, _) = FlightPlanner.Arrival(Scel.Value, l, North);
        var heading = GeoMath.InitialBearingDeg(North, Scel.Value.Reference);
        var behind = GeoMath.Destination(North, GeoMath.NormalizeDeg(heading + 180), 0.5 * Nm);
        AirborneAgent Make(string cs, GeoPoint at) =>
            new(cs, "LAN", "A320", "SCDA", "SCEL", null, PerformanceCategory.MediumJet, path, at, 35_000, heading, 450, 0, 35_000, l);
        var lead = Make("LAN1", North);
        var follow = Make("LAN2", behind);
        var air = new List<AirborneAgent> { lead, follow };
        for (var i = 0; i < 20 * 60 * 10 && lead.Phase is FlightPhase.Cruise or FlightPhase.Descent; i++)
        {
            lead.Step(0.1);
            follow.Step(0.1);
            if (spaced && i % 20 == 0)
                EnrouteSpacing.Update(air);
        }
        var gap = follow.RemainingNm - lead.RemainingNm;
        if (spaced)
            Assert.True(gap >= EnrouteSpacing.TrailNm - 1, $"{gap:0.0} NM");
        else
            Assert.True(gap < 2, $"{gap:0.0} NM");
    }
}
