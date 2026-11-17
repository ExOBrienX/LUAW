using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;
using SimATC.Core.Traffic;
using SimATC.Core.World;

namespace SimATC.Core.Tests.Traffic;

/// <summary>Tráfico automático completo (etapas 6, 6b y 7) en SCEL con la base mundial real, sin simulador.</summary>
public class TrafficDirectorTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static readonly Lazy<AirportData> Scel = new(() =>
        SimAirports.Load("SCEL.json"));

    private static readonly Lazy<WorldAirports> Airports = new(() => WorldAirports.Load(WorldAirports.PathFor(DataDir)));

    private static AircraftModel M(string title, string type, WakeCategory wake = WakeCategory.Medium, double span = 34) =>
        new(title, ModelSource.Fsltl, type, "", wake, 2, span, ["GATE"], []);

    // TEST DATA: modelos genéricos (sin liveries de aerolínea).
    private static ModelMatcher Matcher() => new(
        [M("X_A320", "A320"), M("X_B738", "B738"), M("X_A20N", "A20N"), M("X_B788", "B788", WakeCategory.Heavy, 60)], []);

    /// <summary>
    /// ADR-088 (pedido de Marco): un aeropuerto chico no se llena como uno grande. Con la base de rutas reales, en hora punta
    /// SCEL recibe ~16 llegadas por hora y SCIE ~2; de madrugada, mucho menos.
    /// </summary>
    [Fact]
    public void The_hourly_rate_follows_the_real_daily_flights()
    {
        var scie = SimAirports.Load("SCIE.json");
        var zone = Airports.Value.Within(Scel.Value.Reference, 400 * 1852).Select(a => a.Icao).ToHashSet();
        var routes = WorldRoutes.LoadZone(WorldRoutes.PathFor(DataDir), zone, Scel.Value.Reference, 400 * 1852);
        // 16:00 UTC ≈ 11:00 hora local aproximada en Chile (por la longitud).
        DirectorContext Ctx(AirportData a, int hourUtc) => new(a, null, null, a.Reference, new DateTime(2026, 11, 6, hourUtc, 0, 0, DateTimeKind.Utc),
            [], new HashSet<int>(), 100);
        var scel = TrafficDirector.HourlyRate(Scel.Value, Ctx(Scel.Value, 16), routes.ArrivalsTo("SCEL").Count);
        var small = TrafficDirector.HourlyRate(scie, Ctx(scie, 16), routes.ArrivalsTo("SCIE").Count);
        // Promedio (ADR-145): 0,8 de lo que dan los horarios, y nunca más de 20 por pista ni de 40 en total (dos pistas).
        Assert.InRange(scel, 9, 20);
        Assert.InRange(small, 0.5, 3);
        Assert.True(TrafficDirector.ArrivalsPerRunwayHour * 2 <= 40);
        // De madrugada (≈ 03:00 local), un cuarto.
        Assert.InRange(TrafficDirector.HourlyRate(Scel.Value, Ctx(Scel.Value, 8), routes.ArrivalsTo("SCEL").Count), scel * 0.2, scel * 0.3);
    }

    [Fact]
    public void World_data_has_real_routes_for_scel()
    {
        Assert.True(Airports.Value.Count > 3000);
        var scel = Airports.Value.Find("SCEL")!.Value;
        var zone = Airports.Value.Within(scel.Position, 300 * 1852).Select(a => a.Icao).ToHashSet();
        var routes = WorldRoutes.LoadZone(WorldRoutes.PathFor(DataDir), zone, scel.Position, 300 * 1852);
        Assert.True(routes.ArrivalsTo("SCEL").Count > 200, $"{routes.ArrivalsTo("SCEL").Count} llegadas");
        Assert.Contains(routes.ForCallsign("LAN10"), l => l.Origin == "SCIE" && l.Destination == "SCEL");
    }

    [Theory]
    [InlineData("LAN002  ", "LAN2")]
    [InlineData("SKU320", "SKU320")]
    [InlineData("CCDPF", null)]
    [InlineData("JAT0531A", "JAT531A")]
    public void Live_callsigns_are_normalized_like_the_route_base(string raw, string? expected) =>
        Assert.Equal(expected, Callsigns.Normalize(raw));

    [Fact]
    public async Task An_hour_of_automatic_traffic_lands_and_parks_arrivals_without_conflicts()
    {
        var airport = Scel.Value;
        var graph = TaxiGraph.Build(airport);
        // El piloto estacionado lejos de todo, en un puesto de SCEL.
        var pilot = airport.Parkings[0].Position;
        var bridge = new FakeSimBridge(new UserAircraftState(pilot, airport.ElevationFt, 0, 0, 0, 0, 0, true, 0, 2000));
        bridge.Start();
        var log = new List<string>();
        var engine = new TrafficEngine(bridge, m => { lock (log) log.Add(m); });
        // TEST DATA: frecuencias de Ground, Tower y Approach.
        engine.Radio.Frequencies = new RadioFrequencies(121_900, 118_100, 120_400);
        var lines = new List<RadioLine>();
        bridge.Emit();
        var matcher = Matcher();
        var director = new TrafficDirector(Airports.Value, WorldRoutes.PathFor(DataDir), matcher, engine, m => { lock (log) log.Add(m); }, seed: 7);
        var parked = new List<InjectedParked>();
        var landed = 0;
        engine.DepartureHandoff = d => director.OnDepartureAirborne(d, airport);
        engine.ArrivalHandoff = f => director.OnArrivalLanded(f, airport, graph);
        engine.ArrivalParked += g =>
        {
            if (director.OnArrivalParked(g, airport, DateTime.UtcNow) is { } p)
                parked.Add(p);
        };
        engine.FlightFinished += f =>
        {
            if (f.Phase == FlightPhase.Landed)
                landed++;
        };

        // TEST DATA: 14:00 UTC (11:00 en Chile), pista 17R.
        var now = new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc);
        var maxAir = 0;
        var goArounds = 0;
        engine.Sequencer.Event += (_, e) =>
        {
            if (e == ArrivalEvent.GoAround)
                goArounds++;
        };
        var checkedSpawn = false;
        for (var t = 0.0; t < 3600; t += 1.0 / 30)
        {
            // Recién creadas, las llegadas nacen sobre su ruta: ya apuntan a su próximo punto (sin giros al aparecer).
            if (!checkedSpawn && t > 1)
            {
                checkedSpawn = true;
                foreach (var snap in engine.Snapshot().Where(x => x.Airborne))
                {
                    if (engine.Find(snap.ObjectId) is not AirborneAgent fa)
                        continue;
                    var toTarget = GeoMath.InitialBearingDeg(fa.Position, fa.Path[fa.TargetIndex].Position);
                    Assert.True(Math.Abs(GeoMath.AngleDiffDeg(fa.HeadingDeg, toTarget)) < 25,
                        $"{fa.Callsign} nace con rumbo {fa.HeadingDeg:0}° y su próximo punto está a {toTarget:0}°");
                }
            }
            if ((int)(t * 30) % (30 * 20) == 0)
            {
                var busy = parked.Select(p => p.Aircraft.ParkingIndex).ToHashSet();
                await director.UpdateAsync(new DirectorContext(airport, graph, "17R", pilot, now.AddSeconds(t), parked, busy, 100,
                    Departures: false), CancellationToken.None);
            }
            bridge.RaiseFrame(new FrameInfo(1.0 / 30, 1, false));
            engine.OnFrame(new FrameInfo(1.0 / 30, 1, false));
            maxAir = Math.Max(maxAir, engine.AirborneCount);
            while (engine.Radio.Lines.TryDequeue(out var line))
                lines.Add(line);
        }

        var summary = $"{landed} aterrizajes, {parked.Count} estacionados, {goArounds} motor y al aire, máx. {maxAir} en el aire, " +
                      $"{lines.Count(l => l.Kind == RadioLineKind.Descend)} descensos, {lines.Count(l => l.Kind == RadioLineKind.ClearedApproach)} aproximaciones. " +
                      string.Join(" | ", log.Where(l => l.Contains("error") || l.Contains("no se pudo") || l.Contains("llegada desde")).Take(8));
        Assert.True(landed >= 4, summary);
        Assert.True(parked.Count >= 3, summary);
        Assert.True(maxAir >= 5, summary);
        Assert.Equal(0, engine.Control.Tower.AiIncursions);
        Assert.True(goArounds <= landed / 2, summary);
        // La Torre autoriza los aterrizajes por radio, y las transmisiones de una frecuencia nunca se pisan (RF-38).
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.ClearedToLand) >= landed - 1, summary);
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.ArrivalFinal) >= landed - 1, summary);
        // Approach autoriza los descensos por escalones y la aproximación (paso 7).
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.Descend) >= landed, summary);
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.ClearedApproach) >= landed - 1, summary);
        foreach (var group in lines.GroupBy(l => l.Khz))
        {
            var ordered = group.OrderBy(l => l.TimeS).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].TimeS >= ordered[i - 1].TimeS + AiRadio.DurationS(ordered[i - 1].Kind) - 0.01,
                    $"{ordered[i - 1].Callsign} {ordered[i - 1].Kind} y {ordered[i].Callsign} {ordered[i].Kind} se pisan en {group.Key}");
        }
        // Cada llegada que se estacionó libró la pista por radio y rodó con autorización de Ground (ADR-065).
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.VacateContactGround) >= parked.Count, summary);
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.RunwayVacated) >= parked.Count, summary);
        Assert.True(lines.Count(l => l.Kind == RadioLineKind.ReadbackTaxiToStand) >= parked.Count, summary);
        Assert.All(lines.Where(l => l.Kind == RadioLineKind.TaxiToStand), l => Assert.False(string.IsNullOrEmpty(l.Detail)));
        // Dos llegadas estacionadas nunca en el mismo puesto.
        Assert.Equal(parked.Count, parked.Select(p => p.Aircraft.ParkingIndex).Distinct().Count());
    }

    /// <summary>
    /// ADR-145: la demanda con las pistas que el aeropuerto puede abrir (dos), no con la que está en uso. Con una sola, el tope
    /// era de una pista, el reparto veía poca demanda y dejaba una sola pista mixta (KEWR: 12 llegadas por hora a las 18 Z).
    /// </summary>
    [Fact]
    public void With_two_landing_runways_the_hourly_cap_doubles()
    {
        DirectorContext Ctx(IReadOnlyList<string>? landing) => new(Scel.Value, null, null, Scel.Value.Reference,
            new DateTime(2026, 11, 6, 16, 0, 0, DateTimeKind.Utc), [], new HashSet<int>(), 100) { LandingRunways = landing };
        var one = TrafficDirector.HourlyRate(Scel.Value, Ctx(null), 100_000);
        var two = TrafficDirector.HourlyRate(Scel.Value, Ctx(["17L", "17R"]), 100_000);
        Assert.Equal(TrafficDirector.ArrivalsPerRunwayHour, one, 3);
        // Con dos pistas sube, hasta el tope de puestos (60 % de las puertas de SCEL).
        Assert.True(two > one * 1.15, $"una pista {one:0.0}, dos {two:0.0}");
        Assert.True(two <= TrafficDirector.ArrivalsPerRunwayHour * 2);
    }
}
