using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>
/// La llegada vigilada (ADR-105, prueba de Marco 2026-11-09 en RJBB): frustrada con instrucciones y reintegro, velocidad,
/// perfil, alerta de altitud baja, motor y al aire si no está estabilizado, panel de instrucciones y la colación pendiente
/// al retomar. Destino SCEL (datos reales del simulador y su archivo ATC, con Aproximación).
/// </summary>
public class ArrivalWatchTests
{
    private const double Nm = 1852;
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private sealed class Pilot
    {
        public required PilotSession Session;
        public required ArrivalInfo Arrival;
        public required GeoPoint Threshold;
        public required double Course;
        public required double ElevationFt;
        public GeoPoint Position;
        public double HeadingDeg;
        public double AltitudeFt;
        public double? Ias;
        public double? Vs;
        public bool OnGround;
        public double Now;
        public readonly List<string> Heard = [];

        /// <summary>En la prolongación del eje a <paramref name="nm"/> del umbral, con rumbo de pista, en la senda + desvío.</summary>
        public void OnFinal(double nm, double offsetFt = 0, double crossNm = 0)
        {
            var p = GeoMath.Destination(Threshold, GeoMath.NormalizeDeg(Course + 180), nm * Nm);
            Position = crossNm == 0 ? p : GeoMath.Destination(p, GeoMath.NormalizeDeg(Course + 90), crossNm * Nm);
            HeadingDeg = Course;
            AltitudeFt = FinalGeometry.Of(Threshold, Course, ElevationFt, 3, Position, Course).ProfileFt + offsetFt;
        }

        public IReadOnlyList<string> Tick(int times = 1)
        {
            var said = new List<string>();
            for (var i = 0; i < times; i++)
            {
                Now += 1;
                foreach (var x in Session.Tick(Now))
                    said.Add($"[{x.Station.Callsign}] {x.Text}");
            }
            Heard.AddRange(said);
            return said;
        }

        public IReadOnlyList<string> Say(string text, int? khz = null)
        {
            Now += 1;
            var replies = Session.OnPilotMessage(text, khz ?? (Session.ReadbackStation ?? Session.ExpectedStation)!.Khz, Now)
                .Select(x => $"[{x.Station.Callsign}] {x.Text}").ToList();
            Heard.AddRange(replies);
            return replies;
        }

        /// <summary>Lo que diría el copiloto: la primera sugerencia.</summary>
        public IReadOnlyList<string> SayFirstSuggestion() => Say(Session.Suggestions()[0].Text);
    }

