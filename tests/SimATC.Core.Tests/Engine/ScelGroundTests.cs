using SimATC.Core.Atc;
using SimATC.Core.Airports;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Engine;

/// <summary>Tráfico coordinado en tierra (RF-18, hito 3b) sobre la caché real de SCEL.</summary>
public class ScelGroundTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    private sealed class Harness
    {
        public readonly FakeSimBridge Bridge = new(default);
        public readonly TrafficEngine Engine;
        public readonly List<DepartureAgent> Agents = [];
        public readonly List<string> Logs = [];
        public double Time;

        /// <summary>Grafo de este arnés: el compartido o uno propio (con sentidos de circulación, ADR-067).</summary>
        public TaxiGraph Graph = ScelGroundTests.Graph.Value;

        public Harness()
        {
            Bridge.Start();
            Engine = new TrafficEngine(Bridge, Logs.Add);
        }

        public DepartureAgent Add(string name, int number, string runway, AircraftPerformance? perf = null,
            WakeCategory wake = WakeCategory.Medium, double wingspanM = 36, double startAheadM = 0, double noseGearM = 0)
        {
            var a = Scel.Value;
            var stand = a.Parkings.ToList().FindIndex(p => p.Name.ToString() == name && p.Number == number);
            var (r, primary) = RunwayEnd(runway);
            var parking = a.Parkings[stand];
            // Como el Parked ATC: el simulador lo deja unos metros adelante del centro del puesto.
            var start = GeoMath.Destination(parking.Position, parking.HeadingTrueDeg, startAheadM);
            var agent = DepartureAgent.Plan((uint)(1000 + Agents.Count), $"T{Agents.Count + 1:00}", a, Graph, stand, r, primary,
                perf ?? AircraftPerformance.MediumJet, DepartureTimings.Default, 2, null, noseGearM, wingspanM,
                start, parking.HeadingTrueDeg, wake)!;
            Agents.Add(agent);
            Engine.Add(agent);
            return agent;
        }

        /// <summary>Avanza en cuadros de 1/30 s; <paramref name="each"/> corre en cada cuadro.</summary>
        public void Run(double seconds, Action? each = null, Func<bool>? until = null)
        {
            for (var t = 0.0; t < seconds && until?.Invoke() != true; t += 1.0 / 30)
            {
                Engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
                Time += 1.0 / 30;
                each?.Invoke();
            }
        }
    }

    private static (int Runway, bool Primary) RunwayEnd(string ident)
    {
        var a = Scel.Value;
        for (var r = 0; r < a.Runways.Count; r++)
        {
            if (a.Runways[r].Primary.Ident == ident)
                return (r, true);
            if (a.Runways[r].Secondary.Ident == ident)
                return (r, false);
        }
        throw new ArgumentException(ident);
    }

    private static bool OnRunway(DepartureAgent a) =>
        a.Phase is DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll or DeparturePhase.Rotation;

    private static bool Moving(DepartureAgent a) =>
        a.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete or DeparturePhase.Taxi or DeparturePhase.HoldingShort
            or DeparturePhase.LineUp or DeparturePhase.LinedUp or DeparturePhase.TakeoffRoll;

    [Fact]
    public void ThreeDepartures_InARow_KeepDistance_UseTheRunwayOneAtATime_AndAllTakeOff()
    {
        var h = new Harness();
        var minGap = double.MaxValue;
        var where = "";
        var takeoffs = new List<(string Callsign, double Time)>();
        var seenPhase = new Dictionary<DepartureAgent, DeparturePhase>();

        h.Add("Gate", 16, "35L");
        h.Run(40);
        h.Add("Gate", 18, "35L");
        h.Run(40);
        h.Add("Gate", 20, "35L");
        h.Run(2400, () =>
        {
            // Nunca dos en la pista a la vez.
            Assert.True(h.Agents.Count(OnRunway) <= 1, "dos aviones en la pista");
            // Distancia entre aviones que se mueven en tierra (referencia a referencia).
            for (var i = 0; i < h.Agents.Count; i++)
            for (var j = i + 1; j < h.Agents.Count; j++)
            {
                var a = h.Agents[i];
                var b = h.Agents[j];
                if (!Moving(a) || !Moving(b) || a.Phase == DeparturePhase.TakeoffRoll || b.Phase == DeparturePhase.TakeoffRoll)
                    continue;
                var d = LocalPoint.Distance(a.Position, b.Position);
                if (d < minGap)
                {
                    minGap = d;
                    where = $"{a.Callsign} {a.Phase} / {b.Callsign} {b.Phase}";
                }
            }
            foreach (var a in h.Agents)
            {
                if (seenPhase.GetValueOrDefault(a) != DeparturePhase.TakeoffRoll && a.Phase == DeparturePhase.TakeoffRoll)
                    takeoffs.Add((a.Callsign, h.Time));
                seenPhase[a] = a.Phase;
            }
        }, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        Assert.All(h.Agents, a => Assert.True(a.Phase is DeparturePhase.Climb or DeparturePhase.Departed, $"{a.Callsign} quedó en {a.Phase} ({a.Hold})"));
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m ({where})");
        Assert.Equal(0, h.Engine.Control.Tower.AiIncursions); // la IA nunca pisa una pista sin autorización
        Assert.Equal(3, takeoffs.Count);
    }

    [Fact]
    public void Takeoff_WaitsUntilThePreviousIsAirborneAnd6000FtAway()
    {
        var h = new Harness();
        var first = h.Add("Gate", 16, "35L");
        h.Run(30);
        var second = h.Add("Gate", 18, "35L");
        double? distanceAtRoll = null;
        var prev = DeparturePhase.DoorsClosing;

        h.Run(2400, () =>
        {
            if (prev != DeparturePhase.TakeoffRoll && second.Phase == DeparturePhase.TakeoffRoll)
            {
                Assert.False(first.OnGround, "el anterior todavía está en tierra");
                distanceAtRoll = LocalPoint.Distance(first.Position, second.Position);
            }
            prev = second.Phase;
        }, until: () => second.Phase == DeparturePhase.Climb);

        Assert.NotNull(distanceAtRoll);
        Assert.True(distanceAtRoll >= TowerControl.DepartureSpacingM, $"a {distanceAtRoll:F0} m");
    }

    [Fact]
    public void BehindAHeavy_AMediumWaitsTwoMinutes()
    {
        var h = new Harness();
        var heavy = h.Add("Gate", 16, "35L", AircraftPerformance.HeavyJet, WakeCategory.Heavy, 60);
        h.Run(30);
        var medium = h.Add("Gate", 18, "35L");
        double? heavyLiftoff = null, mediumRoll = null;
        var prevHeavy = DeparturePhase.DoorsClosing;
        var prevMedium = DeparturePhase.DoorsClosing;
        var sawWake = false;

        h.Run(2400, () =>
        {
            if (prevHeavy != DeparturePhase.Climb && heavy.Phase == DeparturePhase.Climb)
                heavyLiftoff = h.Time;
            if (prevMedium != DeparturePhase.TakeoffRoll && medium.Phase == DeparturePhase.TakeoffRoll)
                mediumRoll = h.Time;
            sawWake |= medium.Hold == HoldKind.Wake;
            prevHeavy = heavy.Phase;
            prevMedium = medium.Phase;
        }, until: () => medium.Phase == DeparturePhase.Climb);

        Assert.NotNull(heavyLiftoff);
        Assert.NotNull(mediumRoll);
        Assert.True(mediumRoll - heavyLiftoff >= TowerControl.WakeDelayS - 0.2, $"solo {mediumRoll - heavyLiftoff:F0} s");
        Assert.True(sawWake);
    }

    [Fact]
    public void TwoPushbacks_NeverUseTheSamePlaceAtTheSameTime()
    {
        var h = new Harness();
        // Puestos vecinos que empujan a la misma calle; salen casi juntos.
        var a = h.Add("Gate", 19, "35L");
        var b = h.Add("Gate", 20, "35L");
        var waited = false;

        h.Run(900, () =>
        {
            var bothPushing = a.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete
                && b.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete;
            if (bothPushing)
                Assert.True(LocalPoint.Distance(a.PushEnd, b.PushEnd) >= GroundControl.PushEndSpacingM, "dos pushback al mismo lugar");
            waited |= b.Hold == HoldKind.PushbackTraffic || a.Hold == HoldKind.PushbackTraffic;
        }, until: () => a.Phase >= DeparturePhase.HoldingShort && b.Phase >= DeparturePhase.HoldingShort);

        Assert.True(a.Phase >= DeparturePhase.HoldingShort && b.Phase >= DeparturePhase.HoldingShort, $"{a.Phase} ({a.Hold}) / {b.Phase} ({b.Hold})");
        if (LocalPoint.Distance(a.PushEnd, b.PushEnd) < GroundControl.PushEndSpacingM)
            Assert.True(waited, "el segundo debió esperar al primero");
    }

    /// <summary>
    /// Ground autoriza un pushback con tráfico rodando en la misma calle y sentido si ese tráfico llega cuando el pushback
    /// ya terminó de moverse (lejos y con margen; el que viene lo sigue), o si todavía puede detenerse antes con margen:
    /// entonces le indica ceder el paso ("give way", hito 4e-2) y el que viene espera hasta que el otro ruede.
    /// </summary>
    [Theory]
    [InlineData("GateF", 3, false)] // pasa a ~2 km del fin del pushback: lo autoriza antes de que pase, sin "give way"
    [InlineData("Gate", 22, true)]  // pasa a ~600 m: el que viene cede el paso y el pushback sale primero
    public void Pushback_WithTrafficComingBehindInTheSameDirection_IsGrantedBeforeItPasses(string name, int number, bool givesWay)
    {
        var h = new Harness();
        var runway = Scel.Value.Runways[1].Secondary.Ident;
        var a = h.Add(name, number, runway, startAheadM: 6);
        h.Run(900, until: () => a.Phase == DeparturePhase.Taxi);
        Assert.Equal(DeparturePhase.Taxi, a.Phase);
        var b = h.Add("Gate", 14, runway, startAheadM: 6);
        var passAt = a.TaxiPath.ProjectWithin(b.PushEnd, 0, a.TaxiPath.Length, out _);
        bool? pushedBeforePass = null;
        var heldForPush = false;
        var minGap = double.MaxValue;

        h.Run(2400, () =>
        {
            if (pushedBeforePass is null && b.Phase == DeparturePhase.Pushback)
            {
                var ahead = passAt - a.TaxiProgress;
                pushedBeforePass = a.Phase == DeparturePhase.Taxi && ahead > 0;
                // Si lo autorizó con el otro viniendo sin "give way", el otro estaba lejos (y frena detrás).
                if (pushedBeforePass == true && !givesWay)
                    Assert.True(ahead >= GroundControl.PushMinTrafficDistanceM, $"pushback con tráfico a {ahead:F0} m");
            }
            if (b.Phase == DeparturePhase.Pushback && a.Hold == HoldKind.Traffic && a.HoldFor == b.ObjectId && a.SpeedKt < 0.5)
                heldForPush = true;
            if (Moving(a) && Moving(b) && a.Phase != DeparturePhase.TakeoffRoll && b.Phase != DeparturePhase.TakeoffRoll)
                minGap = Math.Min(minGap, LocalPoint.Distance(a.Position, b.Position));
        }, until: () => h.Agents.All(x => x.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        Assert.All(h.Agents, x => Assert.True(x.Phase is DeparturePhase.Climb or DeparturePhase.Departed, $"{x.Callsign} {x.Phase} {x.Hold}"));
        Assert.True(pushedBeforePass);
        Assert.Equal(givesWay, heldForPush);
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m");
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    /// <summary>
    /// Prueba de Marco (2026-10-31): un avión en la puerta 24 esperaba el pushback por otro que recién empujaba desde la
    /// 43, a cientos de metros. Si el otro todavía está empujando lejos, se estima cuándo llega y el pushback se autoriza.
    /// </summary>
    [Theory]
    [InlineData(43, 24)]
    [InlineData(27, 18)]
    public void Pushback_is_not_held_by_a_far_aircraft_that_is_still_pushing(int farGate, int gate)
    {
        var h = new Harness();
        var runway = "17R";
        var far = h.Add("Gate", farGate, runway);
        h.Run(900, until: () => far.Phase == DeparturePhase.Pushback);
        Assert.Equal(DeparturePhase.Pushback, far.Phase);
        var near = h.Add("Gate", gate, runway);
        var heldBy = new HashSet<uint>();
        h.Run(600, () =>
        {
            if (near.Hold == HoldKind.PushbackTraffic)
                heldBy.Add(near.HoldFor);
        }, until: () => near.Phase >= DeparturePhase.Pushback);

        Assert.True(near.Phase >= DeparturePhase.Pushback, $"{near.Callsign} sigue en {near.Phase} ({near.Hold})");
        // Si esperó por el lejano, fue solo mientras este pasaba cerca (no durante todo su pushback).
        if (heldBy.Contains(far.ObjectId))
            Assert.NotEqual(DeparturePhase.Pushback, far.Phase);
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    /// <summary>
    /// Prueba de Marco (2026-10-31): con un avión empujando desde la puerta 26, los de la misma calle (19, 20, 25) se
    /// esperaban en cadena varios minutos, aun por tráfico lejano. Ahora solo se esperan los vecinos.
    /// </summary>
    [Fact]
    public void Gates_on_the_same_taxilane_do_not_wait_in_a_chain()
    {
        var h = new Harness();
        var far = h.Add("Gate", 26, "17R");
        h.Run(900, until: () => far.Phase == DeparturePhase.Pushback);
        var others = new[] { h.Add("Gate", 19, "17R"), h.Add("Gate", 20, "17R"), h.Add("Gate", 25, "17R") };
        var t0 = h.Time;
        var pushedAt = new Dictionary<uint, double>();
        var minGap = double.MaxValue;
        h.Run(1800, () =>
        {
            foreach (var o in others)
            {
                if (o.Phase == DeparturePhase.Pushback && !pushedAt.ContainsKey(o.ObjectId))
                    pushedAt[o.ObjectId] = h.Time - t0;
            }
            foreach (var x in h.Agents)
            {
                foreach (var y in h.Agents)
                {
                    if (x.ObjectId < y.ObjectId && Moving(x) && Moving(y))
                        minGap = Math.Min(minGap, LocalPoint.Distance(x.Position, y.Position));
                }
            }
        }, until: () => h.Agents.All(x => x.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        Assert.All(h.Agents, x => Assert.True(x.Phase is DeparturePhase.Climb or DeparturePhase.Departed, $"{x.Callsign} {x.Phase} {x.Hold}"));
        // Antes: 174, 309 y 431 s, esperando aun por tráfico lejano. Ahora (70, 174 y 316 s) solo se esperan los vecinos que
        // empujan al lado o que pasan rodando por delante del puesto.
        var times = string.Join(", ", pushedAt.Values.Select(t => $"{t:F0} s"));
        Assert.True(pushedAt.Values.Min() < 90, times);
        Assert.True(pushedAt.Values.Max() < 360, times);
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m");
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    // ---------- Radio de los IA (hito 4e-1) ----------

    private static readonly RadioFrequencies ScelRadio = new(122200, 118100, 119700);

    private static List<RadioLine> Drain(TrafficEngine engine, List<RadioLine> into)
    {
        while (engine.Radio.Lines.TryDequeue(out var l))
            into.Add(l);
        return into;
    }

    [Fact]
    public void Ai_departure_talks_on_the_frequency_and_acts_only_after_its_readback()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        var a = h.Add("Gate", 14, "17R");
        var lines = new List<RadioLine>();
        double? pushStartedAt = null, lineUpAt = null, rollAt = null;
        h.Run(2400, () =>
        {
            Drain(h.Engine, lines);
            if (a.Phase == DeparturePhase.Pushback)
                pushStartedAt ??= h.Engine.Clock.SimTime;
            if (a.Phase == DeparturePhase.LineUp)
                lineUpAt ??= h.Engine.Clock.SimTime;
            if (a.Phase == DeparturePhase.TakeoffRoll)
                rollAt ??= h.Engine.Clock.SimTime;
        }, until: () => a.Phase == DeparturePhase.Climb && a.AglFt > 1500);
        Drain(h.Engine, lines);

        var kinds = lines.Select(l => l.Kind).ToList();
        Assert.Equal(
            [RadioLineKind.RequestPushback, RadioLineKind.PushbackApproved, RadioLineKind.ReadbackPushback,
             RadioLineKind.RequestTaxi, RadioLineKind.Taxi, RadioLineKind.ReadbackTaxi,
             RadioLineKind.ContactTower, RadioLineKind.ReadbackContactTower],
            kinds.Take(8));
        Assert.Contains(RadioLineKind.ReadyForDeparture, kinds);
        Assert.Contains(RadioLineKind.ReadbackTakeoff, kinds);
        Assert.Equal(RadioLineKind.ReadbackContactDeparture, kinds[^1]);
        Assert.All(lines, l => Assert.Equal("T01", l.Callsign));
        Assert.Equal(122200, lines.First(l => l.Kind == RadioLineKind.Taxi).Khz);
        Assert.Equal(118100, lines.First(l => l.Kind == RadioLineKind.ReadyForDeparture).Khz);
        Assert.Equal("17R", lines.First(l => l.Kind == RadioLineKind.Taxi).Runway);
        // Solo actúa después de colacionar.
        Assert.True(pushStartedAt >= lines.First(l => l.Kind == RadioLineKind.ReadbackPushback).TimeS);
        Assert.True(lineUpAt >= lines.First(l => l.Kind == RadioLineKind.ReadbackLineUp).TimeS);
        Assert.True(rollAt >= lines.First(l => l.Kind == RadioLineKind.ReadbackTakeoff).TimeS);
    }

    [Fact]
    public void Ai_transmissions_never_overlap_on_a_frequency()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        foreach (var g in new[] { 14, 15, 16, 19, 22 })
            h.Add("Gate", g, "17R");
        var lines = new List<RadioLine>();
        h.Run(3600, () => Drain(h.Engine, lines),
            until: () => h.Agents.All(x => x.Phase is DeparturePhase.Climb or DeparturePhase.Departed));
        Drain(h.Engine, lines);

        Assert.All(h.Agents, x => Assert.True(x.Phase is DeparturePhase.Climb or DeparturePhase.Departed, $"{x.Callsign} {x.Phase} {x.Hold}"));
        foreach (var freq in lines.GroupBy(l => l.Khz))
        {
            var ordered = freq.OrderBy(l => l.TimeS).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                Assert.True(ordered[i].TimeS >= prev.TimeS + AiRadio.DurationS(prev.Kind) - 1e-6,
                    $"{freq.Key}: {prev.Callsign} {prev.Kind} a {prev.TimeS:F1} s y {ordered[i].Callsign} {ordered[i].Kind} a {ordered[i].TimeS:F1} s");
            }
        }
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    [Fact]
    public void Ai_waits_while_the_pilot_uses_the_frequency()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        var a = h.Add("Gate", 14, "17R");
        var lines = new List<RadioLine>();
        h.Run(900, () => Drain(h.Engine, lines), until: () => a.Phase == DeparturePhase.BeaconOn);
        // El piloto habla en Ground justo cuando el IA quiere pedir el pushback.
        h.Engine.Radio.MarkBusy(122200, 10);
        var t0 = h.Engine.Clock.SimTime;
        h.Run(60, () => Drain(h.Engine, lines), until: () => lines.Count > 0);
        Assert.True(lines[0].TimeS >= t0 + 9.9, $"habló a {lines[0].TimeS - t0:F1} s");
    }

    [Theory]
    [InlineData(0.0, RadioLineKind.Follow)]
    [InlineData(180.0, RadioLineKind.GiveWay)]
    public void Ground_tells_the_ai_to_follow_or_give_way_to_the_pilot(double relativeHeading, RadioLineKind expected)
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        var a = h.Add("Gate", 16, "35L");
        // El piloto detenido sobre la ruta del IA, en su mismo sentido o de frente.
        var stop = a.TaxiPath.PointAt(300);
        var heading = GeoMath.NormalizeDeg(a.TaxiPath.HeadingAt(300) + relativeHeading);
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(stop), HeadingTrueDeg = heading, OnGround = true };
        h.Bridge.Emit();
        var lines = new List<RadioLine>();

        h.Run(1200, () => Drain(h.Engine, lines), until: () => lines.Any(l => l.Kind is RadioLineKind.ReadbackFollow or RadioLineKind.ReadbackGiveWay));

        var advisory = Assert.Single(lines, l => l.Kind is RadioLineKind.Follow or RadioLineKind.GiveWay);
        Assert.Equal(expected, advisory.Kind);
        Assert.Equal(AtcRole.Ground, advisory.Station);
        Assert.Equal(122200, advisory.Khz);
        Assert.True(advisory.FromAtc);
        if (expected == RadioLineKind.GiveWay)
            Assert.NotNull(advisory.Detail);
        Assert.Equal(HoldKind.UserAircraft, a.Hold);
    }

    // Gestor de rodaje (ADR-094): una llegada avisada (en final o en la pista) va a recorrer de frente un tramo de la ruta de
    // la salida. La salida no entra al tramo: espera antes, y sigue cuando el aviso se borra.
    [Fact]
    public void A_departure_does_not_enter_a_stretch_an_announced_arrival_will_use_head_on()
    {
        var h = new Harness();
        var a = h.Add("Gate", 16, "35L");
        var marks = a.Marks;
        // Un tramo de su ruta que empieza a unos 300 m del puesto: la llegada lo recorre en sentido contrario.
        var first = Enumerable.Range(0, marks.Count).First(i => marks[i].S > 300 && marks[i].Edges.Length > 0);
        var keys = Enumerable.Range(first, Math.Min(4, marks.Count - first)).SelectMany(i => marks[i].Edges)
            .Select(TaxiMark.Reverse).ToArray();
        var preview = new ArrivalRoute(keys, []);
        var entry = marks[first].S;
        var furthest = 0.0;

        // El aviso vence a los 240 s (ArrivalPreviewS): se renueva, como si la llegada siguiera en final.
        h.Run(900, () =>
        {
            h.Engine.Coordinator.SetArrivalPreview(9001, preview);
            furthest = Math.Max(furthest, a.TaxiProgress);
        });

        Assert.True(furthest < entry, $"entró al tramo: avance {furthest:F0} m, el tramo empieza en {entry:F0} m");
        Assert.Equal(9001u, a.HoldFor);

        h.Engine.Coordinator.ClearArrivalPreview(9001);
        h.Run(300, until: () => a.TaxiProgress > entry + 50);
        Assert.True(a.TaxiProgress > entry + 50, $"no siguió: avance {a.TaxiProgress:F0} m ({a.Hold})");
    }

    [Fact]
    public void UserAircraftOnTheRoute_IsAnObstacle()
    {
        var h = new Harness();
        var a = h.Add("Gate", 16, "35L");
        // El piloto detenido sobre la ruta de rodaje, 300 m adelante del fin del pushback.
        var stop = a.TaxiPath.PointAt(300);
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(stop), OnGround = true };
        h.Bridge.Emit();
        var closest = double.MaxValue;

        h.Run(1200, () => closest = Math.Min(closest, LocalPoint.Distance(a.Position, stop)));

        Assert.Equal(HoldKind.UserAircraft, a.Hold);
        Assert.True(a.Phase == DeparturePhase.Taxi && a.SpeedKt < 0.1, $"{a.Phase} a {a.SpeedKt:F1} kt");
        Assert.True(closest >= GroundCoordinator.FollowGapM + TrafficEngine.DefaultUserWingspanM / 2, $"llegó a {closest:F0} m del piloto");
    }

    [Theory]
    [InlineData("35L", 0)]
    [InlineData("17L", 0)]
    [InlineData("35R", 0)]
    [InlineData("17R", 0)]
    [InlineData("35L", 6)]
    [InlineData("17L", 6)]
    [InlineData("35R", 6)]
    [InlineData("17R", 6)]
    public void TenDepartures_FromAllOverTheApron_AllTakeOff(string runway, double startAheadM)
    {
        TenDepartures(runway, startAheadM, flows: false);
    }

    // Lo mismo con los sentidos de circulación automáticos de la pista en uso (ADR-067).
    [Theory]
    [InlineData("35L")]
    [InlineData("17L")]
    [InlineData("35R")]
    [InlineData("17R")]
    public void TenDepartures_WithTaxiFlows_AllTakeOff(string runway)
    {
        TenDepartures(runway, 6, flows: true);
    }

    /// <summary>ADR-102: flechas de sentido para el radar, paralelas a la pista (o en su contra) y sin amontonarse.</summary>
    [Fact]
    public void Flow_arrows_follow_the_parallel_taxiways_and_do_not_crowd()
    {
        var graph = TaxiGraph.Build(Scel.Value);
        Assert.Empty(graph.FlowArrows());
        var (ri, primary) = RunwayEnd("17R");
        graph.SetFlow(ri, primary);
        var arrows = graph.FlowArrows();
        Assert.NotEmpty(arrows);
        var runwayHeading = primary ? Scel.Value.Runways[ri].Primary.HeadingTrueDeg : Scel.Value.Runways[ri].Secondary.HeadingTrueDeg;
        Assert.All(arrows, a => Assert.True(Math.Abs(GeoMath.AngleDiffDeg(a.HeadingDeg, runwayHeading)) < 25
                                            || Math.Abs(GeoMath.AngleDiffDeg(a.HeadingDeg, runwayHeading + 180)) < 25, $"{a.HeadingDeg:0}°"));
        for (var i = 0; i < arrows.Count; i++)
            for (var j = i + 1; j < arrows.Count; j++)
                Assert.False(GeoMath.DistanceM(arrows[i].Position, arrows[j].Position) < 89
                             && Math.Abs(GeoMath.AngleDiffDeg(arrows[i].HeadingDeg, arrows[j].HeadingDeg)) < 45);
    }

    private static void TenDepartures(string runway, double startAheadM, bool flows)
    {
        var h = new Harness();
        if (flows)
        {
            h.Graph = TaxiGraph.Build(Scel.Value);
            var (ri, primary) = RunwayEnd(runway);
            h.Graph.SetFlow(ri, primary);
            Assert.True(h.Graph.FlowEdgeCount > 0, "ninguna calle paralela con sentido");
        }
        (string, int)[] stands = [("Gate", 14), ("GateE", 3), ("Gate", 22), ("GateD", 3), ("GateC", 3), ("Gate", 25), ("GateF", 3), ("GateW", 4), ("Gate", 11), ("GateE", 22)];
        var minGap = double.MaxValue;
        var where = "";
        foreach (var (name, number) in stands)
        {
            h.Add(name, number, runway, startAheadM: startAheadM);
            h.Run(30);
        }
        h.Run(5400, () =>
        {
            Assert.True(h.Agents.Count(OnRunway) <= 1, "dos aviones en la pista");
            for (var i = 0; i < h.Agents.Count; i++)
            for (var j = i + 1; j < h.Agents.Count; j++)
            {
                var a = h.Agents[i];
                var b = h.Agents[j];
                if (!Moving(a) || !Moving(b) || a.Phase == DeparturePhase.TakeoffRoll || b.Phase == DeparturePhase.TakeoffRoll)
                    continue;
                var d = LocalPoint.Distance(a.Position, b.Position);
                if (d < minGap)
                {
                    minGap = d;
                    where = $"{a.Callsign} {a.Phase} {a.Hold} / {b.Callsign} {b.Phase} {b.Hold}";
                }
            }
        }, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        var stuck = h.Agents.Where(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed))
            .Select(a => $"{a.Callsign} {a.Phase} {a.Hold} por {h.Agents.FirstOrDefault(o => o.ObjectId == a.HoldFor)?.Callsign}").ToList();
        Assert.True(stuck.Count == 0, $"atascados: {string.Join("; ", stuck)} | log: {string.Join(" / ", h.Logs.Where(l => l.Contains("bloqueo", StringComparison.OrdinalIgnoreCase)).Take(6))}");
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m ({where})");
        Assert.Equal(0, h.Engine.Control.Tower.AiIncursions); // la IA nunca pisa una pista sin autorización
    }

    // Caso visto en el simulador (2026-10-30): GateC 2 está al fondo de una calle sin salida; GateC 4 empujaba a esa
    // misma calle entre GateC 2 y el cruce que GateC 2 ya había reservado, y se bloqueaban mutuamente.
    [Theory]
    [InlineData(2, 4, 20)]
    [InlineData(2, 4, 40)]
    [InlineData(2, 4, 60)]
    [InlineData(4, 2, 40)]
    [InlineData(2, 3, 40)]
    [InlineData(3, 4, 40)]
    [InlineData(2, 5, 20)]
    public void DeadEndLane_TwoPushbacksInARow_DoNotBlockEachOther(int first, int second, double gapS)
    {
        var h = new Harness();
        // 787 (pesados), partiendo 6 m adelante del centro del puesto como el Parked ATC.
        h.Add("GateC", first, "35L", AircraftPerformance.HeavyJet, WakeCategory.Heavy, 60, startAheadM: 6, noseGearM: 25);
        h.Run(gapS);
        h.Add("GateC", second, "35L", AircraftPerformance.HeavyJet, WakeCategory.Heavy, 60, startAheadM: 6, noseGearM: 25);

        h.Run(3600, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        var stuck = h.Agents.Where(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed))
            .Select(a => $"{a.Callsign} {a.Phase} {a.Hold}").ToList();
        Assert.True(stuck.Count == 0, $"atascados: {string.Join("; ", stuck)}");
    }

    // TESTING.md §2: dos aviones enfrentados en una calle se resuelven. Gate 16 → 17L va al este por G y Gate 22 → 35L
    // al oeste por la misma calle.
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(100)]
    public void HeadOn_OnTheSameTaxiway_IsResolved(double gapS)
    {
        var h = new Harness();
        h.Add("Gate", 16, "17L");
        h.Run(gapS);
        h.Add("Gate", 22, "35L");
        var minGap = double.MaxValue;

        h.Run(3600, () =>
        {
            var (a, b) = (h.Agents[0], h.Agents[1]);
            if (Moving(a) && Moving(b) && a.Phase != DeparturePhase.TakeoffRoll && b.Phase != DeparturePhase.TakeoffRoll)
                minGap = Math.Min(minGap, LocalPoint.Distance(a.Position, b.Position));
        }, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        var stuck = h.Agents.Where(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed))
            .Select(a => $"{a.Callsign} {a.Phase} {a.Hold} {a.SpeedKt:F0}kt").ToList();
        Assert.True(stuck.Count == 0, $"atascados: {string.Join("; ", stuck)}");
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m");
    }

    // Bloqueos que aparecieron en un barrido de 1056 pares (RF-18b): cruces reservados en cruz, y pushback a mitad de
    // una calle de frente a un avión que ya venía por ella. Todos deben despegar.
    [Theory]
    [InlineData("Gate", 14, "35R", "Gate", 18, "17R", 45)]
    [InlineData("Gate", 14, "35R", "Gate", 20, "17R", 45)]
    [InlineData("Gate", 16, "35R", "Gate", 20, "17R", 0)]
    [InlineData("Gate", 18, "17L", "Gate", 22, "35L", 0)]
    [InlineData("Gate", 18, "35R", "Gate", 22, "17R", 45)]
    [InlineData("Gate", 20, "17L", "Gate", 24, "35L", 0)]
    [InlineData("Gate", 14, "35L", "GateE", 3, "17R", 0)]
    [InlineData("Gate", 14, "35R", "GateD", 3, "17R", 45)]
    [InlineData("Gate", 14, "35L", "GateC", 3, "17R", 45)]
    [InlineData("GateD", 3, "35R", "GateF", 3, "17R", 0)]
    [InlineData("GateF", 3, "17L", "Gate", 24, "35R", 0)]
    [InlineData("GateF", 3, "17L", "GateD", 3, "35R", 0)]
    [InlineData("GateW", 4, "17L", "Gate", 18, "35L", 0)]
    [InlineData("GateE", 3, "17L", "Gate", 14, "35L", 0)]
    [InlineData("GateW", 4, "17L", "Gate", 14, "35L", 0)]
    [InlineData("GateW", 8, "17L", "Gate", 14, "35L", 0)]
    public void CrossingAndHeadOnPairs_AllTakeOff(string n1, int x1, string r1, string n2, int x2, string r2, double gapS)
    {
        var h = new Harness();
        h.Add(n1, x1, r1, startAheadM: 6);
        h.Run(gapS);
        h.Add(n2, x2, r2, startAheadM: 6);
        var minGap = double.MaxValue;
        var where = "";

        h.Run(2400, () =>
        {
            var (a, b) = (h.Agents[0], h.Agents[1]);
            if (Moving(a) && Moving(b) && a.Phase != DeparturePhase.TakeoffRoll && b.Phase != DeparturePhase.TakeoffRoll)
            {
                var d = LocalPoint.Distance(a.Position, b.Position);
                if (d < minGap)
                {
                    minGap = d;
                    where = $"{a.Phase} {a.Hold} {a.SpeedKt:F0}kt rr{a.Reroutes} / {b.Phase} {b.Hold} {b.SpeedKt:F0}kt rr{b.Reroutes} t={h.Time:F0}";
                }
            }
        }, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        var stuck = h.Agents.Where(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed))
            .Select(a => $"{a.Callsign} {a.Phase} {a.Hold}").ToList();
        Assert.True(stuck.Count == 0, $"atascados: {string.Join("; ", stuck)} | log: {string.Join(" / ", h.Logs.Where(l => l.Contains("bloqueo", StringComparison.OrdinalIgnoreCase)).Take(6))}");
        Assert.True(minGap > 45, $"se acercaron a {minGap:F0} m ({where})");
        Assert.Equal(0, h.Engine.Control.Tower.AiIncursions); // la IA nunca pisa una pista sin autorización
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    // Headless (ADR-044): pérdidas de 44 m entre dos que ruedan sin esperarse. GateC 1 y GateE 2 van por calles de plataforma
    // paralelas a 40 m que se unen en curva (scel-busy-morning con radio, semilla 11): se ponían a la par con 4 m entre
    // puntas, y el que esperaba el cruce quedaba a menos de 45 m de la ruta del otro. Gate 12 y GateD 1 (semilla 1) pasan por
    // cruces distintos a 22 m uno del otro, sin tramo en común. Fallaban con los desfases 0 y 240 s.
    [Theory]
    [InlineData("GateC", 1, "GateE", 2, "35R", 0)]
    [InlineData("GateE", 2, "GateC", 1, "35R", 0)]
    [InlineData("GateC", 1, "GateE", 2, "35R", 180)]
    [InlineData("Gate", 12, "GateD", 1, "35R", 240)]
    [InlineData("GateD", 1, "GateF", 2, "35L", 180)]
    public void Merges_AndNearbyCrossings_KeepSeparation(string n1, int x1, string n2, int x2, string runway, double gapS)
    {
        var h = new Harness();
        h.Add(n1, x1, runway, startAheadM: 6);
        h.Run(gapS);
        h.Add(n2, x2, runway, startAheadM: 6);
        var lost = "";

        h.Run(2400, () =>
        {
            var (a, b) = (h.Agents[0], h.Agents[1]);
            if (lost.Length == 0 && Moving(a) && Moving(b) && a.Phase != DeparturePhase.TakeoffRoll && b.Phase != DeparturePhase.TakeoffRoll
                && GroundCoordinator.SeparationLost(a, b, 45))
                lost = $"{LocalPoint.Distance(a.Position, b.Position):F0} m: {a.Phase} {a.Hold} {a.SpeedKt:F0}kt / {b.Phase} {b.Hold} {b.SpeedKt:F0}kt t={h.Time:F0}";
        }, until: () => h.Agents.All(a => a.Phase is DeparturePhase.Climb or DeparturePhase.Departed));

        var stuck = h.Agents.Where(a => a.Phase is not (DeparturePhase.Climb or DeparturePhase.Departed))
            .Select(a => $"{a.Callsign} {a.Phase} {a.Hold}").ToList();
        Assert.True(stuck.Count == 0, $"atascados: {string.Join("; ", stuck)}");
        Assert.True(lost.Length == 0, $"pérdida de separación a {lost}");
        Assert.Equal(0, h.Engine.Coordinator.DeadlocksUnresolved);
    }

    // Headless (semilla 3 sin radio, ADR-044): Ground le dijo a HDL044 que cediera el paso a HDL038 y 5 s después a HDL048.
    // Solo cabía un "give way" por avión: el segundo (más adelante en la ruta) reemplazó al primero, y HDL044 pasó a 20 kt
    // a 36 m de HDL038, que empujaba. Vale el punto más cercano mientras cada pushback siga activo.
    [Fact]
    public void GiveWay_ToTwoPushbacks_StopsAtTheNearerOne()
    {
        var h = new Harness();
        var a = h.Add("GateE", 3, "35R", startAheadM: 6);
        h.Run(1200, until: () => a.Phase == DeparturePhase.Taxi);
        var p1 = h.Add("Gate", 17, "35R", startAheadM: 6);
        var p2 = h.Add("Gate", 22, "35R", startAheadM: 6);
        static bool Pushing(DepartureAgent p) => p.Phase is DeparturePhase.Pushback or DeparturePhase.PushbackComplete
            || (p.Phase == DeparturePhase.BeaconOn && (p.Clearances & ClearanceKind.Pushback) != 0);
        h.Run(1200, until: () => a.Phase != DeparturePhase.Taxi || (Pushing(p1) && Pushing(p2)));
        Assert.True(a.Phase == DeparturePhase.Taxi && Pushing(p1) && Pushing(p2), $"{a.Phase} / {p1.Phase} / {p2.Phase}");

        var near = a.TaxiProgress + 80;
        h.Engine.Coordinator.GiveWay(a, near, p1);
        h.Engine.Coordinator.GiveWay(a, a.TaxiProgress + 300, p2);
        h.Run(0.5); // unos ticks del motor

        Assert.True(a.TaxiLimitS <= near, $"límite {a.TaxiLimitS:F0}, debía detenerse en {near:F0}");
    }

    [Theory]
    [InlineData(0, 40, 0, true)]     // calles paralelas a 40 m, envergaduras de 36 m: 4 m entre puntas
    [InlineData(0, 43, 0, false)]    // a 43 m: 7 m entre puntas
    [InlineData(0, 43, 180, false)]  // paralelas en sentidos contrarios
    [InlineData(0, 43, 90, false)]   // en ángulo a 43 m: 7 m entre la punta del ala y la cola (ADR-091, siluetas)
    [InlineData(0, 30, 90, true)]    // en ángulo a 30 m: la cola del otro bajo el ala
    [InlineData(0, 10, 0, true)]     // sobre el eje del otro
    public void SeparationLost_CountsWingtipsOnParallelLanes(double heading1, double eastM, double heading2, bool lost)
    {
        // b al este de a, a eastM.
        Assert.Equal(lost, GroundCoordinator.SeparationLost(new LocalPoint(0, 0), heading1, 36, new LocalPoint(eastM, 0), heading2, 36, 45));
    }

    // TESTING.md §2: un ciclo de 3 aviones se detecta (A espera a B, B a C, C a A).
    [Fact]
    public void ThreeAircraftWaitCycle_IsDetected()
    {
        var h = new Harness();
        var a = h.Add("Gate", 14, "35L");
        var b = h.Add("Gate", 18, "35L");
        var c = h.Add("Gate", 22, "35L");
        var coordinator = new GroundCoordinator();
        (a.Hold, a.HoldFor) = (HoldKind.Intersection, b.ObjectId);
        (b.Hold, b.HoldFor) = (HoldKind.Traffic, c.ObjectId);
        (c.Hold, c.HoldFor) = (HoldKind.Intersection, a.ObjectId);

        Assert.True(coordinator.InWaitCycle(a, h.Agents));
        Assert.True(coordinator.InWaitCycle(c, h.Agents));
        c.HoldFor = 0; // se rompe el ciclo
        Assert.False(coordinator.InWaitCycle(a, h.Agents));
    }

    // RF-18c: el piloto entra a la pista mientras un IA corre para despegar: aborta, frena, espera y despega cuando sale.
    /// <summary>
    /// Prueba de Marco (2026-10-31): un IA en el punto de espera ya autorizado a alinearse (la radio todavía no lo dijo)
    /// y el piloto, detrás en la fila, recibían "line up and wait" a la vez. Ahora la pista figura ocupada por el IA
    /// autorizado y el piloto no es el siguiente hasta llegar a su punto de espera.
    /// </summary>
    [Fact]
    public void Pilot_is_not_lined_up_together_with_an_ai_already_cleared_to_line_up()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        var a = h.Add("Gate", 14, "17R");
        var tower = h.Engine.Control.Tower;
        var (r, _) = RunwayEnd("17R");
        // El piloto en tierra, lejos, todavía rodando hacia su punto de espera; ya avisó que está listo.
        var stand = Scel.Value.Parkings.First(p => p.Name.ToString() == "GateW" && p.Number == 11).Position;
        h.Bridge.State = h.Bridge.State with { Position = stand, OnGround = true };
        h.Bridge.Emit();
        tower.SetPilotHoldingPoint(a.Projection.ToGeo(a.TaxiPath.PointAt(a.HoldAt)));
        tower.SetPilot(r, PilotTowerClearance.Ready);

        h.Run(2400, until: () => a.Phase == DeparturePhase.HoldingShort && (a.Clearances & ClearanceKind.LineUp) != 0);
        Assert.Equal(DeparturePhase.HoldingShort, a.Phase);
        h.Run(0.1);
        var st = tower.PilotStatus;
        Assert.Equal(a.Callsign, st.OccupiedBy);
        Assert.False(st.AtHoldingPoint);
        Assert.False(st.Free);
    }

    /// <summary>La fila del punto de espera es por orden de llegada: un IA que llega después del piloto (listo) espera.</summary>
    [Fact]
    public void An_ai_arriving_after_the_ready_pilot_waits_its_turn()
    {
        var h = new Harness();
        var a = h.Add("Gate", 14, "17R");
        var tower = h.Engine.Control.Tower;
        var (r, _) = RunwayEnd("17R");
        // El piloto ya está en su punto de espera (otro, fuera del camino del IA) y avisó que está listo.
        var spot = Scel.Value.Parkings.First(p => p.Name.ToString() == "GateW" && p.Number == 11).Position;
        h.Bridge.State = h.Bridge.State with { Position = spot, OnGround = true };
        h.Bridge.Emit();
        tower.SetPilotHoldingPoint(spot);
        tower.SetPilot(r, PilotTowerClearance.Ready);
        h.Run(1);
        Assert.True(tower.PilotStatus.Free); // el piloto es el primero

        h.Run(2400, until: () => a.Phase == DeparturePhase.HoldingShort);
        h.Run(60);
        Assert.Equal(DeparturePhase.HoldingShort, a.Phase);
        Assert.Equal(HoldKind.Runway, a.Hold);
        Assert.True(tower.PilotStatus.Free);

        // El piloto se alinea, despega y se va: el IA sigue.
        tower.SetPilot(r, PilotTowerClearance.Takeoff);
        h.Bridge.State = h.Bridge.State with { OnGround = false, AltitudeAglFt = 3000 };
        h.Bridge.Emit();
        h.Run(900, until: () => a.Phase == DeparturePhase.Climb);
        Assert.Equal(DeparturePhase.Climb, a.Phase);
    }

    /// <summary>
    /// Prueba de Marco (EGLL, 2026-11-27): el piloto llegó a su punto de espera antes que el IA, pero avisó "listo" cuando
    /// el IA ya estaba alineado. El IA esperaba al piloto (llegó antes a la fila) y al piloto le decían que esperara al IA
    /// (en la pista): nadie salía. El que ya está en la pista sale primero.
    /// </summary>
    [Fact]
    public void An_ai_already_lined_up_departs_before_a_pilot_who_reports_ready_later()
    {
        var h = new Harness();
        var a = h.Add("Gate", 14, "17R");
        var tower = h.Engine.Control.Tower;
        var (r, _) = RunwayEnd("17R");
        var spot = Scel.Value.Parkings.First(p => p.Name.ToString() == "GateW" && p.Number == 11).Position;
        h.Bridge.State = h.Bridge.State with { Position = spot, OnGround = true };
        h.Bridge.Emit();
        tower.SetPilotHoldingPoint(spot);
        tower.SetPilot(r, PilotTowerClearance.None); // en su punto de espera, todavía sin llamar a la Torre
        h.Run(1);

        h.Run(2400, until: () => a.Phase == DeparturePhase.LinedUp);
        Assert.Equal(DeparturePhase.LinedUp, a.Phase);
        tower.SetPilot(r, PilotTowerClearance.Ready);
        h.Run(300, until: () => a.Phase == DeparturePhase.Climb);
        Assert.Equal(DeparturePhase.Climb, a.Phase);
    }

    /// <summary>
    /// Prueba de Marco (2026-11-06): con el piloto ya en tierra (en su puesto, cerca de la pista de la que despegó), un IA
    /// no espera "separación con la salida anterior" para siempre: esa separación es solo con el piloto en el aire.
    /// </summary>
    [Fact]
    public void Pilot_spacing_does_not_hold_ai_once_the_pilot_is_on_the_ground()
    {
        var h = new Harness();
        var tower = h.Engine.Control.Tower;
        var (r, _) = RunwayEnd("17R");
        // A 400 m del umbral de la 17R, a un costado (dentro de los 6000 ft de separación).
        var end = Scel.Value.Runways[r].Primary;
        var spot = GeoMath.Destination(end.Position, end.HeadingTrueDeg + 90, 400);
        // El piloto despega de la 17R...
        tower.SetPilot(r, PilotTowerClearance.Takeoff);
        h.Bridge.State = h.Bridge.State with { Position = spot, OnGround = false, AltitudeAglFt = 3000 };
        h.Bridge.Emit();
        h.Run(1);
        // ...y después está en tierra en un puesto cerca de la pista: el IA despega igual.
        h.Bridge.State = h.Bridge.State with { OnGround = true, AltitudeAglFt = 0 };
        h.Bridge.Emit();
        var a = h.Add("Gate", 14, "17R");
        h.Run(2400, until: () => a.Phase == DeparturePhase.Climb);
        Assert.Equal(DeparturePhase.Climb, a.Phase);
    }

    /// <summary>Un IA que llegó antes al punto de espera sale antes: el piloto ve cuántos tiene delante.</summary>
    [Fact]
    public void The_pilot_sees_how_many_depart_before_him()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio; // el IA espera en el punto de espera el intercambio por radio
        var a = h.Add("Gate", 14, "17R");
        var tower = h.Engine.Control.Tower;
        var (r, _) = RunwayEnd("17R");
        h.Run(2400, until: () => a.Phase == DeparturePhase.HoldingShort);
        var spot = Scel.Value.Parkings.First(p => p.Name.ToString() == "GateW" && p.Number == 11).Position;
        h.Bridge.State = h.Bridge.State with { Position = spot, OnGround = true };
        h.Bridge.Emit();
        tower.SetPilotHoldingPoint(spot);
        tower.SetPilot(r, PilotTowerClearance.Ready);
        h.Run(0.2);
        var st = tower.PilotStatus;
        Assert.True(st.AtHoldingPoint);
        Assert.False(st.Free);
        Assert.True(st.AheadCount == 1 || st.OccupiedBy == a.Callsign, $"{st}");
    }

    /// <summary>
    /// Ground le da el paso al piloto en un cruce (después de cederlo varias veces): el IA que viene se detiene antes, se
    /// lo dicen por radio, y sigue cuando se retira la prioridad.
    /// </summary>
    [Fact]
    public void Ai_stops_before_the_crossing_where_the_pilot_has_priority()
    {
        var h = new Harness();
        h.Engine.Radio.Frequencies = ScelRadio;
        var a = h.Add("Gate", 16, "35L");
        h.Run(1200, until: () => a.Phase == DeparturePhase.Taxi && a.SpeedKt > 8);
        Assert.Equal(DeparturePhase.Taxi, a.Phase);
        // El piloto en tierra a un costado; el cruce, 250 m adelante en la ruta del IA.
        var crossing = a.TaxiPath.PointAt(a.TaxiProgress + 250);
        var side = Polyline.Offset(crossing, a.TaxiPath.HeadingAt(a.TaxiProgress + 250) + 90, 70);
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(side), HeadingTrueDeg = GeoMath.NormalizeDeg(a.TaxiPath.HeadingAt(a.TaxiProgress + 250) - 90), OnGround = true };
        h.Bridge.Emit();
        h.Engine.Coordinator.PilotPriority = new PilotPriority(a.Projection.ToGeo(crossing));
        var lines = new List<RadioLine>();
        h.Run(90, () => Drain(h.Engine, lines));

        Assert.True(a.SpeedKt < 0.5, $"{a.SpeedKt:F1} kt");
        Assert.Equal(HoldKind.UserAircraft, a.Hold);
        Assert.True(LocalPoint.Distance(a.Position, crossing) >= GroundCoordinator.PriorityClearM, $"a {LocalPoint.Distance(a.Position, crossing):F0} m del cruce");
        Assert.Contains(lines, l => l.Kind is RadioLineKind.GiveWay or RadioLineKind.Follow);

        // El piloto pasó: se retira la prioridad y el IA sigue.
        h.Engine.Coordinator.PilotPriority = null;
        h.Bridge.State = h.Bridge.State with { OnGround = false, AltitudeAglFt = 3000 };
        h.Bridge.Emit();
        h.Run(60);
        Assert.True(a.SpeedKt > 3, $"{a.SpeedKt:F1} kt");
    }

    [Fact]
    public void PilotOnTheRunway_DuringTheTakeoffRoll_RejectsAndWaits()
    {
        var h = new Harness();
        var a = h.Add("Gate", 16, "35L");
        h.Run(2400, until: () => a.Phase == DeparturePhase.TakeoffRoll && a.SpeedKt > 40);
        Assert.Equal(DeparturePhase.TakeoffRoll, a.Phase);

        // El piloto se mete a la pista 900 m adelante.
        var ahead = Polyline.Offset(a.Position, a.HeadingDeg, 900);
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(ahead), OnGround = true };
        h.Bridge.Emit();
        h.Run(60);

        Assert.Equal(1, h.Engine.Control.Tower.PilotIncursions);
        Assert.Equal(1, h.Engine.Control.Tower.RejectedTakeoffs);
        Assert.True(a.OnGround && a.SpeedKt < 0.1, $"{a.Phase} a {a.SpeedKt:F0} kt");
        Assert.True(LocalPoint.Distance(a.Position, ahead) > 200, "frenó antes de llegar al piloto");

        // Sale de la pista: vuelve a despegar.
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(Polyline.Offset(ahead, a.HeadingDeg + 90, 400)) };
        h.Bridge.Emit();
        h.Run(600, until: () => a.Phase == DeparturePhase.Climb);
        Assert.Equal(DeparturePhase.Climb, a.Phase);
    }

    // RF-18c: a más de 100 kt no se aborta (como pasado V1).
    [Fact]
    public void PilotOnTheRunway_AboveRejectSpeed_TheTakeoffContinues()
    {
        var h = new Harness();
        var a = h.Add("Gate", 16, "35L");
        h.Run(2400, until: () => a.Phase == DeparturePhase.TakeoffRoll && a.SpeedKt > DepartureAgent.RejectMaxSpeedKt + 5);
        h.Bridge.State = h.Bridge.State with { Position = a.Projection.ToGeo(Polyline.Offset(a.Position, a.HeadingDeg, 1500)), OnGround = true };
        h.Bridge.Emit();
        h.Run(120, until: () => a.Phase == DeparturePhase.Climb);

        Assert.Equal(DeparturePhase.Climb, a.Phase);
        Assert.Equal(0, h.Engine.Control.Tower.RejectedTakeoffs);
    }
}
