using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core;
using SimATC.Core.Engine;
using SimATC.Core.Fleet;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Un dato del panel Diagnóstico o del resumen del plan (nombre y valor).</summary>
public sealed record DiagItem(string Name, string Value, bool Warn = false);

/// <summary>Lo que muestra la vista de un <see cref="DiagItem"/>: se actualiza en su lugar, sin recrear los controles.</summary>
public sealed partial class DiagRow(string name) : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = name;
    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial bool Warn { get; set; }

    /// <summary>Lleva <paramref name="rows"/> a <paramref name="items"/>, reutilizando las filas por posición.</summary>
    public static void Sync(ObservableCollection<DiagRow> rows, IReadOnlyList<DiagItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i == rows.Count)
                rows.Add(new DiagRow(items[i].Name));
            var row = rows[i];
            row.Name = items[i].Name;
            row.Value = items[i].Value;
            row.Warn = items[i].Warn;
        }
        while (rows.Count > items.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}

/// <summary>Panel Diagnóstico: estado de las fuentes, métricas del motor y del ATC, herramientas y log filtrable.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Compilación de desarrollo (Debug): muestra las herramientas de prueba del tráfico. En la versión publicada no.</summary>
    public bool IsDeveloperBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Líneas del log que se guardan para el panel Diagnóstico.</summary>
    public const int MaxLogLines = 2000;

    private readonly List<string> _logLines = [];

    /// <summary>Lo que se ve del log (las líneas que pasan el filtro).</summary>
    public ObservableCollection<string> VisibleLog { get; } = [];

    [ObservableProperty]
    public partial string LogFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool LogAutoScroll { get; set; } = true;

    [ObservableProperty]
    public partial IReadOnlyList<DiagItem> SourceItems { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DiagItem> EngineItems { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DiagItem> AtcItems { get; set; } = [];

    public ObservableCollection<DiagRow> SourceRows { get; } = [];
    public ObservableCollection<DiagRow> EngineRows { get; } = [];
    public ObservableCollection<DiagRow> AtcRows { get; } = [];

    partial void OnSourceItemsChanged(IReadOnlyList<DiagItem> value) => DiagRow.Sync(SourceRows, value);

    partial void OnEngineItemsChanged(IReadOnlyList<DiagItem> value) => DiagRow.Sync(EngineRows, value);

    partial void OnAtcItemsChanged(IReadOnlyList<DiagItem> value) => DiagRow.Sync(AtcRows, value);

    partial void OnLogFilterChanged(string value)
    {
        VisibleLog.Clear();
        foreach (var line in _logLines)
        {
            if (Matches(line))
                VisibleLog.Add(line);
        }
    }

    /// <summary>Cuántos logs de sesión se conservan en disco (los más viejos se borran al empezar una sesión).</summary>
    public const int SessionLogsKept = 10;

    // Log de la sesión en disco (ADR-127): se escribe solo, sin esperar al botón ni al cierre (la app se cierra a menudo
    // desde Visual Studio, y el panel solo guarda las últimas 2000 líneas).
    private readonly List<string> _logPending = [];
    private string? _sessionLogPath;
    private bool _sessionLogFailed;

    /// <summary>
    /// Escribe en <c>%LOCALAPPDATA%\LUAW\logs\sesion-(fecha).txt</c> las líneas nuevas del log. Desde el reloj de 1 s y
    /// al cerrar; si no hay líneas nuevas no toca el disco. Si el disco falla una vez, no se insiste.
    /// </summary>
    public void FlushSessionLog()
    {
        if (_logPending.Count == 0 || _sessionLogFailed)
            return;
        try
        {
            if (_sessionLogPath is null)
            {
                var dir = Path.Combine(AppPaths.Root, "logs");
                Directory.CreateDirectory(dir);
                // Solo los "sesion-*" que escribe la app: los logs guardados a mano y los de cada vuelo no se tocan.
                var old = Directory.GetFiles(dir, "sesion-*.txt");
                Array.Sort(old, StringComparer.Ordinal);
                for (var i = 0; i <= old.Length - SessionLogsKept; i++)
                    File.Delete(old[i]);
                _sessionLogPath = Path.Combine(dir, $"sesion-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            }
            File.AppendAllLines(_sessionLogPath, _logPending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _sessionLogFailed = true;
        }
        _logPending.Clear();
    }

    private bool Matches(string line) =>
        LogFilter.Length == 0 || line.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void AddLogLine(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Dispatcher.UIThread.Post(() =>
        {
            _logLines.Add(line);
            _logPending.Add(line);
            if (_logLines.Count > MaxLogLines)
            {
                var dropped = _logLines[0];
                _logLines.RemoveAt(0);
                if (VisibleLog.Count > 0 && ReferenceEquals(VisibleLog[0], dropped))
                    VisibleLog.RemoveAt(0);
            }
            if (Matches(line))
                VisibleLog.Add(line);
        });
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logLines.Clear();
        VisibleLog.Clear();
    }

    /// <summary>Todo el log visible como texto (para copiarlo).</summary>
    public string VisibleLogText() => string.Join(Environment.NewLine, VisibleLog);

    /// <summary>Guarda el log completo en <c>%LOCALAPPDATA%\LUAW\logs</c> y abre la carpeta.</summary>
    [RelayCommand]
    private void SaveLog()
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"simatc-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(path, _logLines);
            TrafficStatus = Tr.F("Log guardado en {0}.", path);
            OpenFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrafficStatus = Tr.F("No se pudo guardar el log ({0}).", ex.Message);
        }
    }

    /// <summary>Guarda la conversación de una radio (1, 2 o 3) en <c>%LOCALAPPDATA%\LUAW\logs</c> y abre la carpeta.</summary>
    [RelayCommand]
    private void SaveComLog(string radio)
    {
        if (!int.TryParse(radio, out var n) || Comms.Channels.FirstOrDefault(c => c.Radio == n) is not { } channel)
            return;
        try
        {
            var dir = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"com{n}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(path, channel.Transcript);
            TrafficStatus = Tr.F("Log de COM{0} guardado en {1}.", n, path);
            OpenFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrafficStatus = Tr.F("No se pudo guardar el log de COM{0} ({1}).", n, ex.Message);
        }
    }

    /// <summary>
    /// Al terminar un vuelo (motores apagados en el puesto): guarda solo, en un archivo, el log del motor y las tres radios,
    /// para revisarlo después sin capturas. Devuelve la ruta o null.
    /// </summary>
    private string? SaveFlightLog(string name)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"vuelo-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var lines = new List<string>();
            foreach (var c in Comms.Channels)
            {
                lines.Add($"===== COM{c.Radio} =====");
                lines.AddRange(c.Transcript);
                lines.Add("");
            }
            lines.Add(Tr.T("===== Log de LUAW ====="));
            lines.AddRange(_logLines);
            File.WriteAllLines(path, lines);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Tr.F("No se pudo guardar el log del vuelo ({0}).", ex.Message));
            return null;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.Root);

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void ResetMaxFrameTime() => _engine?.ResetMaxFrameTime();

    private bool CanReload() => !_busy && !_loading && _bridge.IsConnected;

    /// <summary>
    /// Vuelve a leer el aeropuerto desde el simulador, sin la caché (después de instalar o actualizar un escenario).
    /// Primero borra el tráfico: sus puestos y rutas eran del aeropuerto anterior.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAirportAsync()
    {
        SetBusy(true);
        try
        {
            if (_engine is not null || _parked.Snapshot().Count > 0)
                ClearTrafficCore();
            ClearTestTaxiRoute();
            if (_icao is not { } icao)
                return;
            Status = Tr.F("Conectado · leyendo {0} desde el simulador…", icao);
            var (airport, fromCache) = await _airports.GetAsync(icao, true, CancellationToken.None);
            SetAirport(airport, fromCache);
            Log(Tr.F("Aeropuerto {0} releído del simulador.", airport.Icao));
        }
        catch (Exception ex)
        {
            Status = Tr.F("Error al releer el aeropuerto: {0}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Vuelve a revisar los modelos de FSLTL y AIG (después de instalar o quitar liveries).</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadCatalogAsync()
    {
        SetBusy(true);
        try
        {
            TrafficStatus = Tr.T("Revisando el catálogo de modelos…");
            _matcher = await FleetCatalog.LoadAsync(_bridge, AppPaths.FleetCache, Log, CancellationToken.None, _settings.ExtraModelFolders);
            TrafficStatus = Tr.F("Catálogo listo: {0} modelos.", _matcher.ModelCount);
        }
        catch (Exception ex)
        {
            TrafficStatus = Tr.F("Error al revisar el catálogo: {0}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void NotifyToolCommands()
    {
        ReloadAirportCommand.NotifyCanExecuteChanged();
        ReloadCatalogCommand.NotifyCanExecuteChanged();
        LaunchBatchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TrafficPaused));
    }

    /// <summary>Una vez por segundo (y solo con el panel abierto): fuentes, motor y ATC.</summary>
    private void UpdateDiagnostics()
    {
        if (!IsDiag)
            return;
        SourceItems =
        [
            new(Tr.T("Simulador"), _bridge.IsConnected ? Tr.T("conectado") + (_bridge.SimVersion is { } v ? $" · {v}" : "") : Tr.T("esperando"), !_bridge.IsConnected),
            new(Tr.T("Aeropuerto"), Airport is { } a
                ? Tr.F("{0} · {1} · {2} pistas, {3} puntos, {4} puestos", a.Icao, (_airportFromCache ? Tr.T("caché") : Tr.T("simulador")), a.Runways.Count, a.TaxiPoints.Count, a.Parkings.Count)
                : Tr.T("sin cargar"), Airport is null),
            new(Tr.T("Grafo de rodaje"), _graph is { } g ? Tr.F("{0} nodos", g.NodeCount) : "—"),
            new(Tr.T("Catálogo de modelos"), _matcher is { } m ? Tr.F("{0} modelos (FSLTL + AIG)", m.ModelCount) : Tr.T("sin cargar"), _matcher is null),
            new(Tr.T("Rutas del aeropuerto"), _routes is null ? Tr.T("sin lista (no hay tráfico)") : Tr.T("cargadas"), _routes is null),
            new(Tr.T("Datos ATC"), _atcData is { } atc ? $"{atc.Source.Title}{(atc.Reviewed ? "" : Tr.T(" · sin revisar"))}" : Tr.T("sin archivo data/atc (solo datos del simulador)")),
            new(Tr.T("Clima"), Weather is { } wx
                ? Tr.F("{0:000}°M {1:0}{2} kt (media 2 min) · {3:0} °C · QNH {4:0.0} · {5} · MAGVAR {6:+0.0;-0.0}° · vis. partículas {7:0.#} km (no se usa)", wx.WindFromMagneticDeg, wx.WindKt, (wx.GustKt is { } gust ? $"G{gust:0}" : ""), wx.TemperatureC, wx.QnhHpa, wx.Precipitation switch { Core.Atc.Precipitation.Rain => Tr.T("lluvia"), Core.Atc.Precipitation.Snow => Tr.T("nieve"), _ => Tr.T("sin precipitación") }, wx.MagVarDeg, wx.ParticleVisibilityM / 1000)
                : Tr.T("sin observación (solo se conoce en la posición de tu avión)"), Weather is null),
            new(Tr.T("Pista en uso"), ActiveRunwayText.Length > 0 ? ActiveRunwayText : "—"),
            new(Tr.T("Configuración"), AppSettings.DefaultPath),
        ];
        if (_engine is not { } engine)
        {
            EngineItems = [new(Tr.T("Motor"), Tr.T("sin iniciar"))];
            AtcItems = [];
            return;
        }
        var s = engine.Stats;
        EngineItems =
        [
            new(Tr.T("Estado"), engine.Paused ? Tr.T("congelado") : Tr.T("corriendo"), engine.Paused),
            new(Tr.T("Aviones en movimiento"), engine.ActiveCount.ToString()),
            new(Tr.T("Estacionados"), _parked.Snapshot().Count.ToString()),
            new(Tr.T("Tiempo por cuadro"), Tr.F("{0:0} µs · media {1:0} µs · máx. {2:0} µs", s.LastFrameUs, s.AvgFrameUs, s.MaxFrameUs), s.AvgFrameUs > 1000),
            new(Tr.T("Desglose (media)"), Tr.F("lógica {0:0} µs · simulador {1:0} µs", s.AvgLogicUs, s.AvgBridgeUs)),
            new(Tr.T("Cuadros lentos"), Tr.F("{0} (más de {1:0} ms; detalle en el log)", s.SlowFrames, TrafficEngine.SlowFrameUs / 1000), s.SlowFrames > 0),
            new(Tr.T("Cuadros"), s.Frames.ToString("N0")),
            new(Tr.T("Reloj simulado"), TimeSpan.FromSeconds(engine.Clock.SimTime).ToString(@"hh\:mm\:ss")),
        ];
        var safety = engine.Coordinator;
        var tower = engine.Control.Tower;
        AtcItems =
        [
            new(Tr.T("Bloqueos detectados"), safety.DeadlocksDetected.ToString()),
            new(Tr.T("Bloqueos resueltos"), safety.DeadlocksResolved.ToString()),
            new(Tr.T("Bloqueos sin resolver"), safety.DeadlocksUnresolved.ToString(), safety.DeadlocksUnresolved > 0),
            new(Tr.T("Desvíos de Ground"), s.Reroutes.ToString()),
            new(Tr.T("Incursiones del piloto"), tower.PilotIncursions.ToString(), tower.PilotIncursions > 0),
            new(Tr.T("Incursiones de la IA"), tower.AiIncursions.ToString(), tower.AiIncursions > 0),
            new(Tr.T("Despegues abortados"), tower.RejectedTakeoffs.ToString(), tower.RejectedTakeoffs > 0),
        ];
    }
}
