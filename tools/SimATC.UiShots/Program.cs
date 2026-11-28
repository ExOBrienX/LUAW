using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SimATC.App;
using SimATC.App.ViewModels;
using SimATC.Core.Airports;
using SimATC.Core.Simulation;

// Dibuja la interfaz de LUAW fuera de pantalla, sin simulador y con datos de demostración, y guarda un PNG por pantalla.
// Uso: dotnet run --project tools/SimATC.UiShots -- [carpeta de salida] [ancho] [alto] [pasos] [idioma es|en] [origen] [destino]
// No toca la configuración ni los vuelos de verdad: usa una carpeta de datos temporal (SIMATC_HOME).

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-shots");
var width = args.Length > 1 ? int.Parse(args[1]) : 1500;
var height = args.Length > 2 ? int.Parse(args[2]) : 900;
var steps = args.Length > 3 ? int.Parse(args[3]) : 4;
Directory.CreateDirectory(outDir);
var home = Path.Combine(Path.GetTempPath(), "simatc-uishots-home");
Directory.CreateDirectory(home);
Environment.SetEnvironmentVariable("SIMATC_HOME", home);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();
// Idioma de la interfaz para las capturas (por defecto, español).
SimATC.Core.Text.Tr.Load(Path.Combine(AppContext.BaseDirectory, "data"), args.Length > 4 ? args[4] : "es");

// TEST DATA: SCEL→SCIE de los datos de prueba (u otro par guardado), el piloto en un puesto con motores apagados.
var originIcao = args.Length > 5 ? args[5].ToUpperInvariant() : "SCEL";
var destIcao = args.Length > 6 ? args[6].ToUpperInvariant() : "SCIE";
var repo = FindRepo();
// Son datos guardados desde el simulador: solo están en el repositorio de desarrollo (ADR-153).
if (!File.Exists(Path.Combine(repo, "tests", "SimATC.Core.Tests", "Data", originIcao + ".json")))
{
    Console.Error.WriteLine("UiShots needs tests/SimATC.Core.Tests/Data/SCEL.json and SCIE.json (airports saved from the simulator; not published).");
    Environment.Exit(1);
}
var scel = AirportCache.Deserialize(File.ReadAllText(Path.Combine(repo, "tests", "SimATC.Core.Tests", "Data", originIcao + ".json")))!.Airport;
var scie = AirportCache.Deserialize(File.ReadAllText(Path.Combine(repo, "tests", "SimATC.Core.Tests", "Data", destIcao + ".json")))!.Airport;
var stand = scel.Parkings.First(p => p.Type is ParkingType.GateMedium or ParkingType.GateHeavy);
var state = new UserAircraftState(stand.Position, scel.ElevationFt, 0, stand.HeadingTrueDeg, 0, 0, 0, true, 118_100, 2000);
var bridge = new FakeSimBridge(state, TimeSpan.FromMilliseconds(250), new Dictionary<string, AirportData> { [originIcao] = scel, [destIcao] = scie });
bridge.Nearby.Add(new NearbyAirport(originIcao, scel.Reference, scel.ElevationFt * 0.3048));
// TEST DATA: vuelos de ejemplo en el historial ("Mis vuelos"), en la carpeta temporal.
var logbook = SimATC.Core.Comms.FlightLogbook.PathFor(SimATC.Core.AppPaths.Root);
File.Delete(logbook);
var day = DateTime.UtcNow.Date;
foreach (var (cs, type, from, to, depRwy, arrRwy, standName, daysAgo, minutes, nm, ft) in new[]
         {
             ("BAW388", "A320", "EGLL", "EBBR", "27R", "25L", "A12", 9, 55, 199.0, 33000.0),
             ("UAL1543", "B38M", "KBWI", "KEWR", "33R", "04R", "C71", 6, 48, 175.0, 24000.0),
             ("IBE3217", "A321", "LEBL", "LEMD", "24L", "18R", "536", 2, 62, 286.0, 35000.0),
         })
{
    var takeoff = day.AddDays(-daysAgo).AddHours(14);
    SimATC.Core.Comms.FlightLogbook.Add(logbook, new SimATC.Core.Comms.FlightRecord(cs, type, from, to, depRwy, arrRwy, standName,
        takeoff.AddMinutes(-14), takeoff, takeoff.AddMinutes(minutes), takeoff.AddMinutes(minutes + 9), nm, ft));
}
var airports = new AirportProvider(bridge, new AirportCache(Path.Combine(home, "cache", "airports")));
var vm = new MainWindowViewModel(bridge, airports, Path.Combine(AppContext.BaseDirectory, "data"));
var window = new MainWindow { DataContext = vm, Width = width, Height = height };
window.Show();
bridge.Start();
Radios(118_100);
// TEST DATA: viento del sur de 8 kt, para que haya pista en uso y ATIS.
for (var i = 0; i < 6; i++)
{
    bridge.EmitWeather(new SimATC.Core.Atc.AmbientWeather(stand.Position, true, 0, 180, 8, 9999, 15, 1016, SimATC.Core.Atc.Precipitation.None, 2));
    Pump(1000);
}

