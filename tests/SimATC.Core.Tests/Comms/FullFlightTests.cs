using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

namespace SimATC.Core.Tests.Comms;

/// <summary>
/// El ATC sigue todo el vuelo del piloto (bloque C, ADR-069): un piloto simulado vuela SCEL→SCIE contestando con la primera
/// sugerencia (como el copiloto) y se comprueba cada etapa, del puesto de salida al de llegada. Datos reales de SCEL y SCIE.
/// </summary>
public class FullFlightTests
{
    private const double Nm = 1852;
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private sealed class Live
    {
        public GeoPoint Position;
        public bool OnGround = true;
        public double AltitudeFt = 1600;
        public double GroundSpeedKt;
        public double HeadingDeg;
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)] // pista ocupada hasta 3 NM: "continue approach" y después autorizado (ADR-076)
    [InlineData(true, true, 2)]  // pista ocupada hasta el motor y al aire: vuelve a aproximar y aterriza
    [InlineData(true, false, 3)] // ADR-100: la pista en uso de SCIE cambia en vuelo a la 20 y vuelve a la 02: el ATC avisa las dos
    [InlineData(true, false, 4)] // ADR-101: con la STAR del plan, el descenso es "cleared (STAR) arrival, descend via STAR to…"
    [InlineData(true, false, 5)] // FAA: "descend via (STAR)" sin nivel; vale hasta el final de la STAR (vuelo a KEWR, 2026-11-15)
    public void A_flight_from_scel_to_scie_is_controlled_from_gate_to_gate(bool withCenter, bool byCopilot, int runwayMode)
    {
        var scel = Load("SCEL");
        var scie = Load("SCIE");
        var scelGraph = TaxiGraph.Build(scel);
        var scieGraph = TaxiGraph.Build(scie);
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(DataDir, "SCEL"));
        var phrases = runwayMode == 5
            ? Phraseology.Load(Phraseology.PathFor(DataDir, AtcLanguage.English), Phraseology.PathFor(DataDir, "faa-en"), new Random(3))
            : Phraseology.Load(Phraseology.PathFor(DataDir, AtcLanguage.English), new Random(3));
        // TEST DATA: plan con la forma del de SimBrief (SCEL 17R → SCIE 02, FL240).
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", Position: scel.Reference, PlannedRunway: "17R"),
            new PlanAirport("SCIE", Position: scie.Reference, PlannedRunway: "02"), 24000, "GELU5B GELUS UQ809 ARUNI",
            [
                new PlanFix("GELUS", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.15), 24000),
                new PlanFix("ARUNI", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.55), 24000),
                new PlanFix("BBBBB", "wpt", "UQ809", false, GeoMath.Intermediate(scel.Reference, scie.Reference, 0.75), 24000),
            ]) { SidIdent = "GELU5B" };
        var arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), scieGraph, null, "02", null);
        var starName = scie.Arrivals.Count > 0 ? scie.Arrivals[0].Name : "TEST1A"; // TEST DATA: una STAR de SCIE
        if (runwayMode is 4 or 5)
            arrival = arrival with { StarName = starName };
        // TEST DATA: como en EE. UU., altitud de transición 18000 ft y la STAR termina en 7000 ft (PHLBO4 en KEWR).
        if (runwayMode == 5)
            arrival = arrival with
            {
                TransitionAltitudeFt = 18_000,
                StarRestrictions = [("BBBBB", GeoMath.Intermediate(scel.Reference, scie.Reference, 0.75), 7000)],
            };
        string? viaOrder = null;
        var gate = scel.Parkings.First(p => p.Name.ToString() == "Gate" && p.Number == 14);
        var wentAround = false;
        var live = new Live { Position = gate.Position, HeadingDeg = gate.HeadingTrueDeg };
        var r17R = PilotTaxiRoute.RunwayIndex(scel, "17R")!.Value.Index;
        var askedRunway = false;
        var runwaySwaps = 0;
        var askedEarlyDescent = false;
        string? direct = null;
        var pilotStand = Enumerable.Range(0, scie.Parkings.Count).Last(i => scie.Parkings[i].Type.ToString().StartsWith("Gate"));
        var ctx = new DepartureContext(plan, scel, atc, new AtcStations(scel, atc), scelGraph, phrases, () => ["17R"], () => 'A',
            () => live.Position, () => ReadbackStrictness.Realistic,
            RunwayStatus: () => new PilotRunwayStatus(r17R, null, 0, null, true),
            OnGround: () => live.OnGround, HeadingDeg: () => live.HeadingDeg, AltitudeFt: () => live.AltitudeFt,
            Wind: () => "020 degrees 8 knots", GroundSpeedKt: () => live.GroundSpeedKt,
            Arrival: () => arrival, QnhHpa: () => 1015, DirectTo: fix => direct = fix, CenterAt: withCenter ? CenterAt : null,
            // Un A320 IA delante en la secuencia (TEST DATA, ADR-085).
            // Puesto reservado del piloto (ADR-085/087): la última puerta de SCIE.
            PreferredStand: () => pilotStand,
            // ADR-097: en SCIE se aterriza por la 02; la 20 es en sentido contrario.
            RequestRunway: r => new RunwayRequestAnswer(r == "02", "02"),
            ArrivalRunwayBusy: () => runwayMode switch
            {
                1 => GeoMath.DistanceM(live.Position, scie.Reference) / Nm > 3,
                2 => !wentAround,
                _ => false,
            });
        var s = new PilotSession(ctx, new Random(5));

        var heard = new List<string>();
        var said = new List<string>();
        var runway02 = FlightPlannerLanding(scie);
        // La ruta del piloto (TEST DATA geométrico): de SCEL a 12 NM en la prolongación del eje de la 02 y la final (ADR-105:
        // el ATC vigila la final; antes el piloto iba en línea recta al aeropuerto, llegando por el lado de la 20).
        var finalStart = GeoMath.Destination(runway02.Threshold, GeoMath.NormalizeDeg(runway02.HeadingDeg + 180), 12 * Nm);
        var leg1 = GeoMath.DistanceM(scel.Reference, finalStart);
        var leg2 = GeoMath.DistanceM(finalStart, runway02.Threshold);
        var total = leg1 + leg2;
        (GeoPoint Position, double Heading) At(double d) => d <= leg1
            ? (GeoMath.Intermediate(scel.Reference, finalStart, d / leg1), GeoMath.InitialBearingDeg(GeoMath.Intermediate(scel.Reference, finalStart, d / leg1), finalStart))
            : (GeoMath.Intermediate(finalStart, runway02.Threshold, Math.Min(1, (d - leg1) / leg2)), runway02.HeadingDeg);
        var flown = 0.0;
        var landedAt = -1.0;
        var askedDirect = false;
        var trafficDone = false;
        var levelAsked = false;
        var copilot = new Copilot(new Random(2));
        var copilotSettings = new CopilotSettings(CopilotReadbacks.Always, RoutineCalls: true, Rescue: true, Tune: false);
        for (var t = 0.0; t < 3 * 3600 && s.Phase != PilotPhase.Arrived; t += 2)
        {
            if (viaOrder is null && heard.Exists(h => h.Contains("descend via")))
                viaOrder = s.Orders().FirstOrDefault(o => o.Label == "Altitud")?.Value;
            // Dónde está el piloto según la etapa.
            switch (s.Phase)
            {
                case PilotPhase.Taxiing or PilotPhase.TowerHandoffGiven or PilotPhase.WithTower or PilotPhase.HoldShortGiven when s.TaxiRoute is { } tr:
                    live.Position = tr.HoldShort;
                    break;
                case PilotPhase.TakeoffCleared:
                    live.OnGround = false;
                    live.AltitudeFt = 3500;
                    live.GroundSpeedKt = 180;
                    break;
                case >= PilotPhase.Airborne and < PilotPhase.Landed when !live.OnGround:
                {
                    flown += live.GroundSpeedKt * 0.514444 * 2;
                    var remainingNm = (total - flown) / Nm;
                    (live.Position, live.HeadingDeg) = At(Math.Min(flown, total));
                    live.GroundSpeedKt = remainingNm < 20 ? 160 : 420;
                    // Sube a 2500 ft/min hasta crucero; baja por una senda de 3° hacia el umbral.
                    var descent = scie.ElevationFt + 50 + remainingNm * 318;
                    live.AltitudeFt = Math.Min(Math.Min(live.AltitudeFt + 2500.0 / 30, 24000), descent);
                    if (remainingNm < 0.3 && s.LandingCleared)
                    {
                        live.OnGround = true;
                        live.GroundSpeedKt = 40;
                        live.Position = runway02.Threshold;
                        live.HeadingDeg = runway02.HeadingDeg;
                        landedAt = t;
                    }
                    break;
                }
                case PilotPhase.Landed or PilotPhase.TaxiIn when landedAt >= 0:
                    // Frena por la pista y la libra; con la ruta de Ground, rueda y se detiene en el puesto.
                    live.GroundSpeedKt = 10;
                    live.Position = GeoMath.Destination(runway02.Threshold, runway02.HeadingDeg, 1500);
                    if (s.TaxiInRoute is { Count: > 0 } tin && !s.AwaitingReadback)
                    {
                        live.Position = tin[^1];
                        live.GroundSpeedKt = 0;
                    }
                    break;
            }

            // Modo 3 (ADR-100): después del primer "expect … runway 02", la pista en uso pasa a la 20 (METAR) y después
            // vuelve a la 02; antes de la autorización de aproximación el ATC avisa cada cambio.
            if (runwayMode == 3 && runwaySwaps == 0 && heard.Any(h => h.Contains("approach runway 02")))
            {
                arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), scieGraph, null, "20", null);
                runwaySwaps = 1;
            }
            else if (runwayMode == 3 && runwaySwaps == 1 && heard.Any(h => h.Contains("runway 20") && h.Contains("expect")))
            {
                arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), scieGraph, null, "02", null);
                runwaySwaps = 2;
            }
            foreach (var x in s.Tick(t))
            {
                heard.Add($"[{x.Station.Callsign}] {x.Text}");
                if (x.Text.Contains("go around") && !wentAround)
                {
                    // Motor y al aire: vuelve a la final a 12 NM, en la senda, para otra aproximación.
                    wentAround = true;
                    flown = total - 12 * Nm;
                    live.AltitudeFt = scie.ElevationFt + 50 + 12 * 318;
                }
            }

            // Un avión de frente en crucero, al mismo nivel (TEST DATA): información de tráfico y acuse del piloto (ADR-075).
            if (!trafficDone && askedDirect && s.Phase == PilotPhase.EnRoute && !s.AwaitingReadback && !s.AwaitingCheckIn)
            {
                var ahead = GeoMath.Destination(live.Position, live.HeadingDeg, 6 * Nm);
                var info = s.AirTrafficInfo([new AirTraffic(99, ahead, GeoMath.NormalizeDeg(live.HeadingDeg + 180), live.AltitudeFt, 420)], t);
                if (info.Count > 0)
                {
                    trafficDone = true;
                    foreach (var x in info)
                        heard.Add($"[{x.Station.Callsign}] {x.Text}");
                    var ack = s.Suggestions()[0].Text;
                    said.Add(ack);
                    foreach (var x in s.OnPilotMessage(ack, s.ExpectedStation!.Khz, t))
                        heard.Add($"[{x.Station.Callsign}] {x.Text}");
                    // No se repite el mismo aviso.
                    Assert.Empty(s.AirTrafficInfo([new AirTraffic(99, ahead, GeoMath.NormalizeDeg(live.HeadingDeg + 180), live.AltitudeFt, 420)], t + 5));
                }
            }
            // Otro nivel de crucero más bajo (ADR-089): el ATC lo autoriza con "descend", no "climb".
            if (!levelAsked && trafficDone && s.Phase == PilotPhase.EnRoute && !s.AwaitingReadback && !s.AwaitingCheckIn && live.AltitudeFt >= 23_000)
            {
                levelAsked = true;
                var st = s.ExpectedStation!;
                var ask = $"{st.Callsign}, {s.Callsign}, request flight level 200";
                said.Add(ask);
                var reply = s.OnPilotMessage(ask, st.Khz, t);
                Assert.Contains(reply, x => x.Text.Contains("descend") && x.Text.Contains("200"));
                Assert.DoesNotContain(reply, x => x.Text.Contains("climb"));
                foreach (var x in reply)
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
                var rb = s.Suggestions()[0].Text;
                said.Add(rb);
                foreach (var x in s.OnPilotMessage(rb, st.Khz, t + 1))
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
                Assert.False(s.AwaitingReadback);
            }
            if (byCopilot && s.Phase != PilotPhase.EnRoute | askedDirect)
            {
                // Solo el copiloto (prueba de Marco, 2026-11-05): colaciona y hace los primeros contactos; lo que pregunta
                // (decisiones del capitán) se confirma enviando su propuesta. Con su pausa humana de 1,5 a 4 s.
                if (copilot.Tick(s, copilotSettings, t, false) is { } act)
                {
                    said.Add(act.Text);
                    foreach (var x in s.OnPilotMessage(act.Text, act.Station.Khz, t))
                        heard.Add($"[{x.Station.Callsign}] {x.Text}");
                }
                if (s.Phase == PilotPhase.PushbackApproved && !s.PushedBack)
                    s.MarkPushedBack();
                continue;
            }
            // El piloto (como el copiloto): colaciona, hace el primer contacto o el paso que toca.
            var mustSpeak = s.AwaitingReadback || s.AwaitingCheckIn
                            || s.Phase is PilotPhase.Parked or PilotPhase.Cleared or PilotPhase.PushbackApproved or PilotPhase.WithTower
                                or PilotPhase.Airborne
                            || s.Phase == PilotPhase.ArrivalTower && !s.LandingCleared;
            // Una vez en crucero, pide un directo (RF-29b).
            if (!mustSpeak && s.Phase == PilotPhase.EnRoute && !askedDirect && live.AltitudeFt >= 23000)
            {
                askedDirect = true;
                var station = s.ExpectedStation!;
                said.Add("LAN205 request direct BBBBB");
                foreach (var x in s.OnPilotMessage($"{station.Callsign}, LAN205, request direct BBBBB", station.Khz, t))
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
                continue;
            }
            // Y después pide la pista contraria (ADR-097): se la niegan con la que está en uso.
            if (!mustSpeak && s.Phase == PilotPhase.EnRoute && askedDirect && !askedRunway && !byCopilot)
            {
                askedRunway = true;
                var station = s.ExpectedStation!;
                said.Add("LAN205 request runway 20");
                foreach (var x in s.OnPilotMessage($"{station.Callsign}, LAN205, request runway 20", station.Khz, t))
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
                continue;
            }
            // Y pide descenso lejos del TOD (ADR-098): "maintain FL…, expect descent after (fijo)".
            if (!mustSpeak && s.Phase == PilotPhase.EnRoute && askedRunway && !askedEarlyDescent && !byCopilot && live.AltitudeFt >= 23000)
            {
                askedEarlyDescent = true;
                var station = s.ExpectedStation!;
                said.Add("LAN205 request descent");
                foreach (var x in s.OnPilotMessage($"{station.Callsign}, LAN205, request descent", station.Khz, t))
                    heard.Add($"[{x.Station.Callsign}] {x.Text}");
                continue;
            }
            if (!mustSpeak || s.Suggestions() is not { Count: > 0 } sug)
                continue;
            if (s.Phase == PilotPhase.Cleared && sug.Count > 1)
                sug = [sug[^1]]; // sin pushback: pide rodaje
            var khz = (s.ReadbackStation ?? s.ExpectedStation)?.Khz;
            said.Add(sug[0].Text);
            foreach (var x in s.OnPilotMessage(sug[0].Text, khz, t))
                heard.Add($"[{x.Station.Callsign}] {x.Text}");
        }

        var transcript = string.Join("\n", heard.Zip(said.Concat(Enumerable.Repeat("", heard.Count)), (a, b) => a));
        var all = string.Join(" | ", heard.TakeLast(14));
        Assert.True(s.Phase == PilotPhase.Arrived && s.TaxiInRoute is not null, $"terminó en {s.Phase}: {all} || piloto: {string.Join(" | ", said.TakeLast(8))}");
        // La frase OACI; la FAA (modo 5) dice "climb and maintain…" y el altímetro en pulgadas.
        var icao = runwayMode != 5;
        Assert.Contains(heard, h => h.Contains(icao ? "climb flight level 240" : "climb and maintain flight level 240"));
        Assert.Contains(heard, h => h.Contains(icao ? "route direct BBBBB" : "proceed direct BBBBB"));
        Assert.Contains(heard, h => h.Contains("traffic 12 o'clock, 6 miles, opposite direction"));
        Assert.Contains(said, h => h.StartsWith("Traffic in sight"));
        Assert.Equal("BBBBB", direct);
        Assert.True(levelAsked, "no se pidió otro nivel en crucero");
        Assert.Contains(heard, h => h.Contains("descend"));
        // SCIE no tiene Aproximación: la dependencia de ruta baja con QNH, anuncia y autoriza la aproximación (ADR-075).
        if (icao)
            Assert.Contains(heard, h => h.Contains("descend to altitude") && h.Contains("QNH 1015"));
        Assert.Contains(heard, h => h.Contains("expect ILS approach runway 02"));
        // Sin "number 2, follow the … ahead" en la llegada (ADR-088): solo en el rodaje.
        Assert.DoesNotContain(heard, h => h.Contains("number 2"));
        // La aproximación esperada llega con el primer descenso, no al final (prueba de Marco: "me la dice encima").
        if (icao)
            Assert.True(heard.FindIndex(h => h.Contains("expect ILS approach")) < heard.FindIndex(h => h.Contains("descend to altitude")));
        // Con la letra de la carta y, si llega por una transición, "via …" (ADR-121).
        Assert.Contains(heard, h => h.Contains("cleared ILS") && h.Contains(icao ? "approach runway 02" : "runway 02 approach"));
        Assert.Contains(heard, h => h.Contains("cleared to land"));
        if (runwayMode == 1)
            Assert.Contains(heard, h => h.Contains("continue approach"));
        if (runwayMode == 2)
            Assert.Contains(heard, h => h.Contains("go around, I say again, go around"));
        if (runwayMode == 5)
        {
            // Con "descend via" el piloto baja por la STAR: el ATC no lo hace subir a la altitud de transición.
            var via = heard.FindIndex(h => h.Contains($"descend via {starName}"));
            Assert.True(via >= 0, string.Join(" | ", heard));
            Assert.DoesNotContain(heard.Skip(via), h => h.Contains("climb and maintain"));
            // Autorizado hasta el final de la STAR, no hasta la altitud de transición.
            Assert.StartsWith("7000 ft", viaOrder);
        }
        if (runwayMode == 4)
        {
            Assert.Contains(heard, h => h.Contains($"cleared {starName} arrival, descend via STAR to"));
            Assert.Contains(said, h => h.StartsWith($"Cleared {starName} arrival, descend via STAR to", StringComparison.Ordinal));
        }
        if (runwayMode == 3)
        {
            Assert.Equal(2, runwaySwaps);
            var at20 = heard.FindIndex(h => h.Contains("expect") && h.Contains("runway 20"));
            Assert.True(at20 > 0, "no avisó la 20");
            Assert.Contains(heard.Skip(at20 + 1), h => h.Contains("expect ILS approach runway 02"));
        }
        Assert.Contains(heard, h => h.Contains("vacate") || h.Contains("when vacated"));
        Assert.Contains(heard, h => h.Contains("taxi to stand"));
        Assert.Equal(SimATC.Core.Engine.DepartureAgent.SpokenStand(scie.Parkings[pilotStand]), s.ArrivalStand);
        Assert.NotNull(s.TaxiInRoute);
        // Nada de "negative" ni "say again": cada llamado una sola vez y correcto.
        if (!byCopilot)
        {
            Assert.Contains(heard, h => h.Contains("negative, unable runway 20, runway 02 in use"));
            Assert.True(heard.Any(h => h.Contains(icao ? "expect descent after" : "expect lower at")) || !askedEarlyDescent,
                "sin 'expect descent after': " + string.Join(" | ", heard.SkipWhile(h => !h.Contains("unable runway")).Take(3))
                + " || piloto: " + string.Join(" | ", said.SkipWhile(h => !h.Contains("request runway")).Take(4)));
        }
        var bad = heard.FindIndex(h => h.Contains("negative") && !h.Contains("unable runway") || h.Replace("I say again", "").Contains("say again"));
        Assert.True(bad < 0, bad < 0 ? "" : $"{heard[bad]} || antes: {string.Join(" | ", heard.Skip(Math.Max(0, bad - 4)).Take(4))} || piloto: {string.Join(" | ", said)}");
        if (withCenter)
        {
            Assert.Contains(heard, h => h.Contains("contact Santiago Radar") && h.Contains("126.3")); // AIP: al sur de 33°23'S
            Assert.Contains(heard, h => h.StartsWith("[Santiago Radar]", StringComparison.Ordinal));
        }
    }

    // Centro (ADR-071/072): FIR real de VATSpy y el Centro oficial del AIP Chile (data/centers.json).
    private static readonly Lazy<(SimATC.Core.World.FirMap Map, SimATC.Core.World.CenterData Official)> Centers = new(() =>
        (SimATC.Core.World.FirMap.Load(SimATC.Core.World.FirMap.DatPathFor(DataDir), SimATC.Core.World.FirMap.BoundariesPathFor(DataDir)),
         SimATC.Core.World.CenterData.Load(SimATC.Core.World.CenterData.PathFor(DataDir))));

    private static AtcStation? CenterAt(GeoPoint p) =>
        Centers.Value.Map.At(p) is { } fir ? Centers.Value.Official.StationFor(fir.Icao, p) : null;

    /// <summary>ADR-098: las restricciones "a o por encima de" de una STAR real de SCEL, en orden y con su fijo.</summary>
    [Fact]
    public void Star_restrictions_come_from_the_simulator_procedures()
    {
        var scel = Load("SCEL");
        var star = scel.Arrivals.FirstOrDefault(p => SimATC.Core.Plan.ProcedureGeometry.StarLegs(p, null, null)
            .Any(l => l.AltitudeConstraint == AltitudeConstraint.AtOrAbove && l.FixIcao.Length > 0));
        Assert.NotNull(star);
        var list = new[] { "17L", "17R", "35L", "35R" }.Select(r => ArrivalInfo.RestrictionsFor(scel, star!.Name, null, r))
            .FirstOrDefault(l => l.Count > 0) ?? [];
        Assert.NotEmpty(list);
        Assert.All(list, r => Assert.True(r.MinFt > 0 && r.Fix.Length > 0));
    }

    [Fact]
    public void The_destination_uses_the_planned_runway_its_best_approach_and_the_chart_altitude()
    {
        var scie = Load("SCIE");
        var a = ArrivalInfo.Create(scie, new AtcStations(scie, null), null, null, "02", null);
        Assert.Equal("02", a.Runway);
        Assert.Equal("ILS", a.ApproachWord);
        Assert.NotNull(a.ApproachName);
        Assert.InRange(a.ApproachAltitudeFt, scie.ElevationFt + 1000, 12_000);
    }

    private static SimATC.Core.Flight.LandingRunway FlightPlannerLanding(AirportData scie) =>
        SimATC.Core.Flight.FlightPlanner.LandingFor(scie, null, "02", SimATC.Core.Engine.PerformanceCategory.MediumJet)!;

    [Theory]
    [InlineData(0, 12)]
    [InlineData(60, 2)]
    [InlineData(-90, 9)]
    [InlineData(170, 6)]
    [InlineData(-20, 11)]
    public void Relative_bearing_as_clock(double rel, int clock) => Assert.Equal(clock, PilotSession.Clock(rel));

    [Theory]
    [InlineData(0, 10, 0, "traffic.similar_direction")]
    [InlineData(0, 185, 5, "traffic.opposite_direction")]
    [InlineData(0, 90, -40, "traffic.crossing_left_right_ahead")]   // a la izquierda, va hacia la derecha
    [InlineData(0, 270, 40, "traffic.crossing_right_left_ahead")]
    [InlineData(0, 90, -140, "traffic.crossing_left_right_behind")]
    public void Relative_movement(double mine, double theirs, double relBearing, string id) =>
        Assert.Equal(id, PilotSession.Movement(mine, theirs, relBearing));

    /// <summary>"Sintonizar" como el COM del juego (ADR-078): solo lo que está al alcance desde donde está el piloto.</summary>
    [Fact]
    public void Tunable_stations_depend_on_where_the_pilot_is()
    {
        var scel = Load("SCEL");
        var scie = Load("SCIE");
        var atc = AirportAtcData.Load(AirportAtcData.PathFor(DataDir, "SCEL"));
        var phrases = Phraseology.Load(Phraseology.PathFor(DataDir, AtcLanguage.English), new Random(3));
        // TEST DATA: plan SCEL → SCIE.
        var plan = new FlightPlan(PlanSource.SimBrief, "LAN205", "A20N", new PlanAirport("SCEL", Position: scel.Reference, PlannedRunway: "17R"),
            new PlanAirport("SCIE", Position: scie.Reference, PlannedRunway: "02"), 27000, "", []);
        var arrival = ArrivalInfo.Create(scie, new AtcStations(scie, null), null, null, "02", null);
        var pos = scel.Reference;
        var onGround = true;
        var ctx = new DepartureContext(plan, scel, atc, new AtcStations(scel, atc), TaxiGraph.Build(scel), phrases, () => ["17R"], () => 'A',
            () => pos, () => ReadbackStrictness.Realistic, OnGround: () => onGround, Arrival: () => arrival, CenterAt: CenterAt);
        var s = new PilotSession(ctx, new Random(5));
        string Names() => string.Join(", ", s.TunableStations(pos, onGround ? scel.ElevationFt : 27000).Select(x => x.Callsign));

        // En tierra en SCEL: las de Santiago, nada de Concepción ni del Centro.
        var ground = Names();
        Assert.Contains("Santiago Ground", ground);
        Assert.Contains("Santiago Tower", ground);
        Assert.DoesNotContain("Concepcion", ground);
        Assert.DoesNotContain("Radar", ground);

        // En crucero a mitad de camino (FL270): el Centro; ni Ground de Santiago ni la Torre de Concepción.
        onGround = false;
        pos = GeoMath.Intermediate(scel.Reference, scie.Reference, 0.5);
        var cruise = Names();
        Assert.Contains("Santiago Radar", cruise);
        Assert.DoesNotContain("Santiago Ground", cruise);
        Assert.DoesNotContain("Concepcion Tower", cruise);

        // A 15 NM de Concepción: su Torre aparece; Ground de Concepción (10 NM) todavía no.
        pos = GeoMath.Destination(scie.Reference, 20, 15 * Nm);
        var near = Names();
        Assert.Contains("Concepcion Tower", near);
        Assert.DoesNotContain("Concepcion Ground", near);
    }

    [Theory]
    [InlineData(10, 16)]      // en tierra: ~16 NM
    [InlineData(27_000, 214)] // FL270: ~214 NM
    public void Vhf_radio_horizon(double aglFt, double expectedNm) => Assert.Equal(expectedNm, RadioRange.HorizonNm(aglFt), 0);
}
