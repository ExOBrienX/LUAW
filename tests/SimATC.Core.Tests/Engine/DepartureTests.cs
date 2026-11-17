using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;
using SimATC.Core.Tests.Airports;

namespace SimATC.Core.Tests.Engine;

public class DepartureTests
{
    private static (TrafficEngine Engine, FakeSimBridge Bridge, DepartureAgent Agent, TaxiGraph Graph) Setup()
    {
        var airport = TestAirports.Small();
        var graph = TaxiGraph.Build(airport);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        var agent = DepartureAgent.Plan(1000, "TST001", airport, graph, parkingIndex: 0, runwayIndex: 0, primaryEnd: true,
            AircraftPerformance.MediumJet, DepartureTimings.Default, engineCount: 2)!;
        bridge.Spawned[1000] = new ParkedSpawnRequest("T", "TST001", airport.Parkings[0].Position, 1000, 270, 2);
        engine.Add(agent);
        return (engine, bridge, agent, graph);
    }

    /// <summary>Corre cuadros de 1/30 s hasta que se cumpla la condición o pase el tiempo máximo (en s simulados).</summary>
    private static void Run(TrafficEngine engine, FakeSimBridge bridge, Func<bool> until, double maxSeconds, Action? eachFrame = null)
    {
        for (var t = 0.0; t < maxSeconds && !until(); t += 1.0 / 30)
        {
            bridge.RaiseFrame(new FrameInfo(1.0 / 30, 1, false));
            engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
            eachFrame?.Invoke();
        }
    }

    [Fact]
    public void Departure_GoesThroughAllPhases_AndClimbsPast3000FtAgl()
    {
        var (engine, bridge, agent, _) = Setup();
        var seen = new List<DeparturePhase>();

        Run(engine, bridge, () => agent.AglFt >= 3000, 1800, () =>
        {
            if (seen.Count == 0 || seen[^1] != agent.Phase)
                seen.Add(agent.Phase);
        });

        Assert.True(agent.AglFt >= 3000, $"quedó en {agent.Phase} a {agent.AglFt:F0} ft AGL");
        Assert.Equal(
            [DeparturePhase.DoorsClosing, DeparturePhase.BeaconOn, DeparturePhase.Pushback, DeparturePhase.PushbackComplete,
             DeparturePhase.Taxi, DeparturePhase.HoldingShort, DeparturePhase.LineUp, DeparturePhase.LinedUp,
             DeparturePhase.TakeoffRoll, DeparturePhase.Rotation, DeparturePhase.Climb],
            seen);
        Assert.True(agent.EnginesRunning);
        Assert.False(bridge.Poses[1000].OnGround);
    }

