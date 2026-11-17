using System.Diagnostics;
using System.Globalization;
using SimATC.Core.Simulation;
using SimATC.Sim;

// Probe: conecta con MSFS y muestra el avión del piloto a ~5 Hz. Ctrl+C para salir.
// Con --airport ICAO [--refresh] carga un aeropuerto, imprime un resumen y lo deja en caché.
// Con --weather ICAO… prueba cómo leer el clima del simulador (V-18). Con --nearby, los aeropuertos cercanos (ADR-030).

Console.OutputEncoding = System.Text.Encoding.UTF8; // "°" y acentos en la consola de Windows

// Con --efb-test abre el servidor local de prueba para la app del EFB (V-40, ADR-073).
if (args.Contains("--efb-test"))
    return await EfbTestCommand.RunAsync();

// Con --pushback prueba el remolque del simulador con el avión del piloto (V-27, RF-21c).
if (args.Contains("--pushback"))
    return SimATC.Sim.Diagnostics.PushbackSpike.Run(Console.WriteLine);

// Con --voice [es] prueba la voz (etapa 5b): cada voz instalada dice una frase con el efecto de radio.
if (args.Contains("--voice"))
    return await VoiceCommand.RunAsync(args.Contains("es"));

// Con --route ORIGEN DESTINO [pista] arma la ruta por aerovías del simulador, como el director (ADR-058).
var routeArg = Array.IndexOf(args, "--route");
if (routeArg >= 0 && routeArg + 2 < args.Length)
    return await RouteCommand.RunAsync(args[routeArg + 1].ToUpperInvariant(), args[routeArg + 2].ToUpperInvariant(),
        routeArg + 3 < args.Length ? args[routeArg + 3].ToUpperInvariant() : null);

// Con --airways [FIX:REGION …] prueba las aerovías del simulador (V-21 a V-24, RF-20b).
if (args.Contains("--airways"))
    return SimATC.Sim.Diagnostics.AirwaySpike.Run(args, Console.WriteLine);

// Con --kokoro [en] [--threads N] [--fp16] prueba Kokoro-82M para compararlo con Piper (spike de la 5b).
if (args.Contains("--kokoro"))
{
    var threadsArg = Array.IndexOf(args, "--threads");
    var threads = threadsArg >= 0 && threadsArg + 1 < args.Length ? int.Parse(args[threadsArg + 1], CultureInfo.InvariantCulture) : 2;
    var saveArg = Array.IndexOf(args, "--save");
    if (saveArg >= 0 && saveArg + 1 < args.Length)
        KokoroCommand.SaveDir = args[saveArg + 1];
    return KokoroCommand.Run(args.Contains("en"), threads, args.Contains("--fp16"));
}

var titlesArg = Array.IndexOf(args, "--titles");
if (titlesArg >= 0)
    return await TitlesCommand.RunAsync(titlesArg + 1 < args.Length ? args[titlesArg + 1] : null);

if (args.Contains("--nearby"))
    return await NearbyCommand.RunAsync();

// Con --atc-runway: si el ATC del juego tiene una pista seleccionada para el piloto (V-49, ADR-137).
if (args.Contains("--atc-runway"))
    return SimATC.Sim.Diagnostics.AtcRunwaySpike.Run(Console.WriteLine);

var weatherArg = Array.IndexOf(args, "--weather");
if (weatherArg >= 0)
    return SimATC.Sim.Diagnostics.WeatherSpike.Run(args.Skip(weatherArg + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).DefaultIfEmpty("SCEL").ToArray(), Console.WriteLine);

var airportArg = Array.IndexOf(args, "--airport");
if (airportArg >= 0 && airportArg + 1 < args.Length)
    return await AirportCommand.RunAsync(args[airportArg + 1], args.Contains("--refresh"));

var inv = CultureInfo.InvariantCulture;
var logLines = new Queue<string>();
var logLock = new Lock();
var stateLock = new Lock();
UserAircraftState? latest = null;
var updatesInWindow = 0;
var measuredHz = 0.0;
using var exit = new ManualResetEventSlim(false);

void Log(string message)
{
    lock (logLock)
    {
        logLines.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
        while (logLines.Count > 8)
            logLines.Dequeue();
    }
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // salir de forma ordenada para cerrar SimConnect
    exit.Set();
};

using var bridge = new SimConnectBridge(Log);
bridge.ConnectionChanged += connected =>
{
    if (!connected)
        lock (stateLock) latest = null;
};
bridge.UserStateUpdated += s =>
{
    lock (stateLock)
    {
        latest = s;
        updatesInWindow++;
    }
};
bridge.Start();

var interactive = !Console.IsOutputRedirected;
if (interactive)
{
    Console.Clear();
    Console.CursorVisible = false;
}

var window = Stopwatch.StartNew();
while (!exit.Wait(200))
{
    UserAircraftState? s;
    lock (stateLock)
    {
        if (window.Elapsed.TotalSeconds >= 1)
        {
            measuredHz = updatesInWindow / window.Elapsed.TotalSeconds;
            updatesInWindow = 0;
            window.Restart();
        }
        s = latest;
    }
    Render(s);
}

if (interactive)
    Console.CursorVisible = true;
Console.WriteLine();
Console.WriteLine("Cerrando conexión...");
return 0;

void Render(UserAircraftState? s)
{
    var lines = new List<string>
    {
        "LUAW Probe — Ctrl+C para salir",
        $"Conexión: {(bridge.IsConnected ? "CONECTADO" : "esperando a MSFS")}   Actualización: {measuredHz.ToString("0.0", inv)} Hz",
        "",
    };

    if (s is { } st)
    {
        lines.Add($"Posición:        {st.Position.LatDeg.ToString("0.000000", inv)}, {st.Position.LonDeg.ToString("0.000000", inv)}");
        lines.Add($"Altitud:         {st.AltitudeMslFt.ToString("0", inv)} ft MSL   {st.AltitudeAglFt.ToString("0", inv)} ft AGL");
        lines.Add($"Rumbo verdadero: {st.HeadingTrueDeg.ToString("000.0", inv)}°");
        lines.Add($"Velocidad:       {st.IndicatedAirspeedKt.ToString("0", inv)} kt IAS   {st.GroundSpeedKt.ToString("0", inv)} kt GS");
        lines.Add($"Vel. vertical:   {(Math.Round(st.VerticalSpeedFpm) + 0.0).ToString("0", inv)} ft/min"); // + 0.0 evita "-0"
        lines.Add($"En tierra:       {(st.OnGround ? "sí" : "no")}");
        lines.Add($"COM1 activa:     {(st.Com1ActiveKhz / 1000.0).ToString("0.000", inv)} MHz");
        lines.Add($"Transpondedor:   {(st.TransponderCode >= 0 ? st.TransponderCode.ToString("0000", inv) : "valor inválido (ver log)")}");
    }
    else
    {
        lines.Add("(sin datos del avión todavía)");
    }

    lines.Add("");
    lines.Add("Log:");
    lock (logLock)
        lines.AddRange(logLines);

    if (interactive)
    {
        Console.SetCursorPosition(0, 0);
        var width = Math.Max(Console.WindowWidth - 1, 40);
        foreach (var line in lines)
            Console.WriteLine(line.Length > width ? line[..width] : line.PadRight(width));
        for (var i = lines.Count; i < 22; i++)
            Console.WriteLine(new string(' ', width));
    }
    else
    {
        Console.WriteLine(string.Join(" | ", lines.Where(l => l.Length > 0)));
    }
}