    private static Pilot Make(AtcLanguage language = AtcLanguage.English, Approach? approach = null, Func<(string, double)?>? leader = null,
        ApproachWatchLevel watch = ApproachWatchLevel.Full, bool withoutPath = false, Func<ArrivalInfo, ArrivalInfo>? tweak = null,
        SimATC.Core.Fleet.WakeCategory? leaderWake = null)
    {
        var scel = Load("SCEL");
        var scie = Load("SCIE");
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(DataDir, "SCEL"));
        var phrases = Phraseology.Load(Phraseology.PathFor(DataDir, language), new Random(3));
        // TEST DATA: plan SCIE → SCEL 17L.
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCIE", Position: scie.Reference, PlannedRunway: "02"),
            new PlanAirport("SCEL", Position: scel.Reference, PlannedRunway: "17L"), 24000, "", []);
        var arrival = ArrivalInfo.Create(scel, new AtcStations(scel, atc), null, atc, "17L", approach);
        if (withoutPath)
        {
            // TEST DATA: la misma carta como VOR sin ángulo de senda (no precisión, se puede bajar por escalones).
            var a = arrival.Procedure!;
            arrival = ArrivalInfo.Create(scel, new AtcStations(scel, atc), null, atc, "17L",
                a with { Type = ApproachType.Vor, FinalLegs = [.. a.FinalLegs.Select(l => l with { VerticalAngleDeg = 0 })] });
        }
        if (tweak is not null)
            arrival = tweak(arrival);
        var end = scel.Runways.SelectMany(r => new[] { (r, e: r.Primary), (r, e: r.Secondary) }).First(x => x.e.Ident == "17L");
        Pilot? pilot = null;
        var ctx = new DepartureContext(plan, scie, null, new AtcStations(scie, null), TaxiGraph.Build(scie), phrases, () => ["02"], () => 'A',
            () => pilot!.Position, () => ReadbackStrictness.Realistic,
            OnGround: () => pilot!.OnGround, HeadingDeg: () => pilot!.HeadingDeg, AltitudeFt: () => pilot!.AltitudeFt,
            Wind: () => "180 degrees 8 knots", GroundSpeedKt: () => pilot!.Ias ?? 160, Arrival: () => arrival, QnhHpa: () => 1015,
            Language: language, IndicatedAirspeedKt: () => pilot!.Ias, VerticalSpeedFpm: () => pilot!.Vs, LeaderAhead: leader, WatchLevel: () => watch,
            LeaderWake: () => leaderWake);
        var session = new PilotSession(ctx, new Random(5));
        pilot = new Pilot
        {
            Session = session, Arrival = arrival, Threshold = end.e.Position, Course = end.e.HeadingTrueDeg, ElevationFt = end.r.ElevationFt,
        };
        // Con la Aproximación de SCEL, autorizado a la altitud de la aproximación y con la aproximación esperada.
        var app = arrival.Stations.For(AtcRole.Approach)!;
        pilot.OnFinal(25);
        pilot.AltitudeFt = arrival.ApproachAltitudeFt;
        session.Resume(new FlightSnapshot(plan, PilotPhase.Arrival, null, true, app, DateTime.UtcNow)
        {
            ClearedFt = arrival.ApproachAltitudeFt, DescentGiven = true, ApproachExpected = true,
        }, atStand: false);
        return pilot;
    }

    /// <summary>
    /// Con una transición que empalma (ADR-146): estos tests vigilan velocidad y altitud cruzando la final; sin transición ni
    /// ruta, ese mismo avión recibiría vectores.
    /// </summary>
    private static ArrivalInfo Connected(ArrivalInfo a) => a with { Transition = a.Procedure!.Transitions[0].Name };

    /// <summary>Primer contacto con la Aproximación y lo que responda.</summary>
    private static void CheckIn(Pilot p)
    {
        p.SayFirstSuggestion();
        while (p.Session.AwaitingReadback)
            p.SayFirstSuggestion();
    }

    /// <summary>Colaciona (con la sugerencia, como el copiloto) todo lo pendiente y hace los primeros contactos.</summary>
    private static void Answer(Pilot p)
    {
        for (var i = 0; i < 6 && (p.Session.AwaitingReadback || p.Session.AwaitingCheckIn); i++)
            p.SayFirstSuggestion();
    }

    private static void AssertClean(Pilot p) =>
        Assert.DoesNotContain(p.Heard, h => h.Contains("negative") || h.Replace("I say again", "").Contains("say again") || h.Contains("read back"));

    [Fact]
    public void Going_around_gets_missed_approach_instructions_and_is_not_cleared_again_until_back_on_the_approach()
    {
        var p = Make();
        CheckIn(p);
        // Establecido a 9 NM: autorización de aproximación y paso a la Torre.
        p.OnFinal(9);
        p.Ias = 150;
        for (var i = 0; i < 10 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            Answer(p);
        }
        Assert.Contains(p.Heard, h => h.Contains("cleared ILS approach runway 17L") || h.Contains("cleared") && h.Contains("runway 17L"));
        Assert.Equal(PilotPhase.ArrivalTower, p.Session.Phase);
        p.OnFinal(4);
        p.Tick(3);
        Answer(p);
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));

        // Motor y al aire a 1 NM: la Torre da la frustrada y lo pasa a la Aproximación.
        p.OnFinal(1);
        var tower = p.Say($"Santiago Tower, LAN205, going around");
        Assert.Contains(tower, h => h.Contains("missed approach") || h.Contains("climb to"));
        Assert.Contains(tower, h => h.Contains("contact Santiago Approach 119.7"));
        p.SayFirstSuggestion(); // colación de la frustrada (el primer contacto, después)
        Assert.False(p.Session.AwaitingReadback);
        Assert.Equal(PilotPhase.Arrival, p.Session.Phase);
        Assert.True(p.Session.MissedApproachActive);

        // Subiendo sobre la pista con rumbo de pista: nada de autorizaciones ni de Torre (el bug de RJBB).
        p.Position = GeoMath.Destination(p.Threshold, p.Course, 2 * Nm);
        p.AltitudeFt = p.ElevationFt + 1500;
        var climbing = p.Tick(30);
        Assert.DoesNotContain(climbing, h => h.Contains("cleared") || h.Contains("contact Santiago Tower"));

        // Primer contacto: espera publicada o directo al inicio de la aproximación, y la aproximación que se espera.
        var before = p.Heard.Count;
        Answer(p);
        var reintegration = p.Heard.Skip(before).ToList();
        Assert.Contains(reintegration, h => h.Contains("hold at") || h.Contains("route direct"));
        Assert.Contains(reintegration, h => h.Contains("expect ILS approach runway 17L") || h.Contains("expect") && h.Contains("17L"));
        Assert.NotNull(p.Session.ReintegrationFix);
        Assert.DoesNotContain(p.Heard.Skip(before), h => h.Contains("cleared"));
        Assert.Contains(p.Session.Orders(), o => o.Label is "Espera" or "Directo");
        Assert.Contains(p.Session.Orders(), o => o.Label == "Frustrada");

        // De vuelta establecido en la final: otra vez autorizado.
        p.OnFinal(11);
        var again = p.Tick(3);
        Assert.Contains(again, h => h.Contains("cleared") && h.Contains("17L"));
        AssertClean(p);
    }

    [Fact]
    public void Going_around_still_with_approach_gets_the_missed_approach_and_the_reintegration_from_the_same_station()
    {
        var p = Make(AtcLanguage.Spanish);
        CheckIn(p);
        p.Ias = 150;
        p.OnFinal(11);
        p.Tick(2);
        Answer(p);
        Assert.True(p.Session.ApproachCleared);
        // Antes del paso a la Torre: "motor y al aire" con la Aproximación.
        var said = p.Say("Santiago Aproximación, LAN205, motor y al aire");
        Assert.Contains(said, h => h.Contains("frustrada publicada") || h.Contains("procedimiento de aproximación frustrada") || h.Contains("ascienda a"));
        Assert.DoesNotContain(said, h => h.Contains("contacte"));
        p.SayFirstSuggestion();
        Assert.False(p.Session.AwaitingReadback);
        // La misma Aproximación lo reintegra sin primer contacto.
        var next = p.Tick(2);
        Assert.Contains(next, h => h.Contains("mantenga espera sobre") || h.Contains("directo a"));
        Answer(p);
        AssertClean(p);
    }

    [Fact]
    public void Not_stabilized_at_the_gate_the_tower_orders_a_go_around()
    {
        var p = Make();
        CheckIn(p);
        p.OnFinal(8);
        p.Ias = 150;
        for (var i = 0; i < 10 && !p.Session.LandingCleared; i++)
        {
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));
        // A 2,3 NM, 900 ft sobre la senda.
        p.OnFinal(2.3, 900);
        var said = p.Tick(2);
        Assert.Contains(said, h => h.Contains("go around, I say again, go around"));
        Assert.Contains(said, h => h.Contains("missed approach") || h.Contains("climb to"));
        Assert.False(p.Session.LandingCleared);
    }

    [Fact]
    public void Stable_on_the_glide_path_there_are_no_warnings()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 145;
        for (var nm = 12.0; nm > 1; nm -= 0.1)
        {
            p.OnFinal(nm);
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));
        Assert.DoesNotContain(p.Heard, h => h.Contains("go around") || h.Contains("altitude should be") || h.Contains("low altitude")
                                            || h.Contains("reduce speed") || h.Contains("three sixty") || h.Contains("expedite"));
        AssertClean(p);
    }

    // EBBR 2026-11-14: autorizado a aterrizar en una final larga, el copiloto preguntó "confirm clearance" 2,5 min después.
    [Fact]
    public void Once_cleared_to_land_the_copilot_does_not_ask_to_confirm_the_clearance()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 145;
        var nm = 12.0;
        for (; nm > 1 && !p.Session.LandingCleared; nm -= 0.1)
        {
            p.OnFinal(nm);
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));
        var copilot = new Copilot(new Random(1));
        var settings = new CopilotSettings(CopilotReadbacks.Always, true, true, false);
        var end = p.Now + Copilot.StuckAskS + 60;
        while (p.Now < end)
        {
            p.Tick();
            Assert.DoesNotContain("confirm clearance", copilot.Tick(p.Session, settings, p.Now, atcTalking: false)?.Text ?? "");
        }
    }

    // PLAN-PULIDO bloque 2 (ADR-121): la autorización lleva la letra de la carta y la transición por la que llega.
    [Fact]
    public void The_approach_clearance_says_the_chart_letter_and_the_transition()
    {
        // TEST DATA: la carta de SCEL como "ILS Z" y una transición de nombre inventado.
        var p = Make(tweak: a => a with { Procedure = a.Procedure! with { Suffix = "Z" }, Transition = "TESTO" });
        CheckIn(p);
        p.OnFinal(9);
        p.Ias = 150;
        for (var i = 0; i < 10 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            Answer(p);
        }
        Assert.Contains(p.Heard, h => h.Contains("cleared ILS Zulu approach runway 17L via TESTO"));
        Assert.Equal(PilotPhase.ArrivalTower, p.Session.Phase);
        AssertClean(p);
    }

    /// <summary>TEST DATA: la carta de SCEL con una transición inventada "TESTO" cuyo IAF está a 24 NM y 6 NM al costado, a 6000 ft o más.</summary>
    private static ArrivalInfo WithTransition(ArrivalInfo a)
    {
        var end = a.Airport.Runways.SelectMany(r => new[] { r.Primary, r.Secondary }).First(e => e.Ident == "17L");
        var at = GeoMath.Destination(GeoMath.Destination(end.Position, GeoMath.NormalizeDeg(end.HeadingTrueDeg + 180), 24 * Nm),
            GeoMath.NormalizeDeg(end.HeadingTrueDeg + 90), 6 * Nm);
        var iaf = a.Procedure!.FinalLegs[0] with
        {
            FixIcao = "TESTO", FixPosition = at, AltitudeConstraint = AltitudeConstraint.AtOrAbove, Altitude1Ft = 6000, Altitude2Ft = 0,
            IsFaf = false, IsIf = false, SpeedConstraint = SpeedConstraint.AtOrBelow, SpeedLimitKt = 230,
        };
        return a with { Procedure = a.Procedure with { Transitions = [new ApproachTransition("TESTO", [iaf])] }, Transition = "TESTO" };
    }

    // PLAN-PULIDO bloque 2 (ADR-122): la Aproximación baja a la altitud del IAF; la plataforma llega con la autorización,
    // que se da llegando al IAF aunque falten más de 18 NM.
    [Fact]
    public void Approach_descends_to_the_iaf_altitude_and_clears_the_approach_near_the_iaf()
    {
        var p = Make(tweak: WithTransition);
        Assert.Equal(6000, p.Arrival.InitialAltitudeFt);
        Assert.True(p.Arrival.ApproachAltitudeFt < 6000);
        CheckIn(p);
        // Pide descenso a 8000 ft: lo bajan a la altitud del IAF, no a la plataforma de la final.
        p.OnFinal(40, crossNm: 6);
        p.AltitudeFt = 8000;
        p.Ias = 220;
        var said = p.Say("Santiago Approach, LAN205, request descent");
        Assert.Contains(said, h => h.Contains("6000"));
        Answer(p);
        Assert.Contains(p.Session.Orders(), o => o.Label == "Altitud" && o.Value.Contains("6000"));
        Assert.Contains(p.Session.Orders(), o => o.Label == "Carta" && o.Value.StartsWith("TESTO ≥6000 ≤230 kt", StringComparison.Ordinal));
        // A 30 NM, lejos del IAF: todavía sin autorización.
        p.OnFinal(30, crossNm: 6);
        p.AltitudeFt = 6000;
        p.Tick(3);
        Assert.DoesNotContain(p.Heard, h => h.Contains("cleared ILS"));
        // A 3 NM del IAF (a 27 NM del umbral, no establecido): autorizado, y la altitud pasa a ser la plataforma.
        p.OnFinal(27, crossNm: 6);
        p.AltitudeFt = 6000;
        p.Tick(3);
        Assert.Contains(p.Heard, h => h.Contains("cleared ILS") && h.Contains("approach runway 17L via TESTO"));
        Answer(p);
        Assert.Contains(p.Session.Orders(), o => o.Label == "Altitud" && o.Value.Contains($"{p.Arrival.ApproachAltitudeFt:0}"));
        Assert.DoesNotContain(p.Heard, h => h.Contains("three sixty") || h.Contains("expedite"));
    }

    // PLAN-PULIDO bloque 4 (ADR-123): llegada sin transición que queda abeam del aeropuerto: vectores a la final.
    [Fact]
    public void Abeam_the_airport_without_a_transition_the_approach_gives_vectors_to_final()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 190;
        // Abeam (2 NM antes del umbral sobre el eje), 6 NM a la derecha, volando en el sentido del aterrizaje.
        p.OnFinal(2, crossNm: 6);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        var downwind = (int)(Math.Round(GeoMath.NormalizeDeg(p.Course + 180) / 5) * 5);
        var said = p.Tick(2);
        Assert.Contains(said, h => h.Contains($"heading {downwind:000}, vectoring for ILS") && h.Contains("approach runway 17L"));
        Assert.True(p.Session.Vectoring);
        // Colación con otro rumbo: la corrige.
        Assert.Contains(p.Say($"Turn left heading {(downwind + 40) % 360:000}, LAN205"), h => h.Contains("negative") && h.Contains($"heading {downwind:000}"));
        Answer(p);
        Assert.Contains(p.Session.Orders(), o => o.Label == "Rumbo" && o.Value.StartsWith($"{downwind:000}", StringComparison.Ordinal));
        // En el alejamiento a 6 NM del umbral: nada todavía, y sin autorización aunque esté a menos de 18 NM.
        p.OnFinal(6, crossNm: 6);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 180);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        Assert.Empty(p.Tick(3));
        // A 12 NM: base, hacia el eje.
        p.OnFinal(12, crossNm: 6);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 180);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        var baseHdg = (int)(Math.Round(GeoMath.NormalizeDeg(p.Course - 90) / 5) * 5);
        Assert.Contains(p.Tick(2), h => h.Contains($"turn right heading {baseHdg:000}"));
        Answer(p);
        // A 2,5 NM del eje: rumbo de interceptación con la autorización.
        p.OnFinal(12, crossNm: 2.5);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course - 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        var intercept = (int)(Math.Round(GeoMath.NormalizeDeg(p.Course - 30) / 5) * 5);
        Assert.Contains(p.Tick(2), h => h.Contains($"turn right heading {intercept:000}, cleared ILS") && h.Contains("approach runway 17L"));
        Answer(p);
        Assert.False(p.Session.Vectoring);
        // Con el rumbo de interceptación el radar dibuja avión → corte con el eje → umbral (vuelo a KEWR, 2026-11-15).
        var cut = Assert.IsAssignableFrom<IReadOnlyList<GeoPoint>>(p.Session.VectorPath);
        Assert.Equal(3, cut.Count);
        var onAxis = FinalGeometry.Of(p.Threshold, p.Course, p.ElevationFt, 3, cut[1], p.Course);
        Assert.InRange(Math.Abs(onAxis.CrossNm), 0, 0.1);
        Assert.InRange(onAxis.AlongNm, 7, 9);
        // Establecido: el rumbo ya no vale y la línea se borra.
        p.OnFinal(9);
        Assert.Null(p.Session.AssignedVector);
        Assert.Null(p.Session.VectorPath);
        // Establecido: a la Torre, sin más frases de la Aproximación que el traspaso.
        p.Ias = 150;
        p.OnFinal(9);
        for (var i = 0; i < 10 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            Answer(p);
        }
        Assert.Equal(PilotPhase.ArrivalTower, p.Session.Phase);
        Assert.DoesNotContain(p.Heard, h => h.Contains("three sixty") || h.Contains("say again"));
    }

    // ADR-146: sin transición, de frente a la pista pero cruzando la final a 90° (la discontinuidad del FMS): antes se lo
    // autorizaba a 18 NM sin rumbo; ahora base hacia el eje y el rumbo de interceptación con la autorización.
    [Fact]
    public void In_front_but_crossing_the_final_without_a_transition_gets_vectors()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 210;
        p.OnFinal(15, crossNm: 8);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course - 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        var said = p.Tick(2);
        Assert.True(p.Session.Vectoring, string.Join(" | ", said));
        Assert.True((said).Any(h => h.Contains("heading")), string.Join(" | ", p.Heard));
        Assert.False((said).Any(h => h.Contains("cleared ILS")), string.Join(" | ", p.Heard));
        Answer(p);
        p.OnFinal(15, crossNm: 2.5);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        Assert.True(UntilCleared(p), string.Join(" | ", p.Heard));
        Assert.False(p.Session.Vectoring);
    }

    // ADR-146: lejos (40 NM), por delante y alejándose del eje, sin ruta por delante: directo a un punto de entrada en la
    // prolongación del eje (el radar lo dibuja), y al llegar, el rumbo de interceptación con la autorización.
    [Fact]
    public void Far_away_and_heading_away_the_approach_vectors_to_a_point_on_the_extended_centerline()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 250;
        p.OnFinal(40, crossNm: 15);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        var said = p.Tick(2);
        Assert.True((said).Any(h => h.Contains("vectoring for ILS")), string.Join(" | ", p.Heard));
        var target = Assert.NotNull(p.Session.VectorTarget);
        var gate = FinalGeometry.Of(p.Threshold, p.Course, p.ElevationFt, 3, target, p.Course);
        Assert.InRange(Math.Abs(gate.CrossNm), 0, 0.2);
        Assert.InRange(gate.AlongNm, 12, 20);
        // Lo que dibuja el radar: avión → punto de entrada → umbral.
        var path = Assert.IsAssignableFrom<IReadOnlyList<GeoPoint>>(p.Session.VectorPath);
        Assert.Equal(target, path[1]);
        Assert.True(GeoMath.DistanceM(path[^1], p.Threshold) < 1);
        Answer(p);
        p.OnFinal(gate.AlongNm, crossNm: 2);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course - 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        Assert.True(UntilCleared(p), string.Join(" | ", p.Heard));
    }

    // ADR-146: alineado y convergiendo con la final, sin transición: no hacen falta vectores; se lo autoriza a 18 NM.
    [Fact]
    public void Aligned_with_the_final_without_a_transition_needs_no_vectors()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 200;
        p.OnFinal(24, crossNm: 1);
        p.HeadingDeg = p.Course;
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        Assert.False((p.Tick(3)).Any(h => h.Contains("heading")), string.Join(" | ", p.Heard));
        Assert.False(p.Session.Vectoring);
        p.OnFinal(16);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt;
        Assert.True(UntilCleared(p), string.Join(" | ", p.Heard));
    }

    /// <summary>Colaciona lo que le pidan (velocidad, rumbos) hasta que llegue la autorización de aproximación.</summary>
    private static bool UntilCleared(Pilot p)
    {
        for (var i = 0; i < 8; i++)
        {
            if (p.Tick().Any(h => h.Contains("cleared ILS")))
                return true;
            Answer(p);
        }
        return p.Session.ApproachCleared;
    }

    // ADR-146: antes de la base, los vectores no bajan de la altitud más alta de los tramos iniciales de la carta.
    [Fact]
    public void Vectors_never_descend_below_the_initial_altitudes_of_the_chart()
    {
        var p = Make();
        var floor = PilotSession.VectorFloorFt(p.Arrival);
        Assert.True(floor >= p.Arrival.ApproachAltitudeFt);
        var published = p.Arrival.Procedure!.Transitions.SelectMany(t => t.Legs).Concat(p.Arrival.Procedure.FinalLegs)
            .Where(l => l.Altitude1Ft > 0).Select(l => l.Altitude1Ft).ToList();
        Assert.True(floor == p.Arrival.ApproachAltitudeFt || published.Any(a => Math.Abs(a - floor) < 1) || published.Count == 0,
            $"piso {floor:0} no sale de la carta");
    }

    // PLAN-PULIDO bloque 5 (ADR-124): detrás de un pesado, la Torre avisa de la estela con la autorización de aterrizaje.
    [Fact]
    public void Behind_a_heavy_the_tower_cautions_wake_turbulence_once()
    {
        var p = Make(leader: () => ("B77W", 6.0), leaderWake: SimATC.Core.Fleet.WakeCategory.Heavy);
        CheckIn(p);
        p.Ias = 145;
        for (var nm = 12.0; nm > 1; nm -= 0.1)
        {
            p.OnFinal(nm);
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));
        Assert.Single(p.Heard, h => h.Contains("caution wake turbulence, B77W 6 miles ahead"));
        var caution = p.Heard.FindIndex(h => h.Contains("caution wake turbulence"));
        Assert.Contains("cleared to land", p.Heard[caution + 1]);
    }

    // PLAN-PULIDO bloque 4 (ADR-124): se pasa del nivel autorizado y el ATC lo devuelve, una sola vez.
    [Fact]
    public void Descending_below_the_cleared_altitude_the_atc_sends_the_pilot_back_up_once()
    {
        var p = Make(tweak: Connected);
        CheckIn(p);
        p.Ias = 220;
        var cleared = p.Arrival.ApproachAltitudeFt;
        // Lejos y fuera del eje, 700 ft bajo la altitud autorizada.
        p.OnFinal(30, crossNm: 8);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 60);
        p.AltitudeFt = cleared;
        p.Tick(2);
        p.AltitudeFt = cleared - 700;
        Assert.Empty(p.Tick(10)); // todavía no: tiene que durar
        var said = p.Tick(10);
        Assert.Single(said, h => h.Contains("climb") && h.Contains($"{cleared:0}"));
        Answer(p);
        Assert.DoesNotContain(p.Tick(40), h => h.Contains("climb"));
        AssertClean(p);
    }

    // ADR-133: pedidos fuera de guion en la llegada: se repite lo vigente, nunca "say again".
    [Fact]
    public void Out_of_script_requests_on_arrival_get_the_current_instruction()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 145;
        p.OnFinal(9);
        for (var i = 0; i < 10 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            // Autorizado a la aproximación y pide descenso: "continue approach" (no se le sube la altitud autorizada).
            if (p.Session.Phase == PilotPhase.Arrival && p.Heard.Any(h => h.Contains("cleared ILS")) && !p.Session.AwaitingReadback
                && !p.Heard.Any(h => h.Contains("continue approach")) && !p.Session.AwaitingCheckIn)
                Assert.Contains(p.Say("Santiago Approach, LAN205, request descent"), h => h.Contains("continue approach"));
            Answer(p);
        }
        Assert.Equal(PilotPhase.ArrivalTower, p.Session.Phase);
        for (var nm = 6.0; nm > 3 && !p.Session.LandingCleared; nm -= 0.1)
        {
            p.OnFinal(nm);
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared);
        // "Final" otra vez con el aterrizaje ya autorizado: se lo repite.
        Assert.Contains(p.Say("Santiago Tower, LAN205, final runway 17L"), h => h.Contains("cleared to land"));
        AssertClean(p);
    }

    // Vuelo RJTT-RJSS: nivelado a la altitud del FAF antes de pasarlo no es "altitud baja", aunque quede bajo los 3° vistos de lejos.
    [Fact]
    public void Level_at_the_faf_altitude_before_the_faf_is_not_low()
    {
        var p = Make();
        var faf = p.Arrival.Procedure!.FinalLegs.First(l => l.IsFaf && l.Altitude1Ft > 0);
        var fafNm = GeoMath.DistanceM(faf.FixPosition, p.Threshold) / Nm;
        CheckIn(p);
        p.Ias = 150;
        p.OnFinal(fafNm + 4);
        for (var i = 0; i < 10 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            Answer(p);
        }
        p.OnFinal(fafNm + 2.5);
        var below = p.AltitudeFt - faf.Altitude1Ft;
        Assert.True(below > 300, $"el caso no prueba nada: la senda queda solo {below:0} ft sobre la altitud del FAF");
        p.AltitudeFt = faf.Altitude1Ft;
        Assert.DoesNotContain(p.Tick(3), h => h.Contains("low altitude"));
        // Bajo la altitud del FAF sí.
        p.AltitudeFt = faf.Altitude1Ft - 500;
        Assert.Contains(p.Tick(3), h => h.Contains("low altitude warning"));
    }

    // No se le pide reducir a quien ya viene desacelerando hacia esa velocidad.
    [Fact]
    public void Already_slowing_down_the_approach_does_not_ask_to_reduce_speed()
    {
        // Establecido a 13 NM tras venir a 215 kt: el que sigue a 215 oye "180"; el que ya bajó a 207 y sigue bajando, no.
        IReadOnlyList<string> Run(double iasOnFinal)
        {
            var p = Make(tweak: Connected);
            CheckIn(p);
            p.OnFinal(20, crossNm: 5);
            p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 60);
            p.Ias = 215;
            p.Tick(2);
            p.Now += 4;
            p.OnFinal(13);
            p.Ias = iasOnFinal;
            return p.Tick(2);
        }
        Assert.Contains(Run(215), h => h.Contains("reduce speed to 180 knots"));
        Assert.DoesNotContain(Run(207), h => h.Contains("reduce speed"));
    }

    [Fact]
    public void Fast_in_the_sequence_the_approach_reduces_speed_and_the_readback_is_checked()
    {
        var p = Make(tweak: Connected);
        CheckIn(p);
        // A 20 NM y lejos del eje (no establecido), a 250 kt: nada (ADR-121: fuera de la final manda la carta, ya no se piden
        // 210 kt por distancia). A 290 kt bajo 10 000 ft: 250 kt.
        p.OnFinal(20, crossNm: 5);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 60);
        p.Ias = 250;
        Assert.DoesNotContain(p.Tick(2), h => h.Contains("reduce speed"));
        p.Ias = 290;
        var said = p.Tick(30);
        Assert.Contains(said, h => h.Contains("reduce speed to 250 knots"));
        Assert.Contains(p.Session.Orders(), o => o.Label == "Velocidad" && o.Value.Contains("250"));
        Assert.Contains(p.Say("Reduce speed to 180 knots, LAN205"), h => h.Contains("negative, reduce speed to 250 knots"));
        Assert.Empty(p.Say("Reduce speed to 250 knots, LAN205"));
        Assert.False(p.Session.AwaitingReadback);
    }

    [Fact]
    public void Much_too_high_the_approach_gives_a_three_sixty_and_too_high_expedite_descent()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 180; // dentro de la velocidad publicada del fijo cercano (ADR-121): aquí se prueba solo la altura
        p.Vs = -800;
        // 12 NM fuera del eje a 6000 ft sobre la altitud de la aproximación: muy alto (viraje de 360°).
        p.OnFinal(12, crossNm: 3);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt + 6000;
        var said = p.Tick(2);
        Assert.Contains(said, h => h.Contains("make a three sixty turn"));
        Assert.Empty(p.SayFirstSuggestion());
        Assert.False(p.Session.AwaitingReadback);
        // Sigue alto: acelerar el descenso a la altitud autorizada (después de la pausa entre avisos).
        p.Now += PilotSession.WatchIntervalS;
        p.OnFinal(16, crossNm: 3);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 90);
        p.AltitudeFt = p.Arrival.ApproachAltitudeFt + 4000;
        var more = p.Tick(2);
        Assert.Contains(more, h => h.Contains("expedite descent"));
        Answer(p);
        Assert.DoesNotContain(p.Heard, h => h.Contains("cleared"));
        AssertClean(p);
    }

    [Fact]
    public void Too_close_to_the_arrival_ahead_the_approach_slows_the_pilot_or_gives_a_three_sixty()
    {
        var gap = 4.0;
        var p = Make(leader: () => ("A320", gap));
        CheckIn(p);
        p.OnFinal(20, crossNm: 5);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 60);
        p.Ias = 220;
        Assert.Contains(p.Tick(2), h => h.Contains("reduce speed to 180 knots"));
        Answer(p);
        // Más pegado y lejos de la final: viraje de 360° por secuencia.
        gap = 2;
        p.Now += PilotSession.WatchIntervalS;
        p.Ias = 180;
        Assert.Contains(p.Tick(2), h => h.Contains("make a three sixty turn") && h.Contains("for sequencing"));
        Answer(p);
        AssertClean(p);
    }

    /// <summary>Hasta autorizado a aterrizar en la final (establecido a 8 NM).</summary>
    private static void ClearedToLand(Pilot p)
    {
        CheckIn(p);
        p.Ias = 150;
        p.OnFinal(8);
        for (var i = 0; i < 10 && !p.Session.LandingCleared; i++)
        {
            p.Tick();
            Answer(p);
        }
        Assert.True(p.Session.LandingCleared, string.Join(" | ", p.Heard));
    }

    [Fact]
    public void Advisory_only_never_orders_a_go_around()
    {
        var p = Make(watch: ApproachWatchLevel.AdvisoryOnly);
        ClearedToLand(p);
        p.OnFinal(2.3, 900);
        Assert.DoesNotContain(p.Tick(3), h => h.Contains("go around"));
        Assert.True(p.Session.LandingCleared);
    }

    [Fact]
    public void Watch_off_gives_no_altitude_warnings()
    {
        var p = Make(watch: ApproachWatchLevel.Off);
        ClearedToLand(p);
        p.OnFinal(6, 700);
        p.Tick(2);
        p.OnFinal(4, -600);
        p.Tick(2);
        p.OnFinal(2.3, 900);
        p.Tick(2);
        Assert.DoesNotContain(p.Heard, h => h.Contains("altitude should be") || h.Contains("low altitude") || h.Contains("go around"));
    }

    [Fact]
    public void Without_a_published_path_step_downs_are_not_low_and_only_far_too_high_is_a_go_around()
    {
        var p = Make(withoutPath: true);
        ClearedToLand(p);
        // Por escalones: 600 ft bajo la referencia de 3° a 5 NM y a 2,3 NM: ni alerta ni motor y al aire.
        p.OnFinal(5, -600);
        p.Tick(2);
        p.OnFinal(2.3, -550);
        p.Tick(2);
        Assert.DoesNotContain(p.Heard, h => h.Contains("low altitude") || h.Contains("go around"));
        Assert.Contains(p.Session.Orders(), o => o.Label == "Senda (ref. 3°)");

        // 1500 ft sobre la referencia en la puerta: no estabilizado igual.
        var q = Make(withoutPath: true);
        ClearedToLand(q);
        q.OnFinal(2.3, 1500);
        Assert.Contains(q.Tick(2), h => h.Contains("go around"));
    }

    [Fact]
    public void Low_on_final_the_atc_warns()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 150;
        p.OnFinal(10);
        for (var i = 0; i < 6 && p.Session.Phase != PilotPhase.ArrivalTower; i++)
        {
            p.Tick();
            Answer(p);
        }
        p.OnFinal(5, -600);
        var said = p.Tick(2);
        Assert.Contains(said, h => h.Contains("low altitude warning, check your altitude immediately, QNH 1015"));
    }

    [Fact]
    public void High_on_final_the_atc_says_the_altitude_it_should_be()
    {
        var p = Make();
        CheckIn(p);
        p.Ias = 150;
        p.OnFinal(10);
        p.Tick(2);
        Answer(p);
        p.OnFinal(6, 700);
        var said = p.Tick(2);
        Assert.Contains(said, h => h.Contains("miles from touchdown, altitude should be"));
    }

    [Fact]
    public void Orders_show_altitude_qnh_and_approach()
    {
        var p = Make(AtcLanguage.Spanish);
        CheckIn(p);
        var orders = p.Session.Orders();
        Assert.Contains(orders, o => o.Label == "Altitud" && o.Value.Contains("QNH 1015"));
        Assert.Contains(orders, o => o.Label == "Aproximación" && o.Value.Contains("17L") && o.Value.Contains("esperada"));
        Assert.Contains(orders, o => o.Label == "Frecuencia" && o.Value.Contains("119.7"));
    }

    [Fact]
    public void Resuming_with_a_pending_readback_asks_for_it_again()
    {
        var p = Make();
        CheckIn(p);
        p.OnFinal(20, crossNm: 5);
        p.HeadingDeg = GeoMath.NormalizeDeg(p.Course + 60);
        p.Ias = 290; // bajo 10 000 ft: "reduce speed to 250 knots"
        p.Tick(2);
        Assert.True(p.Session.AwaitingReadback);
        var json = FlightResume.Serialize(p.Session.Snapshot(DateTime.UtcNow));
        var snap = FlightResume.Deserialize(json)!;
        Assert.Equal(250, snap.PendingReadback?.SpeedKt);

        var q = Make();
        q.Position = p.Position;
        q.AltitudeFt = p.AltitudeFt;
        q.Session.Resume(snap, atStand: false, nowS: q.Now);
        Assert.True(q.Session.AwaitingReadback);
        Assert.StartsWith("Reduce speed to 250 knots", q.Session.Suggestions()[0].Text, StringComparison.Ordinal);
        Assert.Empty(q.SayFirstSuggestion());
        Assert.False(q.Session.AwaitingReadback);
    }

    [Fact]
    public void Final_geometry_along_cross_and_profile()
    {
        var thr = new GeoPoint(-33.38, -70.79); // TEST DATA
        var at5 = GeoMath.Destination(thr, 357, 5 * Nm);
        var f = FinalGeometry.Of(thr, 177, 1500, 3, at5, 177);
        Assert.Equal(5, f.AlongNm, 2);
        Assert.Equal(0, f.CrossNm, 2);
        Assert.Equal(1500 + 50 + 5 * 6076.12 * Math.Tan(3 * Math.PI / 180), f.ProfileFt, 0);
        Assert.True(f.Established);
        // A la derecha mirando a la pista (al oeste, rumbo 177): desvío positivo.
        var right = GeoMath.Destination(at5, 267, 0.5 * Nm);
        Assert.True(FinalGeometry.Of(thr, 177, 1500, 3, right, 177).CrossNm > 0.4);
        // Pasado el umbral, alejándose: no establecido.
        Assert.False(FinalGeometry.Of(thr, 177, 1500, 3, GeoMath.Destination(thr, 177, 2 * Nm), 177).Established);
    }

    [Theory]
    [InlineData("Reduciendo a 210 nudos, LAN205", ReadbackVerdict.Correct)]
    [InlineData("Reduce speed to 210 knots, LAN205", ReadbackVerdict.Correct)]
    [InlineData("Reduce speed to 220 knots, LAN205", ReadbackVerdict.Wrong)]
    [InlineData("Roger, LAN205", ReadbackVerdict.Missing)]
    public void Speed_readbacks(string text, ReadbackVerdict verdict)
    {
        var e = new ExpectedReadback("LAN205") { SpeedKt = 210 }; // TEST DATA
        Assert.Equal(verdict, ReadbackChecker.Check(MessageParser.Parse(text, ["lan205", "lan 205"]), e, ReadbackStrictness.Realistic).Verdict);
    }
}