    [Fact]
    public void Only_one_departure_lines_up_on_a_runway_even_from_different_entries()
    {
        // TEST DATA: Small con una segunda entrada (D) 250 m más adentro de la pista 18. Caso EGLL (ADR-093): uno por N2W y
        // otro por S se alineaban a la vez y cada uno esperaba al otro.
        var a = TestAirports.Small();
        var points = a.TaxiPoints.ToList();
        points.Add(new TaxiPoint(TaxiPointType.Normal, HoldShortOrientation.Forward, TestAirports.At(0, 700)));     // 9: en pista
        points.Add(new TaxiPoint(TaxiPointType.HoldShort, HoldShortOrientation.Forward, TestAirports.At(75, 700))); // 10: espera D
        points.Add(new TaxiPoint(TaxiPointType.Normal, HoldShortOrientation.Forward, TestAirports.At(150, 700)));   // 11: D
        // Cada puesto junto a su entrada, para que los dos lleguen a la vez a sus puntos de espera.
        var paths = a.TaxiPaths.Where(p => p.Type is not (TaxiPathType.Runway or TaxiPathType.Parking)).ToList();
        paths.Add(new TaxiPath(TaxiPathType.Runway, 20, 0, RunwayDesignator.None, 0, 9, 0));
        paths.Add(new TaxiPath(TaxiPathType.Runway, 20, 0, RunwayDesignator.None, 9, 1, 0));
        paths.Add(new TaxiPath(TaxiPathType.Taxi, 20, 0, RunwayDesignator.None, 9, 10, 1));
        paths.Add(new TaxiPath(TaxiPathType.Taxi, 20, 0, RunwayDesignator.None, 10, 11, 1));
        paths.Add(new TaxiPath(TaxiPathType.Parking, 20, 0, RunwayDesignator.None, 4, 0, 0));
        paths.Add(new TaxiPath(TaxiPathType.Parking, 20, 0, RunwayDesignator.None, 11, 1, 0));
        var parkings = a.Parkings.ToList();
        parkings[0] = parkings[0] with { Position = TestAirports.At(260, 950) };
        parkings[1] = parkings[1] with { Position = TestAirports.At(260, 700) };
        var airport = a with { TaxiPoints = points, TaxiPaths = paths, Parkings = parkings };
        var graph = TaxiGraph.Build(airport);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        var first = DepartureAgent.Plan(1000, "TST001", airport, graph, 0, 0, true, AircraftPerformance.MediumJet,
            DepartureTimings.Default, 2, entryFilter: e => e.HoldNode == 2)!;
        var second = DepartureAgent.Plan(1001, "TST002", airport, graph, 1, 0, true, AircraftPerformance.MediumJet,
            DepartureTimings.Default, 2, entryFilter: e => e.HoldNode == 10)!;
        bridge.Spawned[1000] = new ParkedSpawnRequest("T", "TST001", airport.Parkings[0].Position, 1000, 270, 2);
        bridge.Spawned[1001] = new ParkedSpawnRequest("T", "TST002", airport.Parkings[1].Position, 1000, 270, 2);
        engine.Add(first);
        engine.Add(second);

        static bool OnRunway(DepartureAgent d) => d.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll;
        Run(engine, bridge, () => first.Phase >= DeparturePhase.Climb && second.Phase >= DeparturePhase.Climb, 2400,
            () => Assert.False(OnRunway(first) && OnRunway(second), $"los dos en la pista: {first.Phase} y {second.Phase}"));
        Assert.True(first.Phase >= DeparturePhase.Climb && second.Phase >= DeparturePhase.Climb, $"{first.Phase} / {second.Phase}");
    }

    [Fact]
    public void Ai_waits_at_the_holding_point_while_the_pilot_has_the_runway()
    {
        var (engine, bridge, agent, _) = Setup();
        Run(engine, bridge, () => agent.Phase == DeparturePhase.HoldingShort, 1800);
        Assert.Equal(DeparturePhase.HoldingShort, agent.Phase);

        // El piloto autorizado a alinearse en la misma pista (RF-26): el IA no entra.
        engine.Control.Tower.SetPilot(0, PilotTowerClearance.LineUp);
        Run(engine, bridge, () => agent.Phase != DeparturePhase.HoldingShort, 120);
        Assert.Equal(DeparturePhase.HoldingShort, agent.Phase);
        Assert.Equal(HoldKind.Runway, agent.Hold);
        Assert.Equal(0, engine.Control.Tower.PilotStatus.RunwayIndex);

        // Sin autorización del piloto, sigue su secuencia y la Torre ve la pista ocupada por él.
        engine.Control.Tower.SetPilot(0, PilotTowerClearance.None);
        Run(engine, bridge, () => agent.Phase == DeparturePhase.LinedUp, 300);
        Assert.Equal(DeparturePhase.LinedUp, agent.Phase);
        Assert.Equal("TST001", engine.Control.Tower.PilotStatus.OccupiedBy);
    }

