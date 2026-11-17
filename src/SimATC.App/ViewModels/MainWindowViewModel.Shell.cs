using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Opción de un selector de la configuración (valor y texto en español).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Interfaz de la etapa 4 (boceto aprobado): paneles, barra superior, comunicaciones, tráfico, configuración y
/// diagnóstico. La lógica de tráfico sigue en MainWindowViewModel.cs.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _settings = AppSettings.Load(AppSettings.DefaultPath);
    private string? _lastTickError;

    /// <summary>Panel de comunicaciones (acoplado al radar y en el panel compacto).</summary>
    public CommsViewModel Comms { get; } = new();

    // --- Navegación ---

    [ObservableProperty]
    public partial string Panel { get; set; } = "radar";

    public bool IsRadar => Panel == "radar";
    public bool IsComms => Panel == "comms";
    public bool IsPlan => Panel == "plan";
    public bool IsTrafficPanel => Panel == "traffic";
    public bool IsSettings => Panel == "settings";
    public bool IsDiag => Panel == "diag";
    public bool IsBriefing => Panel == "briefing";

    partial void OnPanelChanged(string value)
    {
        UpdateDiagnostics();
        if (value == "traffic")
            RefreshTraffic();
        if (value == "manual")
            EnsureManual();
        foreach (var name in new[] { nameof(IsRadar), nameof(IsComms), nameof(IsPlan), nameof(IsTrafficPanel), nameof(IsSettings), nameof(IsDiag), nameof(IsBriefing), nameof(IsFlights), nameof(IsManual),
                     nameof(ShowDockedRadar), nameof(ShowRadarElsewhere) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Navigate(string panel) => Panel = panel;

    // --- Barra superior ---

    [ObservableProperty]
    public partial string UtcTime { get; set; } = "";

    public bool IsConnected => _bridge.IsConnected;
    public string ConnectionText => _reconnecting ? Tr.T("Reconectando…") : _bridge.IsConnected ? Tr.T("MSFS conectado") : Tr.T("Esperando a MSFS");

    private bool _reconnecting;

    /// <summary>Corta y vuelve a conectar con el simulador al instante. Los aviones creados por LUAW se borran.</summary>
    [RelayCommand]
    private void Reconnect()
    {
        _reconnecting = true;
        OnPropertyChanged(nameof(ConnectionText));
        Log(Tr.T("Reconectando con el simulador…"));
        _bridge.Reconnect();
        DispatcherTimer.RunOnce(() =>
        {
            _reconnecting = false;
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(IsConnected));
        }, TimeSpan.FromSeconds(3));
    }
    public string AirportText => Airport is { } a ? $"{a.Icao} · {a.Name}" : _icao ?? Tr.T("Sin aeropuerto");

    public bool ModeText => _settings.Mode == CommsMode.Text;
    public bool ModeVoice => _settings.Mode == CommsMode.Voice;
    public bool ModeCopilot => _settings.Mode == CommsMode.Copilot;

    [RelayCommand]
    private void SetMode(string mode)
    {
        SelectedMode = ModeChoices.First(c => c.Value.ToString() == mode);
    }

    // --- Radar: ruta del piloto (Ground en el hito 4c; por ahora una ruta de prueba desde Diagnóstico) ---

    [ObservableProperty]
    public partial IReadOnlyList<GeoPoint>? UserRoute { get; set; }

    [ObservableProperty]
    public partial GeoPoint? UserHoldShort { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PlanPoint>? PlanRoute { get; set; }

    [ObservableProperty]
    public partial bool ShowStands { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowPlan { get; set; } = true;

    // Capas del radar global (ADR-062).
    [ObservableProperty]
    public partial bool ShowFixes { get; set; }

    [ObservableProperty]
    public partial bool ShowNavaids { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowAirways { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWorldAirports { get; set; } = true;

    /// <summary>
    /// Prueba visual del hito 4a: la ruta que el ATC le daría al piloto desde donde está hasta el punto de espera de la
    /// cabecera elegida (la más corta del grafo). En el hito 4c la asigna Ground al autorizar el rodaje.
    /// </summary>
    [RelayCommand]
    private void ShowTestTaxiRoute()
    {
        if (_graph is not { } graph || UserState is not { } user)
        {
            TrafficStatus = Tr.T("Para la ruta de prueba: conectado, aeropuerto cargado y tu avión en el aeropuerto.");
            return;
        }
        var wanted = SelectedRunwayEnd == AutoRunway ? DepartureEnds.FirstOrDefault() : SelectedRunwayEnd;
        var end = _runwayEnds.Find(r => r.Label == wanted);
        if (end.Label is null || graph.DepartureEntryOrBacktrack(end.Runway, end.Primary, out _) is not { } entry)
            return;
        var here = graph.Projection.ToLocal(user.Position);
        var start = -1;
        var best = double.MaxValue;
        for (var n = 0; n < graph.NodeCount; n++)
        {
            if (graph.IsParking(n) || graph.RunwayAt(n) >= 0)
                continue;
            var d = LocalPoint.Distance(graph.Position(n), here);
            if (d < best)
            {
                best = d;
                start = n;
            }
        }
        if (start < 0 || graph.FindRoute(start, entry.HoldNode) is not { } route)
        {
            TrafficStatus = Tr.T("No encontré una ruta desde tu posición.");
            return;
        }
        UserRoute = [user.Position, .. route.Nodes.Select(n => graph.Projection.ToGeo(graph.Position(n)))];
        UserHoldShort = graph.Projection.ToGeo(graph.Position(entry.HoldNode));
        TrafficStatus = Tr.F("Ruta de prueba a la {0}: vía {1}.", end.Label, string.Join(", ", route.Taxiways.Distinct()));
        Panel = "radar";
    }

    [RelayCommand]
    private void ClearTestTaxiRoute()
    {
        UserRoute = null;
        UserHoldShort = null;
    }

    // --- Configuración (RF-41) ---

    public IReadOnlyList<Choice<CommsMode>> ModeChoices { get; } =
        [new(CommsMode.Text, Tr.T("Texto")), new(CommsMode.Voice, Tr.T("Voz")), new(CommsMode.Copilot, Tr.T("Copiloto"))];

    public IReadOnlyList<Choice<Strictness>> StrictnessChoices { get; } =
        [new(Strictness.Realistic, Tr.T("Realista")), new(Strictness.Permissive, Tr.T("Permisivo (práctica)"))];

    public Choice<CommsMode> SelectedMode
    {
        get => ModeChoices.First(c => c.Value == _settings.Mode);
        set => Change(_settings with { Mode = value.Value }, nameof(SelectedMode), nameof(ModeText), nameof(ModeVoice), nameof(ModeCopilot));
    }

    public IReadOnlyList<Choice<Core.Comms.ApproachWatchLevel>> ApproachWatchChoices { get; } =
    [
        new(Core.Comms.ApproachWatchLevel.Full, Tr.T("Completa (avisos y motor y al aire)")),
        new(Core.Comms.ApproachWatchLevel.AdvisoryOnly, Tr.T("Solo avisos")),
        new(Core.Comms.ApproachWatchLevel.Off, Tr.T("Apagada")),
    ];

    /// <summary>Vigilancia de la aproximación (ADR-108); se aplica al momento.</summary>
    public Choice<Core.Comms.ApproachWatchLevel> SelectedApproachWatch
    {
        get => ApproachWatchChoices.First(c => c.Value == _settings.ApproachWatch);
        set => Change(_settings with { ApproachWatch = value.Value }, nameof(SelectedApproachWatch));
    }

    public Choice<Strictness> SelectedStrictness
    {
        get => StrictnessChoices.First(c => c.Value == _settings.Strictness);
        set => Change(_settings with { Strictness = value.Value }, nameof(SelectedStrictness), nameof(StrictnessText));
    }

    public string StrictnessText => SelectedStrictness.Label;

    public bool CopilotTunesCom1
    {
        get => _settings.CopilotTunesCom1;
        set => Change(_settings with { CopilotTunesCom1 = value }, nameof(CopilotTunesCom1));
    }

    public IReadOnlyList<Choice<CopilotReadbacks>> CopilotReadbackChoices { get; } =
        [new(CopilotReadbacks.Never, Tr.T("Nunca (colaciono yo)")), new(CopilotReadbacks.FrequencyChanges, Tr.T("Solo los cambios de frecuencia")),
         new(CopilotReadbacks.Always, Tr.T("Siempre"))];

    public Choice<CopilotReadbacks> SelectedCopilotReadbacks
    {
        get => CopilotReadbackChoices.First(c => c.Value == _settings.CopilotReadbacks);
        set => Change(_settings with { CopilotReadbacks = value.Value }, nameof(SelectedCopilotReadbacks));
    }

    public IReadOnlyList<Choice<string>> UiLanguageChoices { get; } =
        [new("", Tr.T("Automático (el de Windows)")), new("en", Tr.T("English")), new("es", Tr.T("Español"))];

    /// <summary>Idioma de la interfaz: se guarda y se aplica al volver a abrir la app (los textos se cargan con la ventana).</summary>
    public Choice<string> SelectedUiLanguage
    {
        get => UiLanguageChoices.First(c => c.Value == (_settings.UiLanguage ?? ""));
        set
        {
            if (value.Value == (_settings.UiLanguage ?? ""))
                return;
            Change(_settings with { UiLanguage = value.Value.Length == 0 ? null : value.Value }, nameof(SelectedUiLanguage));
            Notice = new NoticeDialog(Tr.T("Idioma de la interfaz"),
                Tr.T("El cambio se aplica la próxima vez que abras LUAW. El idioma de la radio se elige aparte."), Tr.T("Entendido"), () => { });
        }
    }

    public IReadOnlyList<Choice<AtcLanguage>> AtcLanguageChoices { get; } =
        [new(AtcLanguage.English, Tr.T("Inglés (OACI)")), new(AtcLanguage.Spanish, Tr.T("Español (OACI)"))];

    /// <summary>Idioma del ATC (§8.6): al cambiarlo, la conversación con el ATC empieza de nuevo.</summary>
    public Choice<AtcLanguage> SelectedAtcLanguage
    {
        get => AtcLanguageChoices.First(c => c.Value == _settings.AtcLanguage);
        set
        {
            if (value.Value == _settings.AtcLanguage)
                return;
            Change(_settings with { AtcLanguage = value.Value }, nameof(SelectedAtcLanguage));
            ApplyAtcLanguage();
        }
    }

    public IReadOnlyList<Choice<PhraseologyStyle>> PhraseologyChoices { get; } =
        [new(PhraseologyStyle.Auto, Tr.T("Automática (FAA en EE. UU.)")), new(PhraseologyStyle.Icao, "OACI"), new(PhraseologyStyle.Faa, "FAA")];

    /// <summary>Fraseología en inglés (ADR-099): al cambiarla, la conversación con el ATC empieza de nuevo.</summary>
    public Choice<PhraseologyStyle> SelectedPhraseology
    {
        get => PhraseologyChoices.First(c => c.Value == _settings.Phraseology);
        set
        {
            if (value.Value == _settings.Phraseology)
                return;
            Change(_settings with { Phraseology = value.Value }, nameof(SelectedPhraseology));
            ApplyAtcLanguage();
        }
    }

    /// <summary>ADR-081: el ATC respeta la pista de salida del OFP.</summary>
    public bool UsePlanRunways
    {
        get => _settings.UsePlanRunways;
        set
        {
            Change(_settings with { UsePlanRunways = value }, nameof(UsePlanRunways));
            UpdatePlanViews();
        }
    }

    public bool AutoTraffic
    {
        get => _settings.AutoTraffic;
        set
        {
            Change(_settings with { AutoTraffic = value }, nameof(AutoTraffic));
            OnPropertyChanged(nameof(ShowStartTraffic));
        }
    }

    public decimal TrafficDensity
    {
        get => _settings.TrafficDensity;
        set => Change(_settings with { TrafficDensity = (int)Math.Clamp(value, 0, 300) }, nameof(TrafficDensity), nameof(TrafficLevelSummary));
    }

    public bool MessageSound
    {
        get => _settings.MessageSound;
        set => Change(_settings with { MessageSound = value }, nameof(MessageSound));
    }

    public bool CopilotRoutineCalls
    {
        get => _settings.CopilotRoutineCalls;
        set => Change(_settings with { CopilotRoutineCalls = value }, nameof(CopilotRoutineCalls));
    }

    public bool CopilotAskClearance
    {
        get => _settings.CopilotAskClearance;
        set => Change(_settings with { CopilotAskClearance = value }, nameof(CopilotAskClearance));
    }

    public bool CopilotAskPushback
    {
        get => _settings.CopilotAskPushback;
        set => Change(_settings with { CopilotAskPushback = value }, nameof(CopilotAskPushback));
    }

    public bool CopilotAskTaxi
    {
        get => _settings.CopilotAskTaxi;
        set => Change(_settings with { CopilotAskTaxi = value }, nameof(CopilotAskTaxi));
    }

    public bool CopilotAskReady
    {
        get => _settings.CopilotAskReady;
        set => Change(_settings with { CopilotAskReady = value }, nameof(CopilotAskReady));
    }

    public bool CopilotRescue
    {
        get => _settings.CopilotRescue;
        set => Change(_settings with { CopilotRescue = value }, nameof(CopilotRescue));
    }

    public IReadOnlyList<Choice<int>> TuneRadioChoices { get; } = [new(1, "COM1"), new(2, "COM2")];

    public Choice<int> SelectedTuneRadio
    {
        get => TuneRadioChoices.FirstOrDefault(c => c.Value == _settings.TuneRadio) ?? TuneRadioChoices[0];
        set => Change(_settings with { TuneRadio = value.Value }, nameof(SelectedTuneRadio));
    }

    public IReadOnlyList<Choice<int>> ReadbackTimeChoices { get; } =
        [new(30, "30 s"), new(45, "45 s"), new(60, Tr.T("1 minuto")), new(90, Tr.T("1,5 minutos"))];

    public Choice<int> SelectedReadbackTime
    {
        get => ReadbackTimeChoices.FirstOrDefault(c => c.Value == _settings.ReadbackTimeS) ?? ReadbackTimeChoices[1];
        set => Change(_settings with { ReadbackTimeS = value.Value }, nameof(SelectedReadbackTime));
    }

    public bool TuneToStandby
    {
        get => _settings.TuneToStandby;
        set => Change(_settings with { TuneToStandby = value }, nameof(TuneToStandby));
    }

    public decimal MaxGround
    {
        get => _settings.MaxGround;
        set
        {
            Change(_settings with { MaxGround = (int)value }, nameof(MaxGround));
            ApplyTrafficLimits();
        }
    }

    public decimal MaxAir
    {
        get => _settings.MaxAir;
        set
        {
            Change(_settings with { MaxAir = (int)value }, nameof(MaxAir));
            ApplyTrafficLimits();
        }
    }

    /// <summary>Radio (NM) en el que los vuelos se cargan en el simulador (ADR-089).</summary>
    public decimal InjectRadiusNm
    {
        get => _settings.InjectRadiusNm;
        set
        {
            Change(_settings with { InjectRadiusNm = (int)value }, nameof(InjectRadiusNm));
            Core.Engine.TrafficEngine.SetInjectRadiusNm(_settings.InjectRadiusNm);
        }
    }

    public decimal RadarFps
    {
        get => _settings.RadarFps;
        set => Change(_settings with { RadarFps = (int)value }, nameof(RadarFps), nameof(RadarFpsInt));
    }

    public int RadarFpsInt => _settings.RadarFps;

    public string SimBriefUser
    {
        get => _settings.SimBriefUser;
        set => Change(_settings with { SimBriefUser = value.Trim() }, nameof(SimBriefUser));
    }

    public bool CompactAlwaysOnTop
    {
        get => _settings.CompactAlwaysOnTop;
        set => Change(_settings with { CompactAlwaysOnTop = value }, nameof(CompactAlwaysOnTop));
    }

    private void Change(AppSettings next, params string[] properties)
    {
        if (next == _settings)
            return;
        _settings = next;
        try
        {
            _settings.Save(AppSettings.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Tr.F("No se pudo guardar la configuración ({0}).", ex.Message));
        }
        foreach (var p in properties)
            OnPropertyChanged(p);
    }

    // --- Radar en su propia ventana ---

    /// <summary>El radar está en su propia ventana: la principal muestra las comunicaciones a lo ancho.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockedRadar), nameof(ShowRadarElsewhere), nameof(ShowAtcCard))]
    public partial bool RadarDetached { get; set; }

    public bool ShowDockedRadar => IsRadar && !RadarDetached;
    public bool ShowRadarElsewhere => IsRadar && RadarDetached;

    /// <summary>Lo hace la ventana (es una vista); el modelo solo avisa.</summary>
    public event Action? RadarWindowRequested;
    public event Action? RadarDockRequested;

    [RelayCommand]
    private void OpenRadarWindow() => RadarWindowRequested?.Invoke();

    [RelayCommand]
    private void DockRadar() => RadarDockRequested?.Invoke();

    // --- Panel compacto (RF-42c) ---

    /// <summary>Lo abre la ventana (es una vista); el modelo solo avisa.</summary>
    public event Action? CompactRequested;

    [RelayCommand]
    private void OpenCompact() => CompactRequested?.Invoke();

    // --- Arranque de la interfaz ---

    private void InitShell()
    {
        _clock.Tick += (_, _) =>
        {
            UtcTime = DateTime.UtcNow.ToString("HH:mm") + " Z";
            UpdateDiagnostics();
            RelocateTick();
            try
            {
                AtcTick();
            }
            catch (Exception ex)
            {
                // Un error del ATC no cierra la app: queda en el log (una vez por error distinto) y el tick sigue.
                var key = ex.GetType().Name + ex.Message;
                if (key != _lastTickError)
                    Log(Tr.F("Error en el ATC (la app sigue): {0}", ex));
                _lastTickError = key;
            }
            FlushSessionLog();
        };
        SelectedTrafficFilter = TrafficFilters[0];
        _bridge.UserLoadUpdated += OnUserLoad;
        _bridge.AmbientWeatherUpdated += OnAmbientWeather;
        InitPlan();
        _clock.Start();
        UtcTime = DateTime.UtcNow.ToString("HH:mm") + " Z";
        InitAtc();
        InitVoice();
        InitMic();
        InitEfb();
    }
}