Shot("01-radar-inicio");
// Un plan manual y los primeros llamados, con las sugerencias (como haría el copiloto).
// TEST DATA: indicativos de ejemplo.
vm.ManualCallsign = originIcao == "SCEL" ? "LAN205" : "IBE3216";
vm.ManualType = originIcao == "SCEL" ? "A20N" : "A321";
vm.ManualOrigin = originIcao;
vm.ManualDestination = destIcao;
vm.ManualCruise = "240";
vm.CreateManualPlanCommand.Execute(null);
Pump(2500);
// Las pestañas Salida y Llegada con el selector de pista, antes de la autorización (después queda bloqueado).
vm.Panel = "plan";
if (originIcao == "SCEL")
    vm.SelectedDepartureRunway = vm.DepartureRunwayOptions.FirstOrDefault(o => o.Ident == "17L"); // la de llegadas de los IA
Pump(1500);
var planTabs = window.FindControl<Avalonia.Controls.TabControl>("PlanTabs")!;
foreach (var (tab, name) in new[] { (1, "04b-plan-salida"), (2, "04c-plan-llegada") })
{
    planTabs.SelectedIndex = tab;
    Pump(900);
    Shot(name);
}
planTabs.SelectedIndex = 0;
vm.Panel = "radar";
Pump(500);
for (var i = 0; i < steps; i++)
{
    if (vm.Comms.StepTune is { } tune)
    {
        vm.Comms.TuneCommand.Execute(tune);
        // El avión de mentira obedece: la frecuencia pedida queda en el COM1.
        if (bridge.LastTune is { } asked)
        {
            bridge.State = bridge.State with { Com1ActiveKhz = asked.Khz };
            Radios(asked.Khz);
        }
        Pump(1500);
    }
    if (vm.Comms.Suggestions.Count == 0)
    {
        Pump(2000);
        continue;
    }
    vm.Comms.InputText = vm.Comms.Suggestions[0].Text;
    vm.Comms.SendCommand.Execute(null);
    Pump(5000);
}
Shot("02-radar-con-comunicaciones");
foreach (var (panel, name) in new[]
         {
             ("comms", "03-comunicaciones"), ("plan", "04-plan"), ("briefing", "05-briefing"), ("flights", "06-mis-vuelos"),
             ("traffic", "07-trafico"), ("settings", "08-configuracion"), ("diag", "09-diagnostico"), ("manual", "14-manual"),
         })
{
    vm.Panel = panel;
    Pump(700);
    Shot(name);
}
// El aviso modal de confirmación.
vm.Panel = "plan";
vm.AskClearPlanCommand.Execute(null);
Pump(700);
Shot("10-aviso-modal");
vm.NoticeSecondaryCommand.Execute(null);
// El aviso de versión nueva (ADR-161), con una versión de ejemplo.
vm.OfferUpdate("0.9.3-beta.1");
Pump(700);
Shot("19-version-nueva");
vm.NoticeSecondaryCommand.Execute(null);
// Configuración entera (ventana alta) y el micrófono abierto en el panel de comunicaciones.
window.Height = 2300;
vm.Panel = "settings";
Pump(900);
Shot("11-configuracion-completa");
window.Height = height;
vm.Panel = "radar";
vm.Comms.MicListening = true;
vm.Comms.MicState = "Micrófono abierto: colaciona ahora (se cierra solo al terminar)";
Pump(700);
Shot("12-microfono-abierto");
vm.Comms.MicListening = false;
vm.Comms.MicState = null;
vm.Panel = "comms";
vm.Comms.RequestMenuOpen = true;
Pump(900);
Shot("13-que-puedo-pedir");