    [Fact]
    public void Departure_DoesNotEnterTheRunwayBeforeLineUp()
    {
        var (engine, bridge, agent, graph) = Setup();
        var runway = TestAirports.Small().Runways[0];

        Run(engine, bridge, () => agent.Phase == DeparturePhase.LineUp, 1800, () =>
        {
            // Mientras rueda, nunca dentro del rectángulo de la pista (media anchura + margen).
            var p = graph.Projection.ToLocal(bridge.Poses[1000].Position);
            Assert.True(Math.Abs(p.EastM) > runway.WidthM / 2 + 5 || Math.Abs(p.NorthM) > runway.LengthM / 2,
                $"en {agent.Phase} pisó la pista en {p.EastM:F0},{p.NorthM:F0}");
        });

        Assert.Equal(DeparturePhase.LineUp, agent.Phase);
    }

    [Fact]
    public void Departure_TakesOffAlongTheRunwayHeading()
    {
        var (engine, bridge, agent, _) = Setup();

        Run(engine, bridge, () => agent.Phase == DeparturePhase.Climb, 1800);

        Assert.Equal(DeparturePhase.Climb, agent.Phase);
        Assert.Equal(0, GeoMath.AngleDiffDeg(180, agent.HeadingDeg), 1); // pista 18: rumbo 180
    }

    [Fact]
    public void Pause_FreezesTheDeparture()
    {
        var (engine, bridge, agent, _) = Setup();
        Run(engine, bridge, () => agent.Phase == DeparturePhase.Taxi, 1800);
        var before = agent.Position;

        for (var i = 0; i < 300; i++)
            engine.OnFrame(new FrameInfo(1.0 / 30, 1, Paused: true));

        Assert.Equal(before, agent.Position);
    }

    [Fact]
    public void LightsAndEngines_FollowThePhase()
    {
        var (engine, bridge, agent, _) = Setup();

        Run(engine, bridge, () => agent.Phase == DeparturePhase.Taxi, 1800);
        engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
        Assert.True(bridge.Visual[1000].Running);
        Assert.True(bridge.Visual[1000].Lights.HasFlag(LightFlags.Taxi));
        Assert.True(bridge.Visual[1000].Lights.HasFlag(LightFlags.Beacon));

        Run(engine, bridge, () => agent.Phase == DeparturePhase.TakeoffRoll, 1800);
        engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
        Assert.True(bridge.Visual[1000].Lights.HasFlag(LightFlags.Landing));
        Assert.True(bridge.Visual[1000].Lights.HasFlag(LightFlags.Strobe));
    }

    [Fact]
    public void PushbackCurve_StartsTailFirst_AndEndsWithTheNoseAlongTheTaxiDirection()
    {
        var stand = new LocalPoint(0, 0);
        var curve = new Polyline(DepartureAgent.PushbackCurve(stand, standHeadingDeg: 90, pushEnd: new LocalPoint(-45, -10), endHeadingDeg: 0));
        var f = PathFollower.StartOn(curve, reverse: true);

        Assert.InRange(f.HeadingDeg, 88.5, 91.5);          // al empezar mira al puesto (este)
        Assert.True(curve.PointAt(2).EastM < 0);           // y retrocede hacia el oeste (la cola)
        for (var i = 0; i < 5000 && !f.Arrived(curve); i++)
            f.Step(curve, SimClock.TickSeconds, AircraftPerformance.MediumJet.Ground, 3, stopAtEnd: true);
        Assert.InRange(GeoMath.AngleDiffDeg(0, f.HeadingDeg), -1, 1); // termina mirando al norte
    }

    [Fact]
    public void Pose_CarriesSignedSpeed_ForTheWheelAnimation()
    {
        var (engine, bridge, agent, _) = Setup();
        var sawPush = false;
        var sawTaxi = false;

        Run(engine, bridge, () => agent.Phase == DeparturePhase.HoldingShort, 1800, () =>
        {
            var pose = bridge.Poses[1000];
            if (agent.Phase == DeparturePhase.Pushback && agent.SpeedKt > 0.5)
            {
                Assert.True(pose.LongitudinalSpeedKt < 0, "marcha atrás la velocidad longitudinal es negativa");
                sawPush = true;
            }
            if (agent.Phase == DeparturePhase.Taxi && agent.SpeedKt > 0.5)
            {
                Assert.True(pose.LongitudinalSpeedKt > 0);
                sawTaxi = true;
            }
        });

        Assert.True(sawPush && sawTaxi);
    }

