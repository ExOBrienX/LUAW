using SimATC.Core.Comms;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Taxi;

/// <summary>Pushback del piloto (RF-21c): plan, validación, guiado del remolque y personal de tierra.</summary>
public class PilotPushbackTests
{
    private static readonly LocalPoint Stand = new(0, 0);

    [Theory]
    [InlineData(PushTurn.TailLeft, 90)]    // nariz al norte, cola a la izquierda: termina mirando al este
    [InlineData(PushTurn.TailRight, 270)]  // cola a la derecha: termina mirando al oeste
    [InlineData(PushTurn.Straight, 0)]
    public void The_nose_ends_on_the_side_opposite_to_the_tail(PushTurn turn, double endHeading)
    {
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(20, turn, 90, 25), null);
        Assert.True(plan.CanStart);
        Assert.Equal(endHeading, plan.EndHeadingDeg, 3);
        // Marcha atrás: se aleja hacia el sur (la cola).
        Assert.True(plan.Path.PointAt(10).NorthM < -9);
    }

    [Fact]
    public void Tail_left_moves_the_aircraft_to_its_left()
    {
        // Nariz al norte: la izquierda del piloto es el oeste.
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(10, PushTurn.TailLeft, 90, 25, 0), null);
        Assert.True(plan.Path.End.EastM < -20, $"terminó en {plan.Path.End}");
        Assert.Equal(10 + 25 * Math.PI / 2, plan.Path.Length, 0);
    }

    [Theory]
    [InlineData(PushTurn.TailLeft, 90, 25)]
    [InlineData(PushTurn.TailRight, 60, 30)]
    [InlineData(PushTurn.Straight, 0, 25)]
    public void Solve_finds_the_plan_that_ends_where_the_aircraft_was_dragged(PushTurn turn, double turnDeg, double radius)
    {
        // Se planea "a mano", se toma dónde y cómo termina, y Solve debe encontrar un plan que termine igual.
        var wanted = PilotPushback.Plan(Stand, 30, new PushbackParams(12, turn, turnDeg, radius, 8), null);
        var p = PilotPushback.Solve(Stand, 30, wanted.Path.End, wanted.EndHeadingDeg, radius);
        Assert.NotNull(p);
        var solved = PilotPushback.Plan(Stand, 30, p!.Value, null);
        Assert.True(LocalPoint.Distance(solved.Path.End, wanted.Path.End) < 0.5, $"terminó a {LocalPoint.Distance(solved.Path.End, wanted.Path.End):0.00} m");
        Assert.Equal(wanted.EndHeadingDeg, solved.EndHeadingDeg, 1);
        Assert.Equal(turn, p.Value.Turn);
    }

    /// <summary>
    /// Prueba de Marco 2026-11-09: el automático del piloto es el mismo pushback que haría un IA desde ese puesto hacia la
    /// pista asignada, y en todos los puestos de SCEL termina mirando hacia donde empieza el rodaje a esa pista.
    /// </summary>
    [Theory]
    [InlineData("17L")]
    [InlineData("35R")]
    public void Suggest_uses_the_ai_pushback_and_faces_the_assigned_runway(string runway)
    {
        var airport = SimAirports.Load("SCEL.json");
        var graph = TaxiGraph.Build(airport);
        var rwy = PilotTaxiRoute.RunwayIndex(airport, runway)!.Value;
        var checkedGates = 0;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var gate = airport.Parkings[i];
            if (gate.Name.ToString() != "Gate")
                continue;
            var start = graph.Projection.ToLocal(gate.Position);
            var ai = SimATC.Core.Engine.DepartureAgent.Plan(0, "AI", airport, graph, i, rwy.Index, rwy.Primary,
                SimATC.Core.Engine.AircraftPerformance.MediumJet, new SimATC.Core.Engine.DepartureTimings(), 2,
                startPosition: gate.Position, startHeadingDeg: gate.HeadingTrueDeg);
            var plan = PilotPushback.Suggest(graph, airport, start, gate.HeadingTrueDeg, runway);
            if (ai is null || plan is null)
                continue;
            checkedGates++;
            Assert.True(plan.CanStart);
            // El de los IA si termina sobre la calle y alineado (ADR-134); si no, otro que sí.
            var aiPlan = new PushbackPlan(ai.PushbackPath, GeoMath.NormalizeDeg(ai.PushbackPath.HeadingAt(ai.PushbackPath.Length) + 180),
                PushbackProblem.None, true);
            if (PilotPushback.EndsOnTaxiway(graph, aiPlan))
                Assert.Equal(ai.PushbackPath.Length, plan.Path.Length, 3);
            else
                Assert.True(PilotPushback.EndsOnTaxiway(graph, plan), $"gate {gate.Number}: el automático no termina sobre la calle");
            var route = PilotTaxiRoute.From(graph, airport, graph.Projection.ToGeo(plan.Path.End), runway, fromStand: false,
                headingDeg: plan.EndHeadingDeg)!;
            var local = route.Points.Select(graph.Projection.ToLocal).ToList();
            var first = local.Skip(1).FirstOrDefault(q => LocalPoint.Distance(q, plan.Path.End) > 15);
            if (local.Count > 1)
                Assert.True(Math.Abs(GeoMath.AngleDiffDeg(plan.EndHeadingDeg, Polyline.Bearing(plan.Path.End, first))) < 100,
                    $"{gate.Name} {gate.Number}: mira a {plan.EndHeadingDeg:0}° y el rodaje a la {runway} empieza hacia {Polyline.Bearing(plan.Path.End, first):0}°");
        }
        Assert.True(checkedGates > 10, $"solo {checkedGates} puestos");
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public void Suggest_ends_on_a_taxiway_at_scel_gates(int gateNumber)
    {
        var airport = SimAirports.Load("SCEL.json");
        var graph = TaxiGraph.Build(airport);
        var gate = airport.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == gateNumber);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = PilotPushback.Suggest(graph, airport, graph.Projection.ToLocal(gate.Position), gate.HeadingTrueDeg, "17R");
        sw.Stop();
        Assert.NotNull(plan);
        Assert.True(plan!.CanStart);
        Assert.True(plan.EndsOnTaxiway);
        Assert.True(PilotPushback.NearestTaxiSegment(graph, plan.Path.End)!.Value.DistanceM < 8);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"tardó {sw.ElapsedMilliseconds} ms");
        // Queda mirando hacia donde empieza el rodaje a la 17R (no al revés).
        var route = PilotTaxiRoute.From(graph, airport, graph.Projection.ToGeo(plan.Path.End), "17R", fromStand: false, headingDeg: plan.EndHeadingDeg)!;
        var local = route.Points.Select(graph.Projection.ToLocal).ToList();
        var first = local.Skip(1).First(q => LocalPoint.Distance(q, plan.Path.End) > 15);
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(plan.EndHeadingDeg, Polyline.Bearing(plan.Path.End, first))) < 100,
            $"mira a {plan.EndHeadingDeg:0}° y el rodaje empieza hacia {Polyline.Bearing(plan.Path.End, first):0}°");
    }

    [Fact]
    public void Solve_rejects_a_position_that_cannot_be_reached()
    {
        // Nariz al norte; terminar 5 m al este mirando al este exige un giro imposible con radio 25.
        Assert.Null(PilotPushback.Solve(Stand, 0, new LocalPoint(5, -2), 90, 25));
        // Delante de la nariz (no es un pushback).
        Assert.Null(PilotPushback.Solve(Stand, 0, new LocalPoint(0, 30), 0, 25));
    }

    [Theory]
    [InlineData(10, 90, PushbackProblem.RadiusTooSmall)]
    [InlineData(25, 200, PushbackProblem.TurnTooLarge)]
    public void Impossible_manoeuvres_are_marked(double radius, double turn, PushbackProblem expected)
    {
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(20, PushTurn.TailLeft, turn, radius), null);
        Assert.Equal(expected, plan.Problem);
        Assert.False(plan.CanStart);
    }

    [Theory]
    [InlineData(PushTurn.TailRight)]
    [InlineData(PushTurn.TailLeft)]
    public void Guide_brings_a_simple_aircraft_to_the_end_of_the_line(PushTurn turn)
    {
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(15, turn, 90, 25), null);
        var guide = new PushbackGuide(plan.Path);
        // Como el simulador (V-27): empuja a 2.4 kt fijos y gira la nariz el triple de lo que se le escribe.
        const double dt = 0.1, simGain = 3, speedMps = PushbackGuide.NativeSpeedKt * 0.514444;
        var pos = Stand;
        var heading = 0.0;
        var maxOff = 0.0;
        for (var t = 0; t < 6000; t++)
        {
            if (guide.Next(pos, heading) is not { } cmd)
                break;
            heading = GeoMath.NormalizeDeg(heading + simGain * cmd.YawRateDegS * dt);
            pos = Polyline.Offset(pos, heading + 180, speedMps * dt);
            plan.Path.ProjectWithin(pos, 0, plan.Path.Length, out var lateral);
            maxOff = Math.Max(maxOff, Math.Abs(lateral));
        }
        // Se suelta antes del final: el remolque frena en ~2.5 m.
        Assert.True(guide.Remaining <= PushbackGuide.StopAheadM, $"quedaron {guide.Remaining:0.0} m");
        Assert.True(LocalPoint.Distance(pos, plan.Path.End) < 5, $"terminó a {LocalPoint.Distance(pos, plan.Path.End):0.0} m del final");
        Assert.True(maxOff < 3, $"se salió {maxOff:0.0} m de la línea");
        Assert.True(Math.Abs(GeoMath.AngleDiffDeg(heading, plan.EndHeadingDeg)) < 12, $"rumbo final {heading:0}");
    }

    [Fact]
    public void Ground_crew_follows_the_brake_and_says_goodbye()
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")), new Random(1));
        var crew = new GroundCrew(phrases);
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(20, PushTurn.Straight, 0, 25, 0), null);

        var start = crew.Start(plan.Path, 0);
        Assert.Equal(TugAction.Attach, start.Tug);
        Assert.Empty(start.Messages);
        // El remolque se acomoda unos segundos antes de engancharse de verdad.
        Assert.Empty(crew.Tick(3, Stand, 0, parkingBrakeSet: true).Messages);
        Assert.Contains("Release parking brake", crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, parkingBrakeSet: true).Messages.Single());
        // Con el freno puesto no empuja.
        Assert.Equal(TugAction.None, crew.Tick(15, Stand, 0, parkingBrakeSet: true).Tug);
        var push = crew.Tick(16, Stand, 0, parkingBrakeSet: false);
        Assert.Equal(TugAction.Drive, push.Tug);
        Assert.Equal(0, push.Drive!.Value.YawRateDegS, 3);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        // A mitad de camino autoriza los motores; al final pide el freno.
        Assert.Contains("start engines", crew.Tick(20, new LocalPoint(0, -11), 0, false).Messages.Single());
        // Sigue guiando hasta el final (ADR-134: antes pedía el freno 8 m antes y quedaba corto).
        Assert.Equal(TugAction.Drive, crew.Tick(30, plan.Path.PointAt(plan.Path.Length - 7), 0, false).Tug);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        // En el punto de soltar: se suelta y pide el freno.
        var done = crew.Tick(32, plan.Path.PointAt(plan.Path.Length - PushbackGuide.StopAheadM + 0.2), 0, false);
        Assert.Equal(TugAction.Release, done.Tug);
        Assert.Contains("Set parking brake", done.Messages.Single());
        // Con el freno puesto, se despide.
        var bye = crew.Tick(35, plan.Path.End, 0, true);
        Assert.Contains("Have a nice flight", bye.Messages.Single());
        Assert.Equal(CrewPhase.Done, crew.Phase);
    }

    /// <summary>
    /// Prueba de Marco (2026-11-06): sin el freno puesto, el remolque del simulador seguía empujando de largo. Ahora se suelta
    /// a la distancia en que frena (~2.5 m) para que el avión quede en el punto planeado, y se despide al poner el freno.
    /// </summary>
    [Fact]
    public void Without_brake_the_tug_is_released_to_stop_on_the_planned_point()
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")), new Random(1));
        var crew = new GroundCrew(phrases);
        var plan = PilotPushback.Plan(Stand, 0, new PushbackParams(20, PushTurn.Straight, 0, 25, 0), null);
        crew.Start(plan.Path, 0);
        crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, false, true);
        crew.Tick(GroundCrew.ConnectDelayS + 1, Stand, 0, false, true);
        crew.Tick(30, plan.Path.PointAt(plan.Path.Length - 7), 0, false, true);
        Assert.Equal(CrewPhase.Pushing, crew.Phase); // sigue guiando (ADR-134)
        var release = crew.Tick(31, plan.Path.PointAt(plan.Path.Length - PushbackGuide.StopAheadM + 0.2), 0, false, true);
        Assert.Equal(TugAction.Release, release.Tug);
        Assert.Equal(CrewPhase.WaitBrakeSet, crew.Phase);
        var bye = crew.Tick(34, plan.Path.End, 0, true, false);
        Assert.Contains("Have a nice flight", bye.Messages.Single());
        Assert.Equal(CrewPhase.Done, crew.Phase);
    }

    [Fact]
    public void The_brake_always_pauses_and_the_tug_leaving_finishes_the_pushback()
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")), new Random(1));
        var path = PilotPushback.Plan(Stand, 0, new PushbackParams(30, PushTurn.Straight, 0, 25, 0), null).Path;

        // Freno a 10 m del final: pausa, como en cualquier punto (pedido de Marco).
        var crew = new GroundCrew(phrases);
        crew.Start(path, 0);
        crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, false, true);
        crew.Tick(GroundCrew.ConnectDelayS + 1, Stand, 0, false, true);
        for (var y = 1; y < 20; y++)
            crew.Tick(GroundCrew.ConnectDelayS + 1 + y * 0.5, new LocalPoint(0, -y), 0, false, true);
        crew.Tick(40, new LocalPoint(0, -20), 0, true, true);
        Assert.Equal(CrewPhase.Paused, crew.Phase);
        // Con el freno puesto, el remolque del simulador se va solo: terminado, se despide.
        var bye = crew.Tick(40 + GroundCrew.DetachGraceS + 1, new LocalPoint(0, -20), 0, true, false);
        Assert.Equal(CrewPhase.Done, crew.Phase);
        Assert.Contains("Have a nice flight", bye.Messages.Single());

        // Sin freno al final: se suelta en el punto (ADR-134) y espera el freno.
        crew = new GroundCrew(phrases);
        crew.Start(path, 0);
        crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, false, true);
        crew.Tick(GroundCrew.ConnectDelayS + 1, Stand, 0, false, true);
        for (var y = 1; y <= 26; y++)
            crew.Tick(GroundCrew.ConnectDelayS + 1 + y * 0.5, new LocalPoint(0, -y), 0, false, true);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        Assert.Equal(TugAction.Release, crew.Tick(100, new LocalPoint(0, -28), 0, false, true).Tug);
        Assert.Equal(CrewPhase.WaitBrakeSet, crew.Phase);

        // El remolque se va solo a mitad de camino: se termina ahí y se pide el freno.
        crew = new GroundCrew(phrases);
        crew.Start(path, 0);
        crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, false, true);
        crew.Tick(GroundCrew.ConnectDelayS + 1, Stand, 0, false, true);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        // Recién enganchado puede marcar false un momento: se espera.
        Assert.Equal(CrewPhase.Pushing, (crew.Tick(GroundCrew.ConnectDelayS + 2, new LocalPoint(0, -2), 0, false, false), crew.Phase).Item2);
        var left = crew.Tick(GroundCrew.ConnectDelayS + 1 + GroundCrew.DetachGraceS, new LocalPoint(0, -12), 0, false, false);
        Assert.Equal(TugAction.Release, left.Tug);
        Assert.Equal(CrewPhase.WaitBrakeSet, crew.Phase);
    }

    [Fact]
    public void Setting_the_brake_while_pushing_pauses_and_releasing_it_continues()
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")), new Random(1));
        var crew = new GroundCrew(phrases);
        crew.Start(PilotPushback.Plan(Stand, 0, PilotPushback.Default, null).Path, 0);
        crew.Tick(GroundCrew.ConnectDelayS, Stand, 0, false);
        crew.Tick(GroundCrew.ConnectDelayS + 0.5, Stand, 0, false);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        var pause = crew.Tick(GroundCrew.ConnectDelayS + 1, new LocalPoint(0, -3), 0, true);
        Assert.Equal(CrewPhase.Paused, crew.Phase);
        Assert.Contains("Release parking brake to continue", pause.Messages.Single());
        Assert.NotEqual(TugAction.Release, pause.Tug); // el remolque sigue enganchado
        var go = crew.Tick(GroundCrew.ConnectDelayS + 5, new LocalPoint(0, -3), 0, false);
        Assert.Equal(CrewPhase.Pushing, crew.Phase);
        Assert.Contains("Continuing", go.Messages.Single());
        // El botón Detener sí lo termina.
        var stop = crew.Stop(GroundCrew.ConnectDelayS + 6);
        Assert.Equal(TugAction.Release, stop.Tug);
        Assert.Contains("Stopping pushback", stop.Messages.Single());
        Assert.Equal(CrewPhase.WaitBrakeSet, crew.Phase);
    }
}
