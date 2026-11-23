using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Traffic;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Nivel de tráfico (ADR-109, pedido de Marco 2026-11-09): Bajo, Medio, Alto, Promedio (según el aeropuerto) o
/// Personalizado. Decide cuántos aviones se estacionan en cada tipo de puesto, cuántos vuelan cerca y con qué frecuencia
/// salen y llegan. Lo técnico (máximos y radio de carga) queda en "Avanzado".
/// </summary>
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<Choice<TrafficLevel>> TrafficLevelChoices { get; } =
    [
        new(TrafficLevel.Average, Tr.T("Promedio (según el aeropuerto)")),
        new(TrafficLevel.Low, Tr.T("Bajo")),
        new(TrafficLevel.Medium, Tr.T("Medio")),
        new(TrafficLevel.High, Tr.T("Alto")),
        new(TrafficLevel.Custom, Tr.T("Personalizado")),
    ];

    public Choice<TrafficLevel> SelectedTrafficLevel
    {
        get => TrafficLevelChoices.FirstOrDefault(c => c.Value == _settings.TrafficLevel) ?? TrafficLevelChoices[0];
        set
        {
            Change(_settings with { TrafficLevel = value.Value }, nameof(SelectedTrafficLevel), nameof(IsCustomTraffic), nameof(TrafficLevelHelp),
                nameof(TrafficLevelSummary));
            ApplyTrafficLimits();
        }
    }

    public bool IsCustomTraffic => _settings.TrafficLevel == TrafficLevel.Custom;

    /// <summary>Personalizado: cuántos aviones de aerolínea estacionar (se llena hasta donde quepa).</summary>
    public decimal CustomParked
    {
        get => _settings.CustomParked;
        set => Change(_settings with { CustomParked = (int)Math.Clamp(value, 0, TrafficPreset.MaxParkedEver) }, nameof(CustomParked), nameof(TrafficLevelSummary));
    }

    /// <summary>Los porcentajes y topes del nivel elegido.</summary>
    private TrafficPreset Preset => TrafficPreset.For(_settings.TrafficLevel, _settings.CustomParked, _settings.TrafficDensity, _settings.MaxAir);

    /// <summary>Salidas y llegadas, en % del horario real: la del nivel (en Personalizado, la de Avanzado).</summary>
    private int EffectiveDensity => Preset.DensityPercent;

    /// <summary>Los máximos de inyectados: en el aire, el del nivel; en tierra (rodando), el de Avanzado.</summary>
    private TrafficLimits EffectiveLimits => new(_settings.MaxGround, AirMax, _settings.MaxGround + AirMax);

    /// <summary>
    /// Máximo en el aire: el del nivel; en Promedio, según el movimiento real del aeropuerto a esta hora (ADR-110): unas 0,8
    /// veces sus llegadas por hora más 4 (salidas subiendo y vuelos de paso), entre 8 y 25. SCEL a mediodía (~16/h): ~17.
    /// </summary>
    private int AirMax => _settings.TrafficLevel == TrafficLevel.Average && Airport is { } ap && _director is { } d
        ? Math.Clamp((int)Math.Round(d.ArrivalsPerHour(ap, 100) * 0.8) + 4, 8, 25)
        : Preset.MaxAir;

    /// <summary>Del refresco del tráfico: el máximo en el aire de Promedio sigue la hora del aeropuerto.</summary>
    private void KeepTrafficLimits()
    {
        if (_engine is { } e && e.Limits != EffectiveLimits)
            e.Limits = EffectiveLimits;
    }

    private void ApplyTrafficLimits()
    {
        if (_engine is { } e)
            e.Limits = EffectiveLimits;
        OnPropertyChanged(nameof(TrafficLevelSummary));
    }

    public string TrafficLevelHelp => _settings.TrafficLevel switch
    {
        TrafficLevel.Low => Tr.T("Bajo: pocas puertas ocupadas (~25%), hasta 8 en el aire y la mitad de las salidas y llegadas del horario real. Para PC justos."),
        TrafficLevel.Medium => Tr.T("Medio (optimizado): un poco menos de la mitad de las puertas ocupadas, hasta 15 en el aire y el horario real. Buen equilibrio entre vida y FPS."),
        TrafficLevel.High => Tr.T("Alto: la mayoría de las puertas ocupadas (~70%), hasta 25 en el aire y 1,5 veces el horario real. Para PC potentes: pide más memoria de video."),
        TrafficLevel.Custom => Tr.T("Personalizado: tú eliges cuántos aviones estacionar (se llena hasta donde quepa) y, en Avanzado, la frecuencia y el máximo en el aire."),
        _ => Tr.T("Promedio (lo normal, recomendado): el aeropuerto como es. Cada tipo de puesto con su ocupación típica (fuselaje ancho en puertas grandes, carga en sus plataformas, remotos \"de noche\" y aviación general de decoración), el horario y la densidad reales, y el máximo en el aire según el movimiento del aeropuerto a esta hora."),
    };

    /// <summary>Puestos que se dejan libres: las llegadas de la próxima hora, el del piloto y un margen (ADR-109).</summary>
    private int KeepFree(AirportData airport) =>
        (int)Math.Ceiling(_director?.ArrivalsPerHour(airport, EffectiveDensity) ?? 2) + TrafficDirector.MarginFor(airport)
        + (_pilotStandIcao == airport.Icao && _pilotStand is not null ? 1 : 0);

    /// <summary>"SCEL: 64 puestos de aerolínea… → ~35 estacionados…": qué hará el nivel en el aeropuerto actual.</summary>
    public string TrafficLevelSummary
    {
        get
        {
            if (Airport is not { } ap)
                return Tr.T("Carga un aeropuerto para ver cuántos aviones pondrá el nivel elegido.");
            var c = StandCensus.Of(ap);
            int N(StandCategory k) => c.GetValueOrDefault(k);
            var airlineStands = N(StandCategory.GateHeavy) + N(StandCategory.GateMedium) + N(StandCategory.GateSmall) + N(StandCategory.Cargo) + N(StandCategory.Remote);
            var keep = KeepFree(ap);
            var (airline, ga) = StandCensus.Targets(ap, Preset, keep);
            var gaNote = GaModels().Count == 0 && N(StandCategory.GeneralAviation) > 0 ? Tr.T(" (sin modelos de aviación general: quedan vacías)") : "";
            return Tr.F("{0}: {1} puestos de aerolínea ({2} puertas grandes, {3} medianas, ", ap.Icao, airlineStands, N(StandCategory.GateHeavy), N(StandCategory.GateMedium))
                   + Tr.F("{0} chicas, {1} de carga, {2} remotos) y {3} de aviación general.\n", N(StandCategory.GateSmall), N(StandCategory.Cargo), N(StandCategory.Remote), N(StandCategory.GeneralAviation))
                   + Tr.F("Se estacionan ~{0} aviones de aerolínea y ~{1} de aviación general{2}; quedan al menos {3} puestos libres para las llegadas. ", airline, ga, gaNote, keep)
                   + Tr.F("Hasta {0} en el aire a la vez dentro del radio de carga; salidas y llegadas al {1}% del horario real.", AirMax, Preset.DensityPercent);
        }
    }

    private IReadOnlyList<GaDecorModel>? _gaModels;
    private ModelMatcher? _gaModelsFor;

    /// <summary>La decoración de aviación general que el simulador tiene (se lee una vez por catálogo).</summary>
    private IReadOnlyList<GaDecorModel> GaModels()
    {
        if (_matcher is not { } m)
            return [];
        if (!ReferenceEquals(_gaModelsFor, m))
        {
            _gaModels = GaDecor.Load(GaDecor.PathFor(_dataDir), m.SimTitles.Count > 0 ? m.SimTitles : null);
            _gaModelsFor = m;
            Log(Tr.F("Aviación general de decoración: {0} modelos del simulador.", _gaModels.Count));
        }
        return _gaModels ?? [];
    }

    /// <summary>Ocupación por tipo de puesto: "Puertas grandes 6/12 · medianas 20/34 · …".</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string ParkedBreakdown { get; private set; } = "";

    /// <summary>Las aerolíneas estacionadas, de más a menos.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string AirlineBreakdown { get; private set; } = "";

    private void UpdateBreakdowns(IReadOnlyList<InjectedParked> parked)
    {
        if (Airport is not { } ap)
        {
            ParkedBreakdown = AirlineBreakdown = "";
            return;
        }
        var census = StandCensus.Of(ap);
        var used = new Dictionary<StandCategory, int>();
        foreach (var p in parked)
        {
            var c = StandCensus.CategoryOf(p.Aircraft.Parking);
            used[c] = used.GetValueOrDefault(c) + 1;
        }
        string Part(StandCategory c, string name) => $"{name} {used.GetValueOrDefault(c)}/{census.GetValueOrDefault(c)}";
        ParkedBreakdown = Tr.F("Ocupados: {0} · {1} · ", Part(StandCategory.GateHeavy, Tr.T("puertas grandes")), Part(StandCategory.GateMedium, Tr.T("medianas")))
                          + $"{Part(StandCategory.GateSmall, Tr.T("chicas"))} · {Part(StandCategory.Cargo, Tr.T("carga"))} · {Part(StandCategory.Remote, Tr.T("remotos"))} · "
                          + $"{Part(StandCategory.GeneralAviation, Tr.T("aviación general"))}.";
        var airlines = parked.Where(p => p.Aircraft.Airline.Length > 0)
            .GroupBy(p => p.Aircraft.Airline)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => $"{g.Key} {g.Count()}");
        AirlineBreakdown = parked.Count == 0 ? Tr.T("Sin estacionados.") : Tr.T("Aerolíneas: ") + string.Join(" · ", airlines);
    }

    /// <summary>"Rellenar ahora": reemplaza los estacionados según el nivel elegido.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTraffic))]
    private async Task FillNowAsync()
    {
        if (Airport is not { } airport)
            return;
        SetBusy(true);
        OpenLoadingModal(Tr.F("Rellenando {0}", airport.Icao));
        try
        {
            await FillAirportAsync(airport);
            FinishLoadingModal(LoadingSummaryText());
        }
        catch (Exception ex)
        {
            FinishLoadingModal(Tr.F("No se pudo rellenar: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Llena el aeropuerto según el nivel (ADR-109).</summary>
    private async Task FillAirportAsync(AirportData airport)
    {
        if (_matcher is null || _routes is null)
            return;
        TrafficStatus = Tr.F("Estacionando aviones en {0}…", airport.Icao);
        // Los puestos de las salidas en curso, los reservados para llegadas y los sin camino a las pistas no se usan.
        var busy = _flights.Values.Select(f => f.Aircraft.ParkingIndex).ToHashSet();
        if (_director is { } d)
            busy.UnionWith(d.ReservedStands);
        if (_graph is { } g)
            busy.UnionWith(Enumerable.Range(0, airport.Parkings.Count).Where(i => !g.StandReachable(i)));
        try
        {
            var keep = KeepFree(airport);
            var (airlineTarget, gaTarget) = StandCensus.Targets(airport, Preset, keep);
            ModalLine(Tr.F("Eligiendo aviones: ~{0} de aerolínea (puertas, carga y remotos) y ~{1} de aviación general; ", airlineTarget, gaTarget)
                      + Tr.F("{0} puestos quedan libres para las llegadas.", keep));
            var created = await _parked.PopulateLevelAsync(_bridge, airport, _routes, _matcher, Preset, keep,
                UserState?.Position, Log, CancellationToken.None, busy, GaModels(),
                progress: (i, n, p) => Dispatcher.UIThread.Post(() => ModalDetail(
                    Tr.F("Estacionando {0} de {1}: {2}{3} en {4}", i, n, (p.Airline.Length > 0 ? p.Airline + " " : ""), p.Type, Radar.RadarView.StandName(p.Parking))
                    + (p.Decor ? (p.Airline.Length > 0 ? Tr.T(" (remoto, no sale)") : Tr.T(" (aviación general)")) : "")
                    + $" · {p.Match.Model.Title}", 60 + 28.0 * i / Math.Max(1, n))));
            ModalLine(Tr.F("{0} aviones estacionados.", created));
            RefreshTraffic();
            TrafficStatus = Tr.F("{0} aviones estacionados en {1}.", created, airport.Icao);
        }
        catch (Exception ex)
        {
            TrafficStatus = Tr.F("Error al poblar: {0}", ex.Message);
        }
    }
}
