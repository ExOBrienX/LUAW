using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core.Airports;
using SimATC.Core.Comms;
using SimATC.Core.Plan;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una fila de la tabla de puntos del plan.</summary>
public sealed record PlanFixRow(string Ident, string Airway, string Altitude, bool IsSidStar);

/// <summary>Panel Plan de vuelo (hito 4b): SimBrief o manual, resumen, avisos y ruta en el radar (RF-29).</summary>
public sealed partial class MainWindowViewModel
{
    // Uno solo para toda la app (recomendación de .NET); 15 s y basta.
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LUAW/0.4");
        return http;
    }

    [ObservableProperty]
    public partial FlightPlan? Plan { get; set; }

    [ObservableProperty]
    public partial string PlanStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool PlanLoading { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DiagItem> PlanSummary { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> PlanWarnings { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<PlanFixRow> PlanFixes { get; set; } = [];

    public bool HasPlan => Plan is not null;
    public bool HasPlanWarnings => PlanWarnings.Count > 0;
    public bool HasPlanFixes => PlanFixes.Count > 0;
    public string PlanTitle => Plan is { } p ? $"{p.Callsign} · {p.Origin.Icao} → {p.Destination.Icao}" : Tr.T("Sin plan");

    public ObservableCollection<DiagRow> PlanSummaryRows { get; } = [];

    partial void OnPlanSummaryChanged(IReadOnlyList<DiagItem> value) => DiagRow.Sync(PlanSummaryRows, value);

    partial void OnPlanWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasPlanWarnings));

    partial void OnPlanFixesChanged(IReadOnlyList<PlanFixRow> value) => OnPropertyChanged(nameof(HasPlanFixes));

    /// <summary>El plan como se cargó, sin las pistas que el piloto eligió después en las pestañas (ADR-140).</summary>
    private FlightPlan? _loadedPlan;

    partial void OnPlanChanged(FlightPlan? value)
    {
        if (!_pickingRunway)
            _loadedPlan = value;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanTitle));
        UpdatePlanViews();
        UpdateBriefing();
    }

    private bool _revertingPlan;

    /// <summary>
    /// Plan nuevo según en qué va el vuelo (ADR-137). Sin autorización todavía: sesión nueva, como siempre. Ya autorizado,
    /// rodando o en vuelo, con el mismo vuelo: la sesión sigue y toma el plan nuevo (si cambia la pista de salida, Ground la
    /// avisa). Otro destino en vuelo, u otro vuelo con la autorización ya dada: se pregunta antes (si no, se vuelve al plan
    /// anterior). Antes, cargar el plan rodando reiniciaba la conversación y en vuelo quedaban dos planes mezclados.
    /// </summary>
    partial void OnPlanChanged(FlightPlan? oldValue, FlightPlan? newValue)
    {
        if (_planEditKeepsSession || _revertingPlan)
            return;
        if (_session is not { } s || oldValue is null || newValue is null || s.Phase < Core.Comms.PilotPhase.ClearanceGiven)
        {
            RebuildSession();
            return;
        }
        if (Core.Comms.PilotSession.SameFlight(s.FlightPlan, newValue))
        {
            s.ReplacePlan(newValue);
            Log(Tr.F("Plan actualizado ({0} {1}-{2}): el mismo vuelo, la conversación con el ATC sigue.", newValue.Callsign, newValue.Origin.Icao, newValue.Destination.Icao));
            _arrival = null;
            UpdateCommsView();
            return;
        }
        var inFlight = s.Phase >= Core.Comms.PilotPhase.TakeoffCleared;
        var onlyDestination = inFlight && string.Equals(s.FlightPlan.Callsign, newValue.Callsign, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(s.FlightPlan.Origin.Icao, newValue.Origin.Icao, StringComparison.OrdinalIgnoreCase);
        Notice = onlyDestination
            ? new NoticeDialog(Tr.F("¿Cambiar el destino a {0}?", newValue.Destination.Icao),
                Tr.F("Estás en vuelo hacia {0}. Con el plan nuevo, el ATC te lleva a {1} ", s.FlightPlan.Destination.Icao, newValue.Destination.Icao)
                + Tr.T("(llegada, aproximación y pista del destino nuevo). La conversación sigue donde está."),
                Tr.T("Cambiar destino"), () =>
                {
                    s.ReplacePlan(newValue);
                    _arrival = null;
                    Log(Tr.F("Destino cambiado en vuelo: {0} → {1}.", oldValue.Destination.Icao, newValue.Destination.Icao));
                    UpdateCommsView();
                }, Tr.T("Mantener el plan anterior"))
            {
                OnSecondary = () => RevertPlan(oldValue),
            }
            : new NoticeDialog(Tr.T("¿Empezar un vuelo nuevo?"),
                Tr.F("El plan nuevo ({0} {1} → {2}) es otro vuelo. Si lo usas, la ", newValue.Callsign, newValue.Origin.Icao, newValue.Destination.Icao)
                + Tr.F("conversación con el ATC empieza de nuevo{0}.", (inFlight ? Tr.T(" aunque estés en vuelo") : Tr.T(" y pierdes la autorización"))),
                Tr.T("Empezar de nuevo"), RebuildSession, Tr.T("Mantener el plan anterior"))
            {
                OnSecondary = () => RevertPlan(oldValue),
            };
    }

    /// <summary>Vuelve al plan anterior sin tocar la sesión (el piloto no aceptó el cambio).</summary>
    private void RevertPlan(FlightPlan previous)
    {
        _revertingPlan = true;
        try
        {
            Plan = previous;
        }
        finally
        {
            _revertingPlan = false;
        }
        _ = RefreshProceduresAsync();
        Log(Tr.F("Se mantiene el plan anterior ({0} {1}-{2}).", previous.Callsign, previous.Origin.Icao, previous.Destination.Icao));
    }

    partial void OnAirportChanged(AirportData? value)
    {
        UpdatePlanViews();
        if (value is not null)
            _ = RefreshProceduresAsync();
    }

    // --- Plan manual ---

    [ObservableProperty]
    public partial string ManualCallsign { get; set; } = "";

    [ObservableProperty]
    public partial string ManualType { get; set; } = "";

    [ObservableProperty]
    public partial string ManualOrigin { get; set; } = "";

    [ObservableProperty]
    public partial string ManualDestination { get; set; } = "";

    [ObservableProperty]
    public partial string ManualCruise { get; set; } = "";

    [ObservableProperty]
    public partial string ManualRoute { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> ManualErrors { get; set; } = [];

    private bool CanLoadPlan() => !PlanLoading;

    [RelayCommand(CanExecute = nameof(CanLoadPlan))]
    private async Task LoadSimBriefAsync()
    {
        PlanLoading = true;
        LoadSimBriefCommand.NotifyCanExecuteChanged();
        PlanStatus = Tr.T("Pidiendo el último plan a SimBrief…");
        try
        {
            var plan = await new SimBriefClient(Http).FetchLatestAsync(SimBriefUser, CancellationToken.None);
            SetPlan(plan);
            PlanStatus = Tr.F("Plan cargado de SimBrief ({0} puntos).", plan.Fixes.Count);
        }
        catch (SimBriefException ex)
        {
            PlanStatus = ex.Message;
        }
        finally
        {
            PlanLoading = false;
            LoadSimBriefCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CreateManualPlan()
    {
        var (plan, errors) = ManualPlan.Create(ManualCallsign, ManualType, ManualOrigin, ManualDestination, ManualCruise, ManualRoute);
        ManualErrors = errors;
        if (plan is null)
            return;
        SetPlan(plan);
        PlanStatus = Tr.T("Plan manual cargado (la ruta no se dibuja: no tiene coordenadas).");
    }

    [RelayCommand]
    private void ClearPlan()
    {
        _useAtcDeparture = false;
        // Vuelo cerrado a propósito: no queda nada que retomar.
        _resumeRunway = null;
        _pendingResume = null;
        _resumeKey = null;
        Core.Comms.FlightResume.Delete(Core.Comms.FlightResume.DefaultPath);
        Plan = null;
        ClearProcedures("");
        PlanStatus = Tr.T("Plan quitado.");
    }

    /// <summary>Lo pide la vista (botón "Ver ruta"): el radar encuadra la ruta.</summary>
    public event Action? FitPlanRequested;

    [RelayCommand]
    private void ShowPlanOnRadar()
    {
        ShowPlan = true;
        Panel = "radar";
        FitPlanRequested?.Invoke();
    }

    private void SetPlan(FlightPlan plan)
    {
        _useAtcDeparture = false; // plan nuevo: de nuevo la del plan hasta que elija la de LUAW
        Plan = plan;
        _ = RefreshProceduresAsync();
        Log(Tr.F("Plan {0} {1}-{2} ({3}).", plan.Callsign, plan.Origin.Icao, plan.Destination.Icao, (plan.Source == PlanSource.SimBrief ? "SimBrief" : "manual")));
    }

    /// <summary>
    /// Al abrir no hay plan: siempre se asigna (SimBrief o manual), nunca queda el del vuelo anterior (Marco, 2026-10-31).
    /// Se borra el archivo que guardaban las versiones anteriores.
    /// </summary>
    private void InitPlan()
    {
        try
        {
            File.Delete(Path.Combine(Core.AppPaths.Root, "plan.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Tr.F("No se pudo borrar el plan de la versión anterior ({0}).", ex.Message));
        }
        PlanStatus = Tr.T("Sin plan: cárgalo de SimBrief o créalo a mano.");
    }

    private void UpdatePlanViews()
    {
        if (Plan is not { } p)
        {
            PlanSummary = [];
            PlanWarnings = [];
            PlanFixes = [];
            PlanRoute = null;
            RefreshRunwayPick();
            return;
        }
        RefreshRunwayPick();
        var generated = p.GeneratedUtc is { } g ? g.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) + " Z" : null;
        var items = new List<DiagItem>
        {
            new(Tr.T("Indicativo"), p.Callsign),
            new(Tr.T("Avión"), p.Registration is { } reg ? $"{p.AircraftIcao} · {reg}" : p.AircraftIcao),
            new(Tr.T("Origen"), AirportLine(p.Origin)),
            new(Tr.T("Destino"), AirportLine(p.Destination)),
        };
        if (p.Alternate is { } altn)
            items.Add(new(Tr.T("Alterno"), AirportLine(altn)));
        items.Add(new(Tr.T("Crucero"), p.CruiseAltitudeFt > 0 ? Tr.F("FL{0:000} ({1:N0} ft)", p.CruiseAltitudeFt / 100, p.CruiseAltitudeFt) : "—"));
        items.Add(new("SID", Procedure(p.SidIdent, p.SidTransition)));
        items.Add(new("STAR", Procedure(p.StarIdent, p.StarTransition)));
        // Lo que asignó el ATC o eligió el piloto, si difiere del plan (ADR-114).
        if (_session?.Clearance is { } cl)
            items.Add(new(Tr.T("Salida autorizada"), Tr.F("pista {0}{1} (ATC)", cl.Runway, (cl.Sid is { } cs ? $" · {cs}" : ""))));
        else if (_useAtcDeparture && RecommendedDeparture() is { } rd)
            items.Add(new(Tr.T("Salida elegida"), Tr.F("pista {0}{1} (LUAW)", rd.Runway, (rd.Sid is { } rs ? $" · {rs}" : ""))));
        if (_session is { Phase: >= PilotPhase.EnRoute and < PilotPhase.Arrived } && _arrival is { } arr)
            items.Add(new(Tr.T("Llegada asignada"), $"pista {arr.Runway}{(arr.StarName is { } sn ? $" · {sn}" : "")}"
                                              + $"{(arr.ApproachName is { } an ? $" · {an}" : "")}{(arr.Transition is { } tr ? $" vía {tr}" : "")} (ATC)"));
        items.Add(new(Tr.T("Ruta"), p.Route.Length > 0 ? p.Route : "—"));
        if (p.ScheduledOffBlockUtc is { } off)
            items.Add(new(Tr.T("Salida programada"), off.ToString("HH:mm", CultureInfo.InvariantCulture) + " Z"));
        items.Add(new(Tr.T("Fuente"), p.Source == PlanSource.SimBrief
            ? $"SimBrief{(p.Airac is { } airac ? $" · AIRAC {airac}" : "")}{(generated is null ? "" : $" · generado {generated}")}"
            : "manual"));
        PlanSummary = items;
        var warnings = PlanChecks.Warnings(p, Airport?.Icao, DateTime.UtcNow, Weather?.MagVarDeg ?? 0).ToList();
        // La pista de salida la asigna el ATC (en SCEL, modo segregado del AIP: 17L llegadas, 17R salidas); SimBrief no lo sabe.
        if (p.Origin.PlannedRunway is { } planned && Airport?.Icao == p.Origin.Icao && PilotDepartureEnds is { Count: > 0 } deps
            && !deps.Contains(planned, StringComparer.OrdinalIgnoreCase))
            warnings.Add(Tr.F("El plan sale por la {0}, pero en {1} se sale por la {2} (pista en uso y AIP). ", planned, p.Origin.Icao, string.Join("/", deps)) +
                         Tr.T("El ATC te la dará en la autorización: ajusta la SID y la pista en el FMS."));
        PlanWarnings = warnings;
        PlanFixes = [.. p.Fixes.Where(f => !f.IsPseudo).Select(f => new PlanFixRow(f.Ident, f.Airway ?? "",
            f.AltitudeFt > 0 ? $"{f.AltitudeFt:N0} ft" : "", f.IsSidStar))];

        // Radar: del origen por los fijos reales (sin TOC/TOD) hasta el destino; sin coordenadas no hay línea.
        var route = new List<PlanPoint>();
        if (p.Origin.Position is { } o)
            route.Add(new PlanPoint(o, ""));
        foreach (var f in p.Fixes)
        {
            if (!f.IsPseudo && f.Ident != p.Destination.Icao)
                route.Add(new PlanPoint(f.Position, f.Ident));
        }
        if (p.Destination.Position is { } d)
            route.Add(new PlanPoint(d, p.Destination.Icao));
        PlanRoute = route.Count > 1 ? route : null;
    }

    private static string AirportLine(PlanAirport a) =>
        $"{a.Icao}{(a.Name.Length > 0 ? $" · {a.Name}" : "")}{(a.PlannedRunway is { } rwy ? $" · pista {rwy}" : "")}";

    private static string Procedure(string? ident, string? transition) =>
        ident is null ? "—" : transition is null ? ident : Tr.F("{0} (transición {1})", ident, transition);
}