// TEST DATA: ficha de un IA en vuelo (sin simulador no hay tráfico; los valores son de ejemplo, como los del radar real).
vm.Panel = "radar";
vm.SelectedId = SimATC.App.Radar.RadarView.UserId;
Pump(300);
vm.InspectorMore = true;
vm.InspectorOpen = true;
// Recién actualizada la ficha de tu avión: las filas de ejemplo alcanzan a dibujarse antes de la próxima (5 Hz).
Pump(300);
vm.InspectorTitle = "EZS1516 · A319";
vm.InspectorSubtitle = "EHAM → LSGG";
vm.InspectorPhase = SimATC.Core.Text.Tr.T("crucero");
vm.InspectorAltitude = "FL350";
vm.InspectorSpeed = "450 kt";
string L(string s) => SimATC.Core.Text.Tr.T(s);
var fl350 = args.Length > 4 && args[4] == "en" ? "FL350 · 35,000 ft" : "FL350 · 35.000 ft";
vm.InspectorRows =
[
    new(L("Altitud"), fl350, L("Ahora")),
    new(L("Perfil"), L("nivelado") + SimATC.Core.Text.Tr.F(" · empieza a bajar en {0:0} NM", 216)),
    new(L("Velocidad"), "IAS ≈265 kt · GS 450 kt"),
    new(L("Autorizado"), "—"),
    new(L("Simulador"), L("virtual (lejos de ti, no está en el simulador)")),
    new(L("Falta"), "304 NM · 02:40 UTC (41 min)", SimATC.Core.Text.Tr.F("Llegada a {0}", "LSGG")),
    new(L("Pista"), "22 · 3,0° · 3752 m"),
    new(L("Procedimientos"), "STAR AKIT3R · ILS 22", L("Ruta")),
    new(L("Ruta"), "LOPIK N852 LNO DCT SUTAL DCT GTQ DCT POGOL DCT LASAT DCT MOROK Z24 AKITO AKIT3R"),
    new(L("Crucero"), fl350),
    new(L("Fuente"), L("VATSIM: la ruta que vuelan pilotos reales en ese par (verificada: empalma con la SID y la STAR, sin rodeos)")),
    new(L("Modelo"), "FSLTL_FAIB_A319_EZS-EasyJet Switzerland", L("Avión")),
];
Shot("18-ficha-ia");
vm.SelectedId = null;
vm.InspectorOpen = false;