    [Fact]
    public void Tug_IsOnTheNoseGear_FromDoorsClosingUntilAfterThePushback()
    {
        var (engine, bridge, agent, graph) = Setup();
        var sawPushing = false;

        Run(engine, bridge, () => agent.Phase == DeparturePhase.Taxi, 1800, () =>
        {
            var hasTug = bridge.Tugs.TryGetValue(1000, out var tug);
            if (agent.Phase is DeparturePhase.DoorsClosing or DeparturePhase.BeaconOn or DeparturePhase.Pushback)
            {
                Assert.True(hasTug, $"sin remolcador en {agent.Phase}");
                // Enganchado a la rueda de nariz: 12 m delante del punto de referencia (jet mediano sin dato del modelo).
                var plane = graph.Projection.ToLocal(bridge.Poses[1000].Position);
                var at = graph.Projection.ToLocal(tug.Position);
                Assert.Equal(12, LocalPoint.Distance(plane, at), 1);
                sawPushing |= agent.Phase == DeparturePhase.Pushback && agent.SpeedKt > 1;
            }
        });

        Assert.True(sawPushing);

        // Después vuelve al puesto por el costado: nunca a menos de 6 m del fuselaje (de la nariz a la cola).
        var standNose = Polyline.Offset(graph.Projection.ToLocal(TestAirports.Small().Parkings[0].Position),
            TestAirports.Small().Parkings[0].HeadingTrueDeg, 12);
        var plane = agent.Position;
        var heading = agent.HeadingDeg;
        Run(engine, bridge, () => false, 90, () =>
        {
            var tug = graph.Projection.ToLocal(bridge.Tugs[1000].Position);
            if (agent.Phase != DeparturePhase.PushbackComplete)
                return; // ya rodando, el avión se va
            var fuselage = new Polyline([Polyline.Offset(plane, heading, 15), Polyline.Offset(plane, heading + 180, 20)]);
            var nearest = fuselage.PointAt(fuselage.Project(tug));
            if (LocalPoint.Distance(tug, Polyline.Offset(plane, heading, 12)) > 9) // ya se soltó y alejó de la nariz
                Assert.True(LocalPoint.Distance(tug, nearest) > 6, "el remolcador pasa muy cerca del avión");
        });
        var final = graph.Projection.ToLocal(bridge.Tugs[1000].Position);
        Assert.True(LocalPoint.Distance(final, standNose) < 1,
            $"remolcador en {final.EastM:F1},{final.NorthM:F1}; puesto {standNose.EastM:F1},{standNose.NorthM:F1}; avión al soltar {plane.EastM:F1},{plane.NorthM:F1} rumbo {heading:F0}");
    }

    [Fact]
    public void GearAndFlaps_FollowTheDeparture()
    {
        var (engine, bridge, agent, _) = Setup();
        double? flapsAtLineUp = null;
        double gearAt50Ft = -1;
        var gearAtPreviousFrame = 1.0;

        Run(engine, bridge, () => agent.AglFt >= DepartureAgent.FlapsRetractAglFt + 800, 2400, () =>
        {
            var pose = bridge.Poses[1000];
            if (agent.Phase is DeparturePhase.DoorsClosing or DeparturePhase.Pushback)
                Assert.Equal(0, pose.FlapsPercent);                       // flaps arriba en el puesto y en el pushback
            if (agent.Phase == DeparturePhase.LinedUp)
                flapsAtLineUp ??= pose.FlapsPercent;
            if (agent.OnGround)
                Assert.Equal(1, pose.GearDown);                           // en tierra, siempre abajo
            if (gearAt50Ft < 0 && agent.AglFt >= 50)
                gearAt50Ft = pose.GearDown;
            Assert.InRange(gearAtPreviousFrame - pose.GearDown, 0, 0.011); // sube de a poco, nunca de golpe ni vuelve a bajar
            gearAtPreviousFrame = pose.GearDown;
        });

        Assert.Equal(AircraftPerformance.MediumJet.TakeoffFlapsPercent, flapsAtLineUp);
        Assert.InRange(gearAt50Ft, 0.01, 1);                              // a 50 ft todavía no terminó de subir
        Assert.Equal(0, bridge.Poses[1000].GearDown);                     // arriba al pasar la altura de aceleración
        Assert.Equal(0, bridge.Poses[1000].FlapsPercent, 3);              // y los flaps recogidos
        Assert.True(bridge.Poses[1000].VerticalSpeedFpm > 1000);
    }

