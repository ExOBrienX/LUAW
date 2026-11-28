using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Barra de progreso del tráfico (ADR-076, pedido de Marco): etapas al cargar un aeropuerto o activar el destino en vuelo
/// (aeropuerto, catálogo de modelos, rutas, estacionados, listo) y el botón "Iniciar tráfico" si el automático está apagado.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial double TrafficProgress { get; set; }

    [ObservableProperty]
    public partial string TrafficStage { get; set; } = "";

    [ObservableProperty]
    public partial bool TrafficLoading { get; set; }

    // El tráfico de esta sesión ya se inició con el botón (ADR-084).
    private bool _trafficStarted;

    /// <summary>El botón "Iniciar tráfico": hasta que el piloto lo presiona en esta sesión, y nada cargándose.</summary>
    public bool ShowStartTraffic => !TrafficLoading && !_trafficStarted;

    partial void OnTrafficLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowStartTraffic));

    /// <summary>Una etapa de la carga del tráfico (0-100 %); al 100 % la barra se oculta.</summary>
    private void Stage(string text, double percent)
    {
        TrafficStage = text;
        TrafficProgress = Math.Clamp(percent, 0, 100);
        TrafficLoading = percent < 100;
        if (text.Length > 0)
            ModalLine(text);
    }

    // ---- Ventana de carga (ADR-111, pedido de Marco 2026-11-09) ----

    /// <summary>La ventana de carga sobre la app (fondo oscurecido) mientras se inicia o rellena el tráfico.</summary>
    [ObservableProperty]
    public partial bool LoadingModalOpen { get; set; }

    [ObservableProperty]
    public partial string LoadingTitle { get; set; } = "";

    /// <summary>Lo que se está haciendo ahora mismo (el avión que se estaciona, la fase).</summary>
    [ObservableProperty]
    public partial string LoadingDetail { get; set; } = "";

    /// <summary>Terminó: se muestra el resumen y el botón OK.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingWorking))]
    public partial bool LoadingDone { get; set; }

    public bool LoadingWorking => !LoadingDone;

    [ObservableProperty]
    public partial string LoadingSummary { get; set; } = "";

    /// <summary>Las últimas fases, de la más vieja a la más nueva.</summary>
    public ObservableCollection<string> LoadingLines { get; } = [];

    private const int LoadingLinesMax = 7;

    private void OpenLoadingModal(string title)
    {
        LoadingLines.Clear();
        LoadingTitle = title;
        LoadingDetail = "";
        LoadingSummary = "";
        LoadingDone = false;
        TrafficProgress = 0;
        LoadingModalOpen = true;
    }

    /// <summary>Una línea en la ventana de carga (si está abierta).</summary>
    private void ModalLine(string text)
    {
        if (!LoadingModalOpen || LoadingDone)
            return;
        LoadingDetail = text;
        if (LoadingLines.Count > 0 && LoadingLines[^1] == text)
            return;
        LoadingLines.Add(text);
        while (LoadingLines.Count > LoadingLinesMax)
            LoadingLines.RemoveAt(0);
    }

    /// <summary>Lo que va mientras se estacionan los aviones (no se guarda como fase: cambia con cada avión).</summary>
    private void ModalDetail(string text, double percent)
    {
        if (!LoadingModalOpen || LoadingDone)
            return;
        LoadingDetail = text;
        TrafficProgress = Math.Clamp(percent, 0, 100);
    }

    private void FinishLoadingModal(string summary)
    {
        if (!LoadingModalOpen)
            return;
        TrafficProgress = 100;
        LoadingSummary = summary;
        LoadingDone = true;
    }

    [RelayCommand]
    private void CloseLoadingModal() => LoadingModalOpen = false;

    /// <summary>"Listo: 74 estacionados (62 en puertas, …)": el resumen de la ventana de carga.</summary>
    private string LoadingSummaryText()
    {
        // Si algo falló o no hay rutas, se dice eso (no un "listo" falso).
        if (TrafficStatus.StartsWith("Error", StringComparison.Ordinal) || _routes is null)
            return TrafficStatus.Length > 0 ? TrafficStatus : Tr.T("No se pudo iniciar el tráfico aquí.");
        if (Airport is not { } ap)
            return Tr.T("No hay aeropuerto cargado todavía: conecta con MSFS y espera a que cargue.");
        var parked = _parked.Snapshot();
        var gates = parked.Count(p => StandIsGate(p.Aircraft.Parking));
        var cargo = parked.Count(p => p.Aircraft.Parking.Type == Core.Airports.ParkingType.RampCargo);
        var decor = parked.Count(p => p.Aircraft.Decor);
        var ga = parked.Count(p => p.Aircraft.Airline.Length == 0);
        var airlines = parked.Where(p => p.Aircraft.Airline.Length > 0).Select(p => p.Aircraft.Airline).Distinct().Count();
        return Tr.F("{0} listo: {1} aviones estacionados ({2} en puertas, {3} de carga, {4} remotos y {5} de aviación general) ", ap.Icao, parked.Count, gates, cargo, decor - ga, ga)
               + Tr.F("de {0} aerolíneas. Salidas y llegadas al {1}% del horario real; hasta {2} en el aire a la vez. ", airlines, EffectiveDensity, AirMax)
               + Tr.T("Los horarios y planes de vuelo de las salidas se terminan de armar en los próximos minutos.");
    }

    private static bool StandIsGate(Core.Airports.Parking p) => Core.Traffic.ParkedFleetPlanner.Gate(p);

    /// <summary>Diagnóstico V-43: escribir las velocidades de los IA en el aire (comportamiento anterior) o no.</summary>
    public bool AirborneVelocities
    {
        get => _bridge.AirborneVelocities;
        set
        {
            _bridge.AirborneVelocities = value;
            OnPropertyChanged();
            Log(Tr.F("Diagnóstico: IA en vuelo {0} velocidades escritas.", (value ? Tr.T("con") : Tr.T("sin"))));
        }
    }

    /// <summary>"Iniciar tráfico": enciende el tráfico automático y lo arranca en el aeropuerto activo.</summary>
    [RelayCommand]
    private async Task StartTrafficAsync()
    {
        _trafficStarted = true;
        AutoTraffic = true;
        OnPropertyChanged(nameof(ShowStartTraffic));
        OpenLoadingModal(Tr.T("Iniciando el tráfico"));
        try
        {
            if (_engine is null || _matcher is null)
                await LoadAsync();
            else
            {
                Stage(Tr.T("Iniciando tráfico…"), 40);
                await StartWorldTrafficAsync();
                Stage(Tr.T("Tráfico listo"), 100);
            }
            FinishLoadingModal(LoadingSummaryText());
        }
        catch (Exception ex)
        {
            FinishLoadingModal(Tr.F("No se pudo iniciar el tráfico: {0}", ex.Message));
        }
    }
}