// ADR-173: con zoom cercano, cada avión a su tamaño real con su silueta (el tuyo como A330; IA bimotor, ancho y cuatrimotor).
{
    var radar = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<SimATC.App.Radar.RadarView>().First(r => r.IsEffectivelyVisible);
    radar.UserWingspanM = 60.3; // TEST DATA: A330 (sin catálogo, la app usaría los 36 m supuestos)
    radar.UserShape = SimATC.Core.Fleet.AircraftShape.Wide;
    var behind = SimATC.Core.Geo.GeoMath.Destination(stand.Position, stand.HeadingTrueDeg + 180, 75);
    var beside = SimATC.Core.Geo.GeoMath.Destination(stand.Position, stand.HeadingTrueDeg + 90, 55);
    vm.SelectedId = SimATC.App.Radar.RadarView.UserId;
    radar.FollowSelected = true;
    typeof(SimATC.App.Radar.RadarView).GetField("_scale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .SetValue(radar, 3.2);
    Pump(600);
    vm.InspectorOpen = false;
    // Sin simulador el tráfico se refresca vacío: los IA de ejemplo van directo al radar, justo antes de la captura.
    var far = SimATC.Core.Geo.GeoMath.Destination(stand.Position, stand.HeadingTrueDeg + 140, 120);
    SimATC.Core.Geo.GeoPoint At(double bearing, double m) => SimATC.Core.Geo.GeoMath.Destination(stand.Position, stand.HeadingTrueDeg + bearing, m);
    var wide = SimATC.Core.Fleet.AircraftShape.Wide;
    radar.Traffic =
    [
        new SimATC.App.Radar.TrafficMarker(behind, stand.HeadingTrueDeg, "AAL79 B77W", 1, WingspanM: 64.8, Shape: wide), // TEST DATA
        new SimATC.App.Radar.TrafficMarker(beside, stand.HeadingTrueDeg, "IBE3217 A320", 2, WingspanM: 35.8), // TEST DATA
        new SimATC.App.Radar.TrafficMarker(far, stand.HeadingTrueDeg + 90, "BAW15 A388", 3, WingspanM: 79.8, Shape: SimATC.Core.Fleet.AircraftShape.Quad), // TEST DATA
        new SimATC.App.Radar.TrafficMarker(At(-90, 55), stand.HeadingTrueDeg, "AFR12 AT76", 4, WingspanM: 27.1, Shape: SimATC.Core.Fleet.AircraftShape.Turboprop), // TEST DATA
        new SimATC.App.Radar.TrafficMarker(At(-60, 95), stand.HeadingTrueDeg, "N123 LJ45", 5, true, WingspanM: 14.6, Shape: SimATC.Core.Fleet.AircraftShape.Bizjet), // TEST DATA
        new SimATC.App.Radar.TrafficMarker(At(-35, 120), stand.HeadingTrueDeg, "N456 C172", 6, WingspanM: 11.0, Shape: SimATC.Core.Fleet.AircraftShape.Light), // TEST DATA
    ];
    Shot("20-radar-a-escala");
    radar.FollowSelected = false;
    vm.SelectedId = null;
}

// Ventana de pushback (planificador) y panel compacto, cada una en su propia ventana.
var graph = SimATC.Core.Taxi.TaxiGraph.Build(scel);
var pvm = PushbackViewModel.ForStand(graph, scel, stand.Position, stand.HeadingTrueDeg);
var push = new SimATC.App.Views.PushbackWindow { DataContext = pvm, Width = 1000, Height = 640 };
push.Show();
Pump(900);
WindowShot(push, "15-pushback");
pvm.IsPushing = true;
pvm.PushProgress = 55;
pvm.CrewStatus = SimATC.Core.Text.Tr.F("Empujando… faltan {0:0} m.", 34);
Pump(600);
WindowShot(push, "16-pushback-empujando");
push.Close();
var compact = new SimATC.App.Views.CompactWindow { DataContext = vm.Comms, Width = 420, Height = 600 };
compact.Show();
Pump(900);
WindowShot(compact, "17-panel-compacto");
compact.Close();
Console.WriteLine($"Listo: {outDir}");
Environment.Exit(0);

// COM1 con energía, escuchando y transmitiendo en esa frecuencia.
void Radios(int khz) => bridge.EmitRadios(new UserRadios([new ComRadio(1, true, khz, true, true), new ComRadio(2, true, 121_500, false, false)]));

void Pump(int ms)
{
    var until = Environment.TickCount64 + ms;
    while (Environment.TickCount64 < until)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(20);
    }
}

void Shot(string name)
{
    // Sin simulador no hay base de rutas: la barra de estado no muestra ese aviso en las capturas.
    vm.TrafficStatus = "";
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    using var frame = window.CaptureRenderedFrame();
    if (frame is null)
    {
        Console.WriteLine($"{name}: sin imagen");
        return;
    }
    var path = Path.Combine(outDir, name + ".png");
    frame.Save(path);
    Console.WriteLine(path);
}

void WindowShot(Window w, string name)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    using var frame = w.CaptureRenderedFrame();
    if (frame is null)
        return;
    var path = Path.Combine(outDir, name + ".png");
    frame.Save(path);
    Console.WriteLine(path);
}

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimATC.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repo (SimATC.slnx).");
}