    [Fact]
    public void Taxi_StartsWhereThePushbackEnded_WithoutTurning()
    {
        var (engine, bridge, agent, _) = Setup();
        double? headingAtPushEnd = null;

        Run(engine, bridge, () => agent.Phase == DeparturePhase.Taxi, 1800, () =>
        {
            if (agent.Phase == DeparturePhase.PushbackComplete)
                headingAtPushEnd ??= agent.HeadingDeg;
        });
        var start = agent.HeadingDeg;
        Run(engine, bridge, () => agent.Phase != DeparturePhase.Taxi, 15); // primeros 15 s de rodaje

        Assert.NotNull(headingAtPushEnd);
        Assert.Equal(0, GeoMath.AngleDiffDeg(headingAtPushEnd.Value, start), 1);
    }

    [Fact]
    public void LineUp_EndsOnTheRunwayCenterline_WithTheRunwayHeading()
    {
        var (engine, bridge, agent, graph) = Setup();

        Run(engine, bridge, () => agent.Phase == DeparturePhase.LinedUp, 1800);

        Assert.Equal(DeparturePhase.LinedUp, agent.Phase);
        Assert.True(Math.Abs(agent.Position.EastM) < 0.5, $"quedó a {agent.Position.EastM:F2} m del eje"); // eje en E = 0
        Assert.Equal(0, GeoMath.AngleDiffDeg(180, agent.HeadingDeg), 1);
    }

    [Fact]
    public void Throttle_FollowsThePhase_WithoutJumps()
    {
        var (engine, bridge, agent, _) = Setup();
        var maxTaxi = 0.0;
        var maxStep = 0.0;
        var last = 0.0;
        var maxTakeoff = 0.0;
        var engineOffMax = 0.0;

        Run(engine, bridge, () => agent.AglFt > 2500, 1800, () =>
        {
            var t = agent.ThrottlePercent;
            maxStep = Math.Max(maxStep, Math.Abs(t - last));
            last = t;
            if (!agent.EnginesRunning)
                engineOffMax = Math.Max(engineOffMax, t);
            if (agent.Phase is DeparturePhase.Taxi or DeparturePhase.LineUp)
                maxTaxi = Math.Max(maxTaxi, t);
            if (agent.Phase is DeparturePhase.TakeoffRoll or DeparturePhase.Rotation)
                maxTakeoff = Math.Max(maxTakeoff, t);
        });

        Assert.Equal(0, engineOffMax);
        Assert.InRange(maxTaxi, DepartureAgent.TaxiCruiseThrottle, DepartureAgent.TaxiBreakawayThrottle);
        Assert.Equal(DepartureAgent.TakeoffThrottle, maxTakeoff, 1);
        Assert.Equal(DepartureAgent.ClimbThrottle, agent.ThrottlePercent, 1); // ya sobre 1500 ft: potencia de ascenso
        Assert.True(maxStep <= DepartureAgent.ThrottleDownPerS * SimClock.TickSeconds * 4 + 0.01, $"la palanca saltó {maxStep:F1} %");
        Assert.Equal(agent.ThrottlePercent, bridge.Throttle[1000], 0); // el puente recibe el valor
    }

