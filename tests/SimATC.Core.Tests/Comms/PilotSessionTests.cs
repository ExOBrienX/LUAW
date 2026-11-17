using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>Salida del piloto con Delivery y Ground (hito 4c, parte 2), con los datos reales de SCEL y su archivo ATC.</summary>
public class PilotSessionTests
{
    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));
    private static readonly Lazy<TaxiGraph> Graph = new(() => TaxiGraph.Build(Scel.Value));

    /// <summary>Lo que el ATC ve en vivo del piloto y de la pista (en la app vienen del simulador y de la Torre del motor).</summary>
    private sealed class Live
    {
        public GeoPoint? Position;
        public bool OnGround = true;
        public double AltitudeFt = 1600;
        public double GroundSpeedKt;
        public PilotRunwayStatus? Runway;
        public List<(int Runway, PilotTowerClearance Clearance)> TowerCalls = [];
    }

    private static (PilotSession S, List<string> Log) Session(ReadbackStrictness strictness = ReadbackStrictness.Realistic) =>
        Session(strictness, null);

    private static (PilotSession S, List<string> Log) Session(ReadbackStrictness strictness, Live? live, AirlineTelephony? airlines = null,
        Action<GeoPoint?>? setPriority = null, AtcLanguage language = AtcLanguage.English, Func<GeoPoint, AtcStation?>? centerAt = null,
        bool withoutApproach = false)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(dataDir, "SCEL"));
        // TEST DATA: plan con la forma del de SimBrief (SCEL 17L → SCIE por GELU5B).
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", PlannedRunway: "17L"),
            new PlanAirport("SCIE", PlannedRunway: "02"), 24000, "GELU5B GELUS UQ809 ARUNI ARUN1A",
            [new PlanFix("GELUS", "wpt", "UQ809", false, new GeoPoint(-33.915, -71.203), 23700)]) { SidIdent = "GELU5B" };
        var gate = Scel.Value.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == 14).Position;
        var log = new List<string>();
        live ??= new Live();
        live.Position ??= gate;
        // Sin Approach ni Departure en el escenario ni en el AIP (como EGLL en MSFS 2024, ADR-140).
        var stations = withoutApproach
            ? new AtcStations(Scel.Value with { Frequencies = [.. Scel.Value.Frequencies.Where(f => f.Type is not (FrequencyType.Approach
                or FrequencyType.Departure or FrequencyType.Center))] }, null, language)
            : new AtcStations(Scel.Value, atc, language);
        var ctx = new DepartureContext(plan, Scel.Value, atc, stations, Graph.Value,
            Phraseology.Load(Phraseology.PathFor(dataDir, language), new Random(3)), () => ["17R"], () => 'A', () => live.Position, () => strictness,
            RunwayStatus: () => live.Runway, SetTowerClearance: (r, c) => live.TowerCalls.Add((r, c)),
            OnGround: () => live.OnGround, AltitudeFt: () => live.AltitudeFt, Wind: () => "190 degrees 11 knots",
            GroundSpeedKt: () => live.GroundSpeedKt, Airlines: airlines, SetPilotPriority: setPriority, Language: language, CenterAt: centerAt);
        return (new PilotSession(ctx, new Random(5), log.Add), log);
    }

    private const int Delivery = 136700;
    private const int Ground = 122200;

    /// <summary>Hasta rodando con la ruta colacionada, y el piloto en el punto de la ruta a <paramref name="alongM"/> m.</summary>
    private static (PilotSession S, Live Live, PilotTaxiRoute Route) Taxiing(double alongM)
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 8);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
        live.Position = Along(s.TaxiRoute, alongM, out _);
        live.GroundSpeedKt = 12;
        return (s, live, s.TaxiRoute);
    }

    /// <summary>El punto de la ruta a <paramref name="m"/> metros del inicio, con el rumbo del tramo.</summary>
    private static GeoPoint Along(PilotTaxiRoute route, double m, out double headingDeg)
    {
        var pts = route.Points;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var len = GeoMath.DistanceM(pts[i], pts[i + 1]);
            headingDeg = GeoMath.InitialBearingDeg(pts[i], pts[i + 1]);
            if (m <= len)
                return GeoMath.Destination(pts[i], headingDeg, m);
            m -= len;
        }
        headingDeg = 0;
        return pts[^1];
    }

    /// <summary>
    /// Prueba de Marco 2026-11-09: LUAW se cierra rodando; al retomar sigue rodando con la misma autorización, la ruta
    /// rehecha desde donde está y con Ground, sin volver a pedir nada.
    /// </summary>
    [Fact]
    public void Resume_after_a_crash_while_taxiing_keeps_the_clearance_and_the_route()
    {
        var (s, live, _) = Taxiing(150);
        var json = FlightResume.Serialize(s.Snapshot(DateTime.UtcNow));
        var (again, _) = Session(ReadbackStrictness.Permissive, live);
        var station = again.Resume(FlightResume.Deserialize(json)!, atStand: false);
        Assert.Equal(PilotPhase.Taxiing, again.Phase);
        Assert.Equal(AtcRole.Ground, station!.Role);
        Assert.Equal(s.Clearance!.Squawk, again.Clearance!.Squawk);
        Assert.Equal("17R", again.Clearance.Runway);
        Assert.NotNull(again.TaxiRoute);
        Assert.Contains(live.TowerCalls, c => c.Clearance == PilotTowerClearance.None);
    }

    /// <summary>Retomado en el punto de espera: con la Torre, y "listo para salir" funciona (antes respondía "say again").</summary>
    [Fact]
    public void Resume_at_the_holding_point_goes_to_tower_and_ready_for_departure_works()
    {
        var (s, live, route) = Taxiing(150);
        var snap = s.Snapshot(DateTime.UtcNow) with { Phase = PilotPhase.HoldShortGiven };
        live.Position = route.HoldShort;
        live.GroundSpeedKt = 0;
        var (again, _) = Session(ReadbackStrictness.Permissive, live);
        var tower = again.Resume(snap, atStand: false)!;
        Assert.Equal(PilotPhase.WithTower, again.Phase);
        Assert.Equal(AtcRole.Tower, tower.Role);
        var reply = again.OnPilotMessage("LAN205 holding point runway 17R ready for departure", tower.Khz, 20);
        Assert.NotEmpty(reply);
        Assert.DoesNotContain(reply, r => r.Text.Contains("say again", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Despegó con LUAW cerrado: retoma en el aire y llama a Salida (primer contacto).</summary>
    [Fact]
    public void Resume_airborne_after_leaving_on_the_ground_calls_departure()
    {
        var (s, live, _) = Taxiing(150);
        var snap = s.Snapshot(DateTime.UtcNow);
        live.OnGround = false;
        live.AltitudeFt = 6000;
        var (again, _) = Session(ReadbackStrictness.Permissive, live);
        var next = again.Resume(snap, atStand: false);
        Assert.Equal(PilotPhase.Airborne, again.Phase);
        Assert.Equal(again.DepartureStation, next);
    }

    [Fact]
    public void Ground_tells_the_pilot_to_follow_traffic_ahead_on_the_route()
    {
        var (s, _, route) = Taxiing(100);
        var p = Along(route, 250, out var hdg);
        var t = new GroundTraffic(7, "SKU101", "A320", p, hdg, 14);
        var advisory = s.Traffic([t], 30).Single();
        Assert.Equal("Santiago Ground", advisory.Station.Callsign);
        Assert.Equal("LAN205, follow the A320 ahead", advisory.Text);
        Assert.Equal("Follow the A320 ahead, LAN205", s.Suggestions()[0].Text);
        // Colacionado: sin respuesta, y no lo vuelve a avisar por el mismo avión.
        Assert.Empty(s.OnPilotMessage("Follow the A320 ahead, LAN205", Ground, 33));
        Assert.True(s.LastReadbackAccepted);
        Assert.Empty(s.Traffic([t], 80));
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
    }

    [Fact]
    public void Hold_position_when_traffic_is_close_ahead_and_taxi_again_when_it_clears()
    {
        var (s, live, route) = Taxiing(100);
        var p = Along(route, 140, out var hdg);
        var t = new GroundTraffic(7, "SKU101", "A320", p, hdg, 0);
        Assert.Equal("LAN205, hold position", s.Traffic([t], 30).Single().Text);
        Assert.Empty(s.OnPilotMessage("Holding, LAN205", Ground, 32));
        live.GroundSpeedKt = 0;
        Assert.Empty(s.Traffic([t], 40)); // sigue ahí: mantiene
        // Se fue: el rodaje otra vez (CAP 413 no tiene "continue taxi"), con colación.
        var again = s.Traffic([], 50).Single();
        Assert.Matches("^LAN205, continue taxi(ing)?$", again.Text); // sin repetir toda la ruta (prueba de Marco)
        Assert.Equal("Continue taxi, LAN205", s.Suggestions()[0].Text);
        Assert.Empty(s.OnPilotMessage("Continue taxi, LAN205", Ground, 53));
        Assert.True(s.LastReadbackAccepted);
    }

    [Fact]
    public void Give_way_to_traffic_crossing_the_route_ahead()
    {
        var (s, _, route) = Taxiing(100);
        // Un IA a 80 m a un lado del punto de la ruta a 180 m, rodando hacia la ruta.
        var x = Along(route, 180, out var hdg);
        var p = GeoMath.Destination(x, hdg - 90, 80);
        var t = new GroundTraffic(9, "LAN44", "B737", p, GeoMath.NormalizeDeg(hdg + 90), 15);
        var advisory = s.Traffic([t], 30).Single();
        Assert.Equal("LAN205, give way to the B737 passing left to right", advisory.Text);
        Assert.Empty(s.OnPilotMessage("Give way to the B737, LAN205", Ground, 33));
        Assert.True(s.LastReadbackAccepted);
    }

    /// <summary>Un IA a 80 m a la izquierda del punto de la ruta a <paramref name="atM"/> m, rodando hacia la ruta.</summary>
    private static GroundTraffic CrossingTraffic(PilotTaxiRoute route, uint id, double atM)
    {
        var x = Along(route, atM, out var hdg);
        return new GroundTraffic(id, $"SKU{id}", "B787", GeoMath.Destination(x, hdg - 90, 80), GeoMath.NormalizeDeg(hdg + 90), 15);
    }

    /// <summary>
    /// Prueba de Marco (2026-10-31): en H con P le pasaban IA sin fin y siempre "give way". Después de ceder dos veces,
    /// detenido, Ground detiene a los que vienen (prioridad en el cruce) y le repite el rodaje.
    /// </summary>
    [Fact]
    public void After_giving_way_twice_ground_gives_the_pilot_priority_and_taxi_again()
    {
        var calls = new List<GeoPoint?>();
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live, setPriority: calls.Add);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 8);
        var route = s.TaxiRoute;
        live.Position = Along(route, 100, out _);
        live.GroundSpeedKt = 0; // detenido, cediendo

        Assert.Contains("give way", s.Traffic([CrossingTraffic(route, 1, 180)], 30).Single().Text);
        s.OnPilotMessage("Give way to the B787, LAN205", Ground, 32);
        Assert.Contains("give way", s.Traffic([CrossingTraffic(route, 2, 180)], 55).Single().Text);
        s.OnPilotMessage("Give way to the B787, LAN205", Ground, 57);
        Assert.Empty(calls);

        // Sigue viniendo otro: Ground le da el paso (los IA frenan) y, unos segundos después, el rodaje otra vez.
        var third = CrossingTraffic(route, 3, 180);
        Assert.Empty(s.Traffic([third], 55 + PilotSession.PrioritySettleS));
        Assert.Single(calls);
        Assert.NotNull(calls[0]);
        var again = s.Traffic([third], 55 + 2 * PilotSession.PrioritySettleS).Single();
        Assert.Contains("continue taxi", again.Text);
        Assert.DoesNotContain(route.ViaText, again.Text);
        s.OnPilotMessage("Continue taxi, LAN205", Ground, 80);
        Assert.True(s.LastReadbackAccepted);

        // Pasa el cruce: la prioridad se retira.
        live.Position = Along(route, 260, out _);
        live.GroundSpeedKt = 12;
        s.Traffic([], 90);
        Assert.Null(calls[^1]);
    }

    [Fact]
    public void When_nobody_else_crosses_ground_tells_the_pilot_to_taxi_again()
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 8);
        var route = s.TaxiRoute;
        live.Position = Along(route, 100, out _);
        live.GroundSpeedKt = 0;
        Assert.Contains("give way", s.Traffic([CrossingTraffic(route, 1, 180)], 30).Single().Text);
        s.OnPilotMessage("Give way to the B787, LAN205", Ground, 32);
        Assert.Empty(s.Traffic([], 35)); // todavía muy pronto
        Assert.Contains("continue taxi", s.Traffic([], 30 + PilotSession.ClearAfterS).Single().Text);
    }

    /// <summary>Si el que pasó va ahora adelante por mi ruta: "continue taxi, follow the …".</summary>
    [Fact]
    public void Continue_taxi_follows_the_traffic_that_is_now_ahead_on_the_route()
    {
        var (s, live, route) = Taxiing(100);
        live.GroundSpeedKt = 0;
        Assert.Contains("give way", s.Traffic([CrossingTraffic(route, 1, 180)], 30).Single().Text);
        s.OnPilotMessage("Give way to the B787, LAN205", Ground, 32);
        var merged = new GroundTraffic(1, "SKU1", "Boeing 787", Along(route, 200, out var hdg), hdg, 12);
        var go = s.Traffic([merged], 30 + PilotSession.ClearAfterS + 5).Single();
        Assert.Matches("^LAN205, (continue taxi, follow the Boeing 787|follow the Boeing 787, continue taxi)$", go.Text);
        Assert.Equal("Continue taxi, follow the Boeing 787, LAN205", s.Suggestions()[0].Text);
        Assert.Empty(s.OnPilotMessage("Continue taxi, follow the Boeing 787, LAN205", Ground, 60));
        Assert.True(s.LastReadbackAccepted);
    }

    [Fact]
    public void Ground_tells_the_pilot_to_taxi_slower_when_closing_on_traffic_ahead()
    {
        var (s, live, route) = Taxiing(100);
        live.GroundSpeedKt = 18;
        var t = new GroundTraffic(7, "SKU101", "A320", Along(route, 210, out var hdg), hdg, 6);
        var caution = s.Traffic([t], 30).Single();
        Assert.Matches("^LAN205, caution, A320 ahead", caution.Text);
        Assert.Equal("Wilco, LAN205", s.Suggestions()[0].Text);
        Assert.Empty(s.OnPilotMessage("Wilco, LAN205", Ground, 33));
        Assert.True(s.LastReadbackAccepted);
        // Una vez por avión; después, si lo sigue, el aviso normal.
        Assert.DoesNotContain("caution", s.Traffic([t], 30 + PilotSession.AdvisoryGapS).SingleOrDefault()?.Text ?? "");
    }

    [Fact]
    public void Traffic_advisories_are_spaced_and_ignore_far_or_parked_aircraft()
    {
        var (s, _, route) = Taxiing(100);
        var far = new GroundTraffic(1, "SKU1", "A320", Along(route, 700, out var h1), h1, 15);
        var parked = new GroundTraffic(2, "SKU2", "A320", Along(route, 300, out var h2), h2, 0);
        Assert.Empty(s.Traffic([far, parked], 30));
        var ahead = new GroundTraffic(3, "SKU3", "A321", Along(route, 260, out var h3), h3, 14);
        Assert.Single(s.Traffic([ahead], 31));
        s.OnPilotMessage("Follow the A321 ahead, LAN205", Ground, 33);
        var other = new GroundTraffic(4, "SKU4", "A320", Along(route, 320, out var h4), h4, 14);
        Assert.Empty(s.Traffic([other], 31 + PilotSession.AdvisoryGapS - 1)); // muy pronto
        Assert.Single(s.Traffic([other], 31 + PilotSession.AdvisoryGapS));
    }

    [Theory]
    [InlineData("A20N", "Airbus A320")]
    [InlineData("b38m", "Boeing 737")]
    [InlineData("B77W", "Boeing 777")]
    [InlineData("C172", "C172")]
    [InlineData("", "aircraft")]
    public void Aircraft_types_are_said_by_family(string designator, string spoken) =>
        Assert.Equal(spoken, SimATC.Core.Fleet.AircraftNames.Spoken(designator));

    [Fact]
    public void An_unknown_type_is_said_in_the_atc_language() =>
        Assert.Equal("aeronave", SimATC.Core.Fleet.AircraftNames.Spoken(null, AtcLanguage.Spanish));

    [Fact]
    public void The_pilot_reads_back_with_the_airline_telephony_and_suggestions_say_it()
    {
        var airlines = AirlineTelephony.Load(AirlineTelephony.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")));
        var (s, _) = Session(ReadbackStrictness.Realistic, null, airlines);
        Assert.StartsWith("LAN205", s.OnPilotMessage("Santiago Delivery, LAN CHILE 205, request IFR clearance to SCIE", Delivery, 0).Single().Text);
        // El chat muestra el indicativo escrito; la forma hablada lleva la telefonía (opción A del hito 4e-3).
        var suggestion = s.Suggestions()[0];
        Assert.EndsWith("LAN205", suggestion.Text);
        Assert.EndsWith("LAN CHILE two zero fife", suggestion.Spoken);
        var sq = s.Clearance!.Squawk.ToString("0000");
        Assert.Contains("correct", s.OnPilotMessage(
            $"Cleared to SCIE via GELU5B, runway 17R, climb FL100, squawk {sq}, after departure Santiago Approach 119.7, lan chile two zero five",
            Delivery, 5).Single().Text);
    }

    /// <summary>Corre el reloj del copiloto de 0,5 en 0,5 s hasta que hable (o hasta <paramref name="maxS"/>).</summary>
    private static (CopilotAction? Action, double At) RunCopilot(Copilot cp, PilotSession s, CopilotSettings set, double fromS, double maxS = 10)
    {
        for (var t = fromS; t <= fromS + maxS; t += 0.5)
        {
            if (cp.Tick(s, set, t, atcTalking: false) is { } a)
                return (a, t);
        }
        return (null, fromS + maxS);
    }

    [Fact]
    public void Copilot_reads_back_complete_after_a_human_pause_and_only_once()
    {
        var (s, _) = Session();
        var cp = new Copilot(new Random(1));
        var all = new CopilotSettings(CopilotReadbacks.Always, false, false, false);
        s.OnPilotMessage("Santiago Delivery, LAN205, request IFR clearance to SCIE", Delivery, 0);

        var (a, at) = RunCopilot(cp, s, all, 1);
        Assert.NotNull(a);
        Assert.True(a.IsReadback);
        Assert.Equal("Santiago Delivery", a.Station.Callsign);
        Assert.InRange(at - 1, Copilot.MinDelayS, Copilot.MaxDelayS + 0.5);
        Assert.Contains("correct", s.OnPilotMessage(a.Text, Delivery, at).Single().Text); // colación completa y correcta
        Assert.Equal(PilotPhase.Cleared, s.Phase);
        // Sin llamados de rutina, no pide nada más.
        Assert.Null(RunCopilot(cp, s, all, at + 1).Action);
    }

    [Fact]
    public void Copilot_waits_while_atc_talks_and_is_cancelled_when_the_pilot_writes()
    {
        var (s, _) = Session();
        var cp = new Copilot(new Random(1));
        var all = new CopilotSettings(CopilotReadbacks.Always, false, false, false);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        for (var t = 0.0; t < 20; t += 0.5)
            Assert.Null(cp.Tick(s, all, t, atcTalking: true)); // el ATC sigue hablando: nada
        // El piloto escribe él: el copiloto no lo repite.
        cp.PilotSpoke(s, all);
        s.OnPilotMessage("Cleared SCIE, LAN205", Delivery, 21); // incompleta: el ATC pide el elemento que falta
        Assert.Equal(PilotPhase.ClearanceGiven, s.Phase);
        Assert.Null(RunCopilot(cp, s, all, 22).Action); // la colación sigue pendiente, pero la lleva el piloto
    }

    [Fact]
    public void Copilot_only_frequency_changes_and_rescue_at_how_do_you_read()
    {
        var (s, _) = Session();
        var cp = new Copilot(new Random(1));
        var freq = new CopilotSettings(CopilotReadbacks.FrequencyChanges, false, Rescue: false, false);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        Assert.Null(RunCopilot(cp, s, freq, 1).Action); // la autorización no es un cambio de frecuencia

        // Con rescate: contesta solo después del "how do you read".
        var rescue = freq with { Rescue = true };
        Assert.Null(RunCopilot(cp, s, rescue, 1).Action);
        s.Tick(PilotSession.TextRecallAfterS);
        s.Tick(2 * PilotSession.TextRecallAfterS);
        Assert.Equal(2, s.Recalls);
        var (a, at) = RunCopilot(cp, s, rescue, 2 * PilotSession.TextRecallAfterS + 1);
        Assert.NotNull(a);
        Assert.Contains("correct", s.OnPilotMessage(a.Text, Delivery, at).Single().Text);
    }

    [Fact]
    public void Copilot_makes_the_routine_calls_but_not_taxi_before_the_pushback_ends()
    {
        var (s, _) = Session();
        var cp = new Copilot(new Random(1));
        var full = new CopilotSettings(CopilotReadbacks.Always, RoutineCalls: true, false, false);
        var t = 0.0;
        void Say(CopilotAction a)
        {
            s.OnPilotMessage(a.Text, a.Station.Khz, t);
        }
        var (a1, t1) = RunCopilot(cp, s, full, t);
        Assert.Contains("request IFR clearance", a1!.Text);
        Assert.True(a1.Ask); // decisión del capitán: el copiloto pregunta (prueba de Marco, 2026-11-05)
        t = t1; Say(a1);
        var (a2, t2) = RunCopilot(cp, s, full, t + 1);
        Assert.True(a2!.IsReadback);
        Assert.False(a2.Ask);
        t = t2; Say(a2);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
        var (a3, t3) = RunCopilot(cp, s, full, t + 1);
        Assert.Contains("pushback", a3!.Text); // en una puerta: primero el pushback
        Assert.Equal("Santiago Ground", a3.Station.Callsign);
        t = t3; Say(a3);
        Assert.Equal(PilotPhase.PushbackApproved, s.Phase);
        Assert.Null(RunCopilot(cp, s, full, t + 1, maxS: 30).Action); // el piloto todavía empuja
        s.MarkPushedBack();
        var (a4, _) = RunCopilot(cp, s, full, t + 40);
        Assert.Contains("request taxi", a4!.Text);
        // El rodaje ya no es una pregunta: lo pide solo con el pushback terminado (PLAN-PULIDO bloque 1)…
        Assert.False(a4.Ask);
        // …salvo que el piloto quiera que le pregunte también por eso.
        var (a5, _) = RunCopilot(new Copilot(new Random(1)), s, full with { AskTaxi = true }, t + 80);
        Assert.True(a5!.Ask);
    }

    [Fact]
    public void Copilot_asks_only_for_clearance_and_pushback_by_default_and_can_be_told_not_to_ask()
    {
        var (s, _) = Session();
        var noAsk = new CopilotSettings(CopilotReadbacks.Always, RoutineCalls: true, false, false, AskClearance: false, AskPushback: false);
        var (a1, _) = RunCopilot(new Copilot(new Random(1)), s, noAsk, 0);
        Assert.Contains("request IFR clearance", a1!.Text);
        Assert.False(a1.Ask);
        Assert.True(new CopilotSettings(CopilotReadbacks.Always, true, false, false).AskClearance);
        Assert.True(new CopilotSettings(CopilotReadbacks.Always, true, false, false).AskPushback);
        Assert.False(new CopilotSettings(CopilotReadbacks.Always, true, false, false).AskReady);
    }

    [Fact]
    public void Copilot_off_says_nothing()
    {
        var (s, _) = Session();
        var cp = new Copilot(new Random(1));
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        Assert.Null(RunCopilot(cp, s, CopilotSettings.Off, 1, maxS: 200).Action);
        Assert.False(CopilotSettings.Off.AnyOn);
    }

    /// <summary>ATC en español (§8.6, etapa 5a): Doc 4444 en español, distintivos locales del AIP y colaciones en español.</summary>
    [Fact]
    public void Spanish_atc_clearance_pushback_and_taxi()
    {
        var airlines = AirlineTelephony.Load(AirlineTelephony.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")));
        var (s, _) = Session(ReadbackStrictness.Realistic, null, airlines, language: AtcLanguage.Spanish);
        Assert.Equal("Santiago Autorizaciones", s.ExpectedStation!.Callsign);
        Assert.Equal("Santiago Autorizaciones, LAN205, solicito autorización IFR a SCIE, información Alfa", s.Suggestions()[0].Text);
        Assert.Equal("Santiago Autorizaciones, LAN CHILE dos cero cinco, solicito autorización IFR a SCIE, información Alfa", s.Suggestions()[0].Spoken);

        var c = s.OnPilotMessage("Santiago Autorizaciones, LAN CHILE 205, solicito autorización IFR a SCIE, información Alfa", Delivery, 0).Single();
        foreach (var part in new[] { "LAN205", "autorizado a SCIE", "salida GELU5B", "pista 17R", "nivel de vuelo 100", "transponder", "Santiago Aproximación 119.7" })
            Assert.Contains(part, c.Text);
        // La colación sugerida, tal cual, es correcta.
        var readback = s.Suggestions()[0].Text;
        Assert.StartsWith("Autorizado a SCIE, salida GELU5B, pista 17R, ascienda a nivel de vuelo 100, transponder", readback);
        Assert.Matches("^LAN205, (colación correcta|correcto)$", s.OnPilotMessage(readback, Delivery, 5).Single().Text);
        Assert.Equal(PilotPhase.Cleared, s.Phase);

        // Retroceso y rodaje, escritos a mano en español.
        Assert.Equal("LAN205, retroceso aprobado", s.OnPilotMessage("LAN205, puesto 14, solicito retroceso", Ground, 10).Single().Text.Replace(", Santiago Control Terrestre", ""));
        var taxi = s.OnPilotMessage("Santiago Control Terrestre, LAN205, solicito rodaje", Ground, 20).Single();
        Assert.Contains("ruede a punto de espera", taxi.Text);
        Assert.Contains("pista 17R", taxi.Text);
        // Un valor equivocado se corrige en español; la colación dicha con números en palabras se acepta.
        Assert.Equal("LAN205, negativo, pista 17R", s.OnPilotMessage("ruedo a punto de espera pista uno siete izquierda, LAN205", Ground, 25).Single().Text);
        Assert.Empty(s.OnPilotMessage($"ruedo a punto de espera pista uno siete derecha vía {s.TaxiRoute!.ViaText}, lan chile dos cero cinco", Ground, 30));
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
    }

    [Fact]
    public void Spanish_say_again_and_how_do_you_read()
    {
        var (s, _) = Session(ReadbackStrictness.Realistic, null, language: AtcLanguage.Spanish);
        s.OnPilotMessage("Santiago Autorizaciones, LAN205, solicito autorización IFR a SCIE", Delivery, 0);
        Assert.Equal("LAN205, Santiago Autorizaciones", s.Tick(PilotSession.TextRecallAfterS).Single().Text);
        Assert.Equal("LAN205, Santiago Autorizaciones, ¿cómo me recibe?", s.Tick(2 * PilotSession.TextRecallAfterS).Single().Text);
        Assert.Equal("LAN205, repita", s.OnPilotMessage("LAN205 bla bla", Delivery, 100).Single().Text);
    }

    [Theory]
    [InlineData("Mantengo fuera de pista 17R, LAN205", "Mantengo fuera de pista uno siete derecha, LAN CHILE dos cero cinco")]
    [InlineData("Contacte Santiago Torre 118.1", "Contacte Santiago Torre uno uno ocho decimal uno")]
    [InlineData("Ascienda a nivel de vuelo 100", "Ascienda a nivel de vuelo uno cero cero")]
    [InlineData("Ascienda a 11500 pies", "Ascienda a uno uno mil quinientos pies")]
    [InlineData("Transponder 4729", "Transponder cuatro siete dos nueve")]
    [InlineData("Ruede a punto de espera pista 17R vía Z3, P, H, V", "Ruede a punto de espera pista uno siete derecha vía Zulu tres, Papa, Hotel, Victor")]
    public void Spanish_spoken_form(string written, string spoken)
    {
        var airlines = AirlineTelephony.Load(AirlineTelephony.PathFor(Path.Combine(AppContext.BaseDirectory, "Data")));
        Assert.Equal(spoken, SpokenForm.Of(written, airlines, AtcLanguage.Spanish));
    }

    [Theory]
    [InlineData("LAN Chile dos cero cinco", "lan chile 205")]
    [InlineData("uno uno ocho decimal uno", "118.1")]
    [InlineData("pista uno siete derecha", "pista 17r")]
    [InlineData("nivel de vuelo uno cero cero", "nivel de vuelo 100")]
    [InlineData("uno uno mil quinientos pies", "11500 pies")]
    [InlineData("cinco mil pies", "5000 pies")]
    [InlineData("Solicito autorización, por favor", "solicito autorizacion")]
    public void Spanish_input_is_normalized(string text, string normalized) =>
        Assert.Equal(normalized, InputNormalizer.Normalize(text));

    [Fact]
    public void Nobody_answers_on_a_frequency_without_atc()
    {
        var (s, _) = Session();
        Assert.Empty(s.OnPilotMessage("Santiago Delivery, LAN205, request IFR clearance to SCIE", 122800, 0));
        Assert.Equal(PilotPhase.Parked, s.Phase);
        Assert.Equal(136700, s.ExpectedStation!.Khz);
    }

    [Fact]
    public void Full_clearance_with_corrections_and_readback()
    {
        var (s, _) = Session();
        var c = s.OnPilotMessage("Santiago Delivery, LAN205, request IFR clearance to SCIE, information Alfa", Delivery, 0).Single();
        Assert.Equal("Santiago Delivery", c.Station.Callsign);
        foreach (var part in new[] { "LAN205", "SCIE", "GELU5B", "runway 17R", "flight level 100", "squawk", "Santiago Approach 119.7" })
            Assert.Contains(part, c.Text);
        Assert.Equal(PilotPhase.ClearanceGiven, s.Phase);
        var sq = s.Clearance!.Squawk.ToString("0000");

        // Falta el squawk: lo pide.
        var r1 = s.OnPilotMessage("Cleared to SCIE via GELU5B departure, runway 17R, climb flight level 100, LAN205", Delivery, 3).Single();
        Assert.Equal("LAN205, read back squawk", r1.Text);
        // Nivel equivocado: lo corrige.
        var r2 = s.OnPilotMessage($"Cleared to SCIE via GELU5B, runway 17R, climb FL120, squawk {sq}, LAN205", Delivery, 6).Single();
        Assert.Equal("LAN205, negative, climb flight level 100", r2.Text);
        // Correcta.
        var r3 = s.OnPilotMessage($"Cleared SCIE GELUS5B departure runway 17R climb FL100 squawk {sq} after departure Santiago Approach 119.7 LAN205", Delivery, 9).Single();
        Assert.Contains("correct", r3.Text);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
    }

    [Fact]
    public void Ground_pushback_and_taxi_with_route()
    {
        var (s, _) = Session(ReadbackStrictness.Permissive);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        Assert.Equal(PilotPhase.Cleared, s.Phase);

        // Pedir rodaje a Delivery: lo manda a Ground.
        var wrong = s.OnPilotMessage("LAN205 request taxi", Delivery, 5).Single();
        Assert.Contains("contact Santiago Ground", wrong.Text);
        Assert.Contains("122.2", wrong.Text);

        Assert.Contains("pushback approved", s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 7).Single().Text);
        var taxi = s.OnPilotMessage("Santiago Ground LAN205 request taxi", Ground, 60).Single();
        Assert.Contains("runway 17R", taxi.Text);
        Assert.NotNull(s.TaxiRoute);
        Assert.True(s.TaxiRoute.Points.Count > 2);
        Assert.Equal(PilotPhase.TaxiGiven, s.Phase);
        Assert.Equal("LAN205, negative, runway 17R", s.OnPilotMessage("taxi holding point runway 17L LAN205", Ground, 62).Single().Text);
        Assert.Empty(s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute.ViaText}, LAN205", Ground, 64));
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
    }

    [Fact]
    public void Calls_and_asks_how_do_you_read_when_there_is_no_readback()
    {
        var (s, _) = Session();
        var c = s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0).Single();
        Assert.Empty(s.Tick(20));
        // RF-33c: primero solo lo llama (no repite la autorización entera); después "how do you read".
        Assert.Equal("LAN205, Santiago Delivery", s.Tick(PilotSession.TextRecallAfterS).Single().Text);
        Assert.Contains("how do you read", s.Tick(2 * PilotSession.TextRecallAfterS).Single().Text);
        Assert.Empty(s.Tick(100));
        Assert.Equal(c.Text, s.OnPilotMessage("say again LAN205", Delivery, 101).Single().Text);
    }

    [Fact]
    public void The_time_to_read_back_counts_from_when_the_pilot_sees_the_message()
    {
        var (s, _) = Session();
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        // La app la muestra 5 s después (pausa del ATC): desde ahí cuenta.
        s.Heard(5);
        Assert.Empty(s.Tick(PilotSession.TextRecallAfterS));
        Assert.Equal("LAN205, Santiago Delivery", s.Tick(PilotSession.TextRecallAfterS + 5).Single().Text);
    }

    [Fact]
    public void Say_again_repeats_the_instruction_even_after_a_correct_readback()
    {
        var (s, _) = Session();
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        var sq = s.Clearance!.Squawk.ToString("0000");
        s.OnPilotMessage($"Cleared to SCIE via GELU5B, runway 17R, climb FL100, squawk {sq}, after departure Santiago Approach 119.7, LAN205", Delivery, 5);
        Assert.True(s.LastReadbackAccepted);
        // En una puerta: primero el pushback (aunque pida rodaje), después el rodaje.
        Assert.Contains("pushback approved", s.OnPilotMessage("Santiago Ground, LAN205, request taxi", Ground, 8).Single().Text);
        var taxi = s.OnPilotMessage("Santiago Ground, LAN205, request taxi", Ground, 10).Single();
        Assert.Empty(s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 15));
        Assert.True(s.LastReadbackAccepted);
        // Ya colacionada, pero el piloto pide que se la repitan (con palabras de más).
        Assert.Equal(taxi.Text, s.OnPilotMessage("santiago ground, lan205, say again the taxi route please", Ground, 30).Single().Text);
        Assert.False(s.LastReadbackAccepted);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
        Assert.Empty(s.Suggestions()); // rodando no hay nada que decir
        Assert.Null(s.NextStep().Station);
    }

    // ADR-137: el piloto vuelve a cargar el plan del mismo vuelo ya autorizado: la sesión sigue donde estaba.
    [Fact]
    public void Reloading_the_same_flight_keeps_the_clearance_and_uses_the_new_plan()
    {
        var (s, _) = Session(ReadbackStrictness.Permissive, new Live());
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        var squawk = s.Clearance!.Squawk;
        s.OnPilotMessage($"runway 17R squawk {squawk:0000} LAN205", Delivery, 2);
        var reloaded = s.FlightPlan with { Route = "GELU5B GELUS UQ809 ARUNI DCT", CruiseAltitudeFt = 26000 };
        Assert.True(PilotSession.SameFlight(s.FlightPlan, reloaded));
        s.ReplacePlan(reloaded);
        Assert.Same(reloaded, s.FlightPlan);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
        Assert.Equal(squawk, s.Clearance!.Squawk);
        Assert.False(PilotSession.SameFlight(reloaded, reloaded with { Destination = reloaded.Destination with { Icao = "SCFA" } }));
    }

    // ---------- Pedidos fuera de guion (ADR-132) ----------

    [Fact]
    public void Asking_for_the_clearance_again_repeats_the_same_one()
    {
        var (s, _) = Session(ReadbackStrictness.Permissive, new Live());
        var first = s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0).Single().Text;
        var squawk = s.Clearance!.Squawk;
        s.OnPilotMessage($"runway 17R squawk {squawk:0000} LAN205", Delivery, 2);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
        var again = s.OnPilotMessage("Santiago Delivery, LAN205, request IFR clearance to SCIE", Delivery, 30).Single().Text;
        Assert.Equal(squawk, s.Clearance.Squawk); // la misma, no una nueva
        Assert.Contains($"squawk {squawk:0000}", again);
        Assert.Contains("cleared to SCIE", again);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
        Assert.DoesNotContain("say again", first + again);
    }

    [Fact]
    public void Pushback_or_taxi_without_a_clearance_is_sent_to_delivery_and_out_of_phase_requests_never_go_back()
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        // Sin autorización de vuelo: Ground lo manda a Autorizaciones (antes: "say again").
        Assert.Contains("contact Santiago Delivery", s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 0).Single().Text);
        Assert.Contains("contact Santiago Delivery", s.OnPilotMessage("Santiago Ground, LAN205, request taxi", Ground, 2).Single().Text);
        Assert.Equal(PilotPhase.Parked, s.Phase);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 4);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 6);
        // Pushback aprobado; pedido otra vez, se confirma (no "ya lo tenía").
        Assert.Contains("pushback approved", s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 8).Single().Text);
        Assert.Contains("pushback approved", s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 60).Single().Text);
        var taxi = s.OnPilotMessage("LAN205 request taxi", Ground, 70).Single().Text;
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 75);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
        // Rodando: pedir pushback no lo devuelve a "pushback aprobado"; pedir rodaje otra vez da la ruta de nuevo.
        Assert.Contains("continue taxi", s.OnPilotMessage("LAN205 request pushback", Ground, 80).Single().Text);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);
        Assert.Contains("taxi", s.OnPilotMessage("Santiago Ground, LAN205, request taxi", Ground, 90).Single().Text);
        Assert.Contains("17R", taxi);
    }

    [Fact]
    public void Ready_for_departure_before_taxiing_is_sent_to_ground()
    {
        var (s, _) = Session(ReadbackStrictness.Permissive, new Live());
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        var tower = new AtcStations(Scel.Value, AirportAtcData.Load(AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL")))
            .For(AtcRole.Tower)!.Khz;
        Assert.Contains("contact Santiago Ground", s.OnPilotMessage("Santiago Tower, LAN205, ready for departure", tower, 10).Single().Text);
        Assert.Equal(PilotPhase.Cleared, s.Phase);
    }

    [Fact]
    public void Asking_again_while_holding_short_re_checks_the_runway()
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        live.Runway = new PilotRunwayStatus(r17, "SKU12", 0, null, false, OccupantType: "A20N");
        Assert.Contains("hold short", s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20).Single().Text);
        Assert.Empty(s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 24));
        // Sigue ocupada y pregunta: le repite por qué espera (no silencio).
        Assert.Contains("hold short", s.OnPilotMessage("Santiago Tower, LAN205, confirm clearance", tower, 40).Single().Text);
        Assert.Empty(s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 44));
        // La pista quedó libre y vuelve a avisar que está listo: ahora sí.
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false);
        Assert.Contains("cleared for take-off", s.OnPilotMessage("Santiago Tower, LAN205, ready for departure", tower, 60).Single().Text);
    }

    // ADR-135: el copiloto pregunta si la Torre lo deja esperando de más en el punto de espera.
    [Fact]
    public void The_copilot_asks_once_when_the_tower_keeps_the_pilot_waiting_too_long()
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        live.Runway = new PilotRunwayStatus(r17, "SKU12", 0, null, false, OccupantType: "A20N");
        s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20);
        s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 24);
        var copilot = new Copilot(new Random(1));
        var settings = new CopilotSettings(CopilotReadbacks.Always, true, true, false);
        string? said = null;
        for (var t = 25.0; t < 25 + Copilot.StuckAskS + 40 && said is null; t += 1)
            said = copilot.Tick(s, settings, t, atcTalking: false)?.Text;
        Assert.NotNull(said);
        Assert.Contains("confirm clearance", said);
    }

    [Fact]
    public void Radio_check_is_answered_and_the_request_menu_lists_what_can_be_asked()
    {
        var (s, _) = Session(ReadbackStrictness.Permissive, new Live());
        Assert.Equal("LAN205, Santiago Delivery, readability 5", s.OnPilotMessage("Santiago Delivery, LAN205, radio check", Delivery, 0).Single().Text);
        var parked = s.RequestMenu();
        // Lo que ya está en las sugerencias no se repite en el menú.
        Assert.DoesNotContain(parked, r => s.Suggestions().Any(x => x.Text == r.Text));
        Assert.Contains(parked, r => r.Group == PilotSession.GroupAlways && r.Text.Contains("radio check"));
        Assert.All(parked, r => Assert.NotEmpty(r.Help));
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 2);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 4);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 6);
        var pushed = s.RequestMenu();
        Assert.Contains(pushed, r => r.Group == PilotSession.GroupStuck && r.Text.Contains("request pushback"));
        Assert.Contains(pushed, r => r.Group == PilotSession.GroupStuck && r.Text.Contains("confirm clearance"));
        Assert.DoesNotContain(pushed, r => s.Suggestions().Any(x => x.Text == r.Text));
    }

    /// <summary>Hasta con la Torre en el punto de espera (listo para avisar), con la pista que ve la Torre en <paramref name="live"/>.</summary>
    private static (PilotSession S, int TowerKhz, int R17) AtTheHoldingPoint(Live live)
    {
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 8);
        live.Position = s.TaxiRoute.HoldShort;
        s.Tick(10);
        var towerKhz = new AtcStations(Scel.Value, AirportAtcData.Load(AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL")))
            .For(AtcRole.Tower)!.Khz;
        s.OnPilotMessage("Santiago Tower 118.1, LAN205", Ground, 12);
        return (s, towerKhz, PilotTaxiRoute.RunwayIndex(Scel.Value, "17R")!.Value.Index);
    }

    /// <summary>
    /// ADR-140 (vuelo EGLL-EBBR): Heathrow no trae Approach ni Departure en MSFS. Antes la sesión quedaba en "sigue la SID"
    /// todo el vuelo; ahora la Torre lo pasa al Centro de la zona y el Centro lo identifica y le da el nivel.
    /// </summary>
    [Fact]
    public void Without_approach_or_departure_the_tower_hands_off_to_the_area_center()
    {
        var live = new Live();
        var center = new AtcStation(AtcRole.Center, "Test Control", 134905); // TEST DATA
        var (s, _) = Session(ReadbackStrictness.Permissive, live, centerAt: _ => center, withoutApproach: true);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 stand 14 request pushback", Ground, 4);
        s.OnPilotMessage("LAN205 request taxi", Ground, 6);
        s.OnPilotMessage($"taxi holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 8);
        live.Position = s.TaxiRoute.HoldShort;
        s.Tick(10);
        var tower = new AtcStations(Scel.Value, null).For(AtcRole.Tower)!.Khz;
        s.OnPilotMessage($"Santiago Tower {RadioFormat.Frequency(tower)}, LAN205", Ground, 12);
        var r17 = PilotTaxiRoute.RunwayIndex(Scel.Value, "17R")!.Value.Index;
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false);
        s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20);
        s.Tick(22);
        s.OnPilotMessage("runway 17R cleared for take-off LAN205", tower, 24);
        Assert.Equal(PilotPhase.TakeoffCleared, s.Phase);

        live.OnGround = false;
        live.AltitudeFt = 3500;
        var contact = s.Tick(40).Single();
        Assert.Contains("Test Control", contact.Text);
        Assert.Contains("134.905", contact.Text);
        s.OnPilotMessage("Test Control 134.905, LAN205", tower, 45);
        Assert.Equal(center, s.ExpectedStation);
        var identified = s.OnPilotMessage("Test Control, LAN205, 3500 feet, GELU5B", center.Khz, 60).Single();
        Assert.Contains("identified", identified.Text);
        Assert.Equal(PilotPhase.WithApproach, s.Phase);
    }

    [Fact]
    public void Tower_says_how_many_depart_before_and_lines_up_behind_the_departing_traffic()
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        // Dos IA salen antes (llegaron antes al punto de espera).
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false, AheadCount: 2);
        var hold = s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20).Single();
        Assert.Equal("LAN205, hold short runway 17R, two aircraft to depart before you", hold.Text);
        Assert.Equal((r17, PilotTowerClearance.Ready), live.TowerCalls.Last()); // en la fila
        Assert.Empty(s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 24));
        Assert.Contains("salen 2 aviones", s.NextStep().Text);
        Assert.Empty(s.Tick(30));
        // El último de la fila ya está despegando: alinear detrás (condicional, CAP 413 §4.33).
        live.Runway = new PilotRunwayStatus(r17, "SKU12", 0, "SKU12", false, OccupantType: "A20N", OccupantDeparting: true);
        var behind = s.Tick(40).Single();
        Assert.Equal("LAN205, behind the departing Airbus A320, line up runway 17R behind", behind.Text);
        Assert.Equal((r17, PilotTowerClearance.LineUp), live.TowerCalls.Last());
        Assert.Equal("Behind the departing Airbus A320, line up runway 17R behind, LAN205", s.Suggestions()[0].Text);
        Assert.Empty(s.OnPilotMessage(s.Suggestions()[0].Text, tower, 44));
        Assert.Equal(PilotPhase.LinedUp, s.Phase);
    }

    // ADR-129 (vuelo LEMG-LEMD): con una llegada en final de su pista, la Torre no lo autoriza; y si ya lo autorizó pero
    // sigue fuera de la pista cuando la llegada está encima, le cancela ("hold position").
    [Fact]
    public void Tower_holds_the_pilot_for_an_arrival_on_final_and_cancels_a_clearance_he_did_not_use()
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        // Pista libre de salidas, pero una llegada a 90 s del umbral.
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false, ArrivalInS: 90);
        var hold = s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20).Single();
        Assert.Equal("LAN205, hold short of runway 17R", hold.Text);
        Assert.Empty(s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 24));
        Assert.Contains("en final", s.NextStep().Text);
        Assert.Empty(s.Tick(30));
        // La llegada aterrizó y la siguiente está lejos: autorizado a despegar.
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false, ArrivalInS: 400);
        Assert.Contains("cleared for take-off", s.Tick(40).Single().Text);
        Assert.Empty(s.OnPilotMessage("Runway 17R, cleared for take-off, LAN205", tower, 44));
        Assert.Equal((r17, PilotTowerClearance.Takeoff), live.TowerCalls.Last());
        // No entra a la pista y la siguiente llegada ya está a un minuto: la Torre le cancela y la pista queda para la llegada.
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false, ArrivalInS: 60);
        Assert.Equal("LAN205, hold position", s.Tick(200).Single().Text);
        Assert.Equal((r17, PilotTowerClearance.Ready), live.TowerCalls.Last());
        Assert.Equal(PilotPhase.HoldShortGiven, s.Phase);
        Assert.Empty(s.OnPilotMessage("Holding, LAN205", tower, 204));
    }

    [Theory]
    [InlineData("lan205 pista 17R solicitud de despegue")]
    [InlineData("solicitud de despegue por pista 17R, lan205")]
    public void Asking_for_take_off_in_plain_words_counts_as_ready(string text)
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false);
        Assert.Contains("cleared for take-off", s.OnPilotMessage(text, tower, 20).Single().Text);
    }

    [Fact]
    public void Tower_says_who_is_on_the_runway_when_the_pilot_must_hold()
    {
        var live = new Live();
        var (s, tower, r17) = AtTheHoldingPoint(live);
        // Un IA alineándose (no despegando todavía): espera, con el tráfico.
        live.Runway = new PilotRunwayStatus(r17, "SKU12", 0, null, false, OccupantType: "B38M");
        var hold = s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", tower, 20).Single();
        Assert.Matches("^LAN205, hold short runway 17R, (traffic )?Boeing 737 on the runway$", hold.Text);
        // Todavía no llega a su punto de espera: aunque la pista esté libre, no se alinea.
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false, AtHoldingPoint: false);
        s.OnPilotMessage("Holding short runway 17R, LAN205", tower, 24);
        Assert.Empty(s.Tick(30));
        Assert.Contains("Sigue hasta el punto de espera", s.NextStep().Text);
    }

    [Fact]
    public void Tower_sequences_the_pilot_with_the_runway_and_hands_over_to_approach()
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Realistic, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        var sq = s.Clearance!.Squawk.ToString("0000");
        s.OnPilotMessage($"Cleared to SCIE via GELU5B, runway 17R, climb FL100, squawk {sq}, after departure Santiago Approach 119.7, LAN205", Delivery, 5);
        s.OnPilotMessage("Santiago Ground, LAN205, stand 14, request pushback", Ground, 10);
        s.OnPilotMessage("Santiago Ground, LAN205, request taxi", Ground, 20);
        var r17 = PilotTaxiRoute.RunwayIndex(Scel.Value, "17R")!.Value.Index;
        Assert.Equal((r17, PilotTowerClearance.None), live.TowerCalls.Last()); // la Torre ya sigue la pista del piloto
        s.OnPilotMessage($"Taxi to holding point runway 17R via {s.TaxiRoute!.ViaText}, LAN205", Ground, 25);
        Assert.Equal(PilotPhase.Taxiing, s.Phase);

        // Rodando lejos: nada. Cerca del punto de espera: Ground lo pasa a Torre.
        Assert.Empty(s.Tick(40));
        live.Position = s.TaxiRoute.HoldShort;
        var handoff = s.Tick(60).Single();
        Assert.Equal("Santiago Ground", handoff.Station.Callsign);
        Assert.Contains("Santiago Tower", handoff.Text);
        Assert.Equal(PilotPhase.TowerHandoffGiven, s.Phase);
        var towerKhz = new AtcStations(Scel.Value, AirportAtcData.Load(AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL")))
            .For(AtcRole.Tower)!.Khz;
        Assert.Empty(s.OnPilotMessage($"Santiago Tower {RadioFormat.Frequency(towerKhz)}, LAN205", Ground, 65));
        Assert.Equal(PilotPhase.WithTower, s.Phase);

        // Listo, pero un IA está en la pista: "hold short"; cuando se libera, la Torre lo llama sola.
        live.Runway = new PilotRunwayStatus(r17, "SKU123", 0, null, false);
        var hold = s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", towerKhz, 70).Single();
        Assert.Contains("hold short", hold.Text);
        Assert.Empty(s.OnPilotMessage("Holding short runway 17R, LAN205", towerKhz, 75));
        Assert.Empty(s.Tick(80));
        // Libre pero el IA todavía cerca (separación): alinear y esperar.
        live.Runway = new PilotRunwayStatus(r17, null, 0, "SKU123", false);
        Assert.Contains("line up and wait", s.Tick(90).Single().Text);
        Assert.Equal((r17, PilotTowerClearance.LineUp), live.TowerCalls.Last());
        Assert.Empty(s.OnPilotMessage("Line up and wait runway 17R, LAN205", towerKhz, 95));
        Assert.Equal(PilotPhase.LinedUp, s.Phase);
        Assert.Empty(s.Tick(100));
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false);
        var takeoff = s.Tick(110).Single();
        Assert.Contains("cleared for take-off", takeoff.Text);
        Assert.Contains("190 degrees 11 knots", takeoff.Text);
        Assert.Equal((r17, PilotTowerClearance.Takeoff), live.TowerCalls.Last());
        Assert.Empty(s.OnPilotMessage("Runway 17R, cleared for take-off, LAN205", towerKhz, 115));
        Assert.Equal(PilotPhase.TakeoffCleared, s.Phase);

        // En el aire: como la autorización ya dijo "after departure contact Approach", la Torre no habla.
        live.OnGround = false;
        live.AltitudeFt = 3540;
        Assert.Empty(s.Tick(140));
        Assert.True(s.IsAirborne);
        Assert.Equal("Santiago Approach", s.ExpectedStation!.Callsign);
        var checkIn = s.Suggestions()[0].Text;
        Assert.Equal("Santiago Approach, LAN205, 3500 feet, GELU5B", checkIn);
        var identified = s.OnPilotMessage(checkIn, 119700, 150).Single();
        Assert.Contains("identified", identified.Text);
        Assert.Contains("flight level 100", identified.Text);
        Assert.Empty(s.OnPilotMessage("Climb flight level 100, LAN205", 119700, 155));
        Assert.True(s.LastReadbackAccepted);
        Assert.Equal(PilotPhase.WithApproach, s.Phase);
    }

    [Fact]
    public void After_the_ground_crew_pushback_taxi_starts_where_the_aircraft_is()
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        Assert.Contains("pushback approved", s.OnPilotMessage("LAN205 request pushback", Ground, 5).Single().Text);
        Assert.Contains("automático", s.NextStep().Text);
        // El remolque lo dejó sobre la calle más cercana al puesto.
        var gate = live.Position!.Value;
        var (na, _, _) = Graph.Value.TaxiEdgesNear(Graph.Value.Projection.ToLocal(gate), 150).First();
        live.Position = Graph.Value.Projection.ToGeo(Graph.Value.Position(na));
        s.MarkPushedBack();
        Assert.Contains("Pide rodaje", s.NextStep().Text);
        var taxi = s.OnPilotMessage("LAN205 request taxi", Ground, 60).Single();
        Assert.Contains("taxi", taxi.Text);
        Assert.Equal(PilotPhase.TaxiGiven, s.Phase);
    }

    [Fact]
    public void Tower_clears_for_takeoff_at_once_when_the_runway_is_clear()
    {
        var live = new Live();
        var (s, _) = Session(ReadbackStrictness.Permissive, live);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        s.OnPilotMessage($"runway 17R squawk {s.Clearance!.Squawk:0000} LAN205", Delivery, 2);
        s.OnPilotMessage("LAN205 request pushback", Ground, 5);
        s.OnPilotMessage("LAN205 request taxi", Ground, 8);
        s.OnPilotMessage("taxi runway 17R LAN205", Ground, 10);
        var r17 = PilotTaxiRoute.RunwayIndex(Scel.Value, "17R")!.Value.Index;
        live.Runway = new PilotRunwayStatus(r17, null, 0, null, false);
        // Lejos del punto de espera: que espere antes de la pista.
        live.Position = s.TaxiRoute!.Points[1];
        var towerKhz = new AtcStations(Scel.Value, AirportAtcData.Load(AirportAtcData.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), "SCEL")))
            .For(AtcRole.Tower)!.Khz;
        if (GeoMath.DistanceM(live.Position.Value, s.TaxiRoute.HoldShort) > 2 * PilotSession.TowerHandoffM)
            Assert.Contains("hold short", s.OnPilotMessage("Santiago Tower, LAN205, ready for departure", towerKhz, 20).Single().Text);
        live.Position = s.TaxiRoute.HoldShort;
        var r = s.OnPilotMessage("Santiago Tower, LAN205, holding point runway 17R, ready for departure", towerKhz, 30).Single();
        Assert.Contains("cleared for take-off", r.Text);
        Assert.Equal(PilotPhase.TakeoffGiven, s.Phase);
    }

    [Fact]
    public void Suggestions_follow_the_phase_with_spoken_form()
    {
        var (s, _) = Session();
        var first = s.Suggestions()[0];
        Assert.Equal("Santiago Delivery, LAN205, request IFR clearance to SCIE, information Alfa", first.Text);
        Assert.Contains("LAN two zero fife", first.Spoken);
        s.OnPilotMessage("LAN205 request clearance to SCIE", Delivery, 0);
        var readback = s.Suggestions()[0].Text;
        Assert.StartsWith("Cleared to SCIE via GELU5B departure, runway 17R, climb flight level 100, squawk", readback);
        Assert.EndsWith("after departure Santiago Approach 119.7, LAN205", readback);
    }

    [Theory]
    [InlineData("squawk 4721", "squawk fower seven two one")]
    [InlineData("contact Santiago Approach 119.7", "contact Santiago Approach one one niner decimal seven")]
    [InlineData("runway 17R", "runway one seven right")]
    [InlineData("taxi to holding point runway 17R via G, Q, H", "taxi to holding point runway one seven right via Golf, Quebec, Hotel")]
    [InlineData("Cleared to SCIE via GELU5B departure", "Cleared to SCIE via GELU5B departure")]
    [InlineData("climb 5000 feet", "climb fife thousand feet")]
    [InlineData("climb flight level 240", "climb flight level two fower zero")]
    [InlineData("LAN205", "LAN two zero fife")]
    public void Spoken_form(string written, string spoken) => Assert.Equal(spoken, SpokenForm.Of(written));
}