    [Fact]
    public void Liftoff_HasNoAltitudeJump()
    {
        var (engine, bridge, agent, _) = Setup();
        double? last = null;
        var maxJump = 0.0;

        Run(engine, bridge, () => agent.AglFt > 500, 1800, () =>
        {
            if (agent.Phase < DeparturePhase.Rotation)
                return;
            var alt = bridge.Poses[1000].AltitudeFt;
            if (last is { } l)
                maxJump = Math.Max(maxJump, Math.Abs(alt - l));
            last = alt;
        });

        Assert.True(agent.AglFt > 500);
        Assert.True(maxJump < 2.0, $"saltó {maxJump:F1} ft entre cuadros");
    }

    private static (SimATC.Core.Airports.AirportData Airport, TaxiGraph Graph, int Runway, bool Primary) Scie20()
    {
        var airport = SimAirports.Load("SCIE.json");
        var r = airport.Runways.ToList().FindIndex(x => x.Primary.Ident == "20" || x.Secondary.Ident == "20");
        return (airport, TaxiGraph.Build(airport), r, airport.Runways[r].Primary.Ident == "20");
    }

    /// <summary>Planea desde el primer puesto que tenga salida y lo corre hasta que sube; devuelve dónde empezó la carrera.</summary>
    private static (DepartureAgent Agent, GeoPoint RollStart) FlyFrom(SimATC.Core.Airports.AirportData airport, TaxiGraph graph, int r, bool primary,
        Func<RunwayEntry, bool>? filter = null)
    {
        DepartureAgent? agent = null;
        var stand = -1;
        for (var i = 0; i < airport.Parkings.Count && agent is null; i++)
        {
            agent = DepartureAgent.Plan(1000, "SKU1", airport, graph, i, r, primary, AircraftPerformance.MediumJet,
                DepartureTimings.Default, engineCount: 2, entryFilter: filter);
            stand = i;
        }
        Assert.NotNull(agent);
        var bridge = new FakeSimBridge(default);
        bridge.Start();
        var engine = new TrafficEngine(bridge, _ => { });
        bridge.Spawned[1000] = new ParkedSpawnRequest("T", "SKU1", airport.Parkings[stand].Position, 1000, 0, 2);
        engine.Add(agent!);
        GeoPoint? rollStart = null;
        Run(engine, bridge, () => agent!.Phase == DeparturePhase.Climb, 3600, () =>
        {
            if (agent!.Phase == DeparturePhase.TakeoffRoll && rollStart is null)
                rollStart = agent.Pose(1).Position;
        });
        Assert.Equal(DeparturePhase.Climb, agent!.Phase);
        return (agent, rollStart!.Value);
    }

    /// <summary>
    /// ADR-085 (prueba de Marco en SCIE, "no tiene ruta de salida a esa cabecera"): para la 20 la entrada más cercana a la
    /// cabecera no conecta con las puertas; se usa la calle A (a 262 m) y despega desde ahí.
    /// </summary>
    [Fact]
    public void Departure_uses_the_nearest_reachable_entry()
    {
        var (airport, graph, r, primary) = Scie20();
        var (agent, roll) = FlyFrom(airport, graph, r, primary);
        Assert.Equal(0, agent.BacktrackM);
        var end = primary ? airport.Runways[r].Primary : airport.Runways[r].Secondary;
        Assert.True(GeoMath.DistanceM(roll, end.Position) < 400);
    }

    /// <summary>ADR-085: sin entrada cerca de la cabecera, rueda por la pista, da la vuelta y despega desde la cabecera.</summary>
    [Fact]
    public void Departure_backtracks_the_runway_when_no_entry_is_near_the_threshold()
    {
        var (airport, graph, r, primary) = Scie20();
        // Solo las entradas del medio (como un aeropuerto sin calle paralela).
        var (agent, roll) = FlyFrom(airport, graph, r, primary, e => Math.Abs(e.AlongM) < 400);
        Assert.True(agent.BacktrackM > 700, $"backtrack de {agent.BacktrackM:0} m");
        var end = primary ? airport.Runways[r].Primary : airport.Runways[r].Secondary;
        Assert.True(GeoMath.DistanceM(roll, end.Position) < 200, $"empieza a {GeoMath.DistanceM(roll, end.Position):0} m de la cabecera");
    }
}
