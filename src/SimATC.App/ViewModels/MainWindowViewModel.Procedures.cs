using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Radar;
using SimATC.Core.Airports;
using SimATC.Core.Comms;
using SimATC.Core.Plan;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Procedimientos del simulador para el plan (referencia en el radar y en la vista previa): la SID, la STAR y la
/// aproximación recomendadas, con el porqué de cada una y de las demás. El piloto las selecciona a mano en su avión.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private AirportData? _originData;
    private AirportData? _destData;
    private int _procedureVersion;

    [ObservableProperty]
    public partial IReadOnlyList<Choice<ProcedureAdvice?>> SidChoices { get; set; } = [];

    [ObservableProperty]
    public partial Choice<ProcedureAdvice?>? SelectedSid { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Choice<ProcedureAdvice?>> StarChoices { get; set; } = [];

    [ObservableProperty]
    public partial Choice<ProcedureAdvice?>? SelectedStar { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Choice<ProcedureAdvice?>> ApproachChoices { get; set; } = [];

    [ObservableProperty]
    public partial Choice<ProcedureAdvice?>? SelectedApproach { get; set; }

    /// <summary>Transiciones de ruta de la SID elegida (la de SimBrief preseleccionada): por dónde sale a la ruta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidTransitions))]
    public partial IReadOnlyList<string> SidTransitions { get; set; } = [];

    [ObservableProperty]
    public partial string? SelectedSidTransition { get; set; }

    public bool HasSidTransitions => SidTransitions.Count > 0;

    /// <summary>Transiciones de ruta de la STAR elegida (la de SimBrief preseleccionada): por dónde entra desde la ruta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStarTransitions))]
    public partial IReadOnlyList<string> StarTransitions { get; set; } = [];

    [ObservableProperty]
    public partial string? SelectedStarTransition { get; set; }

    public bool HasStarTransitions => StarTransitions.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<string> ApproachTransitions { get; set; } = [];

    [ObservableProperty]
    public partial string? SelectedApproachTransition { get; set; }

    [ObservableProperty]
    public partial string ProcedureStatus { get; set; } = "";

    [ObservableProperty]
    public partial string RunwayNote { get; set; } = "";

    // Por qué la elegida y qué tienen las demás.
    [ObservableProperty]
    public partial IReadOnlyList<string> SidWhy { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> SidOthers { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> StarWhy { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> StarOthers { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> ApproachWhy { get; set; } = [];

    // Tablas de tramos.
    [ObservableProperty]
    public partial IReadOnlyList<LegRow> SidLegs { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LegRow> StarLegs { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LegRow> ApproachLegs { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ProcedureLine>? ProcedureLines { get; set; }

    /// <summary>Destino y alterno para el radar (pistas y calles).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AirportData>? PlanAirports { get; set; }

    public bool HasProcedures => SidChoices.Count > 0 || StarChoices.Count > 0 || ApproachChoices.Count > 0;

    partial void OnSelectedSidChanged(Choice<ProcedureAdvice?>? value)
    {
        (SidWhy, SidOthers) = Explain(value?.Value, SidChoices);
        SidTransitions = value?.Value?.Procedure is { } sid ? [.. sid.EnrouteTransitions.Select(t => t.Name).Where(n => n.Length > 0).Distinct()] : [];
        SelectedSidTransition = PickTransition(SidTransitions, Plan?.SidTransition);
        BuildProcedureLines();
    }

    partial void OnSelectedSidTransitionChanged(string? value) => BuildProcedureLines();

    partial void OnSelectedStarTransitionChanged(string? value) => BuildProcedureLines();

    /// <summary>La transición del plan si existe en la lista; si no, ninguna (el plan se une en el primer fijo común).</summary>
    private static string? PickTransition(IReadOnlyList<string> options, string? planned) =>
        planned is { Length: > 0 } && options.FirstOrDefault(o => string.Equals(o, planned, StringComparison.OrdinalIgnoreCase)) is { } match
            ? match : null;

    partial void OnSelectedStarChanged(Choice<ProcedureAdvice?>? value)
    {
        (StarWhy, StarOthers) = Explain(value?.Value, StarChoices);
        StarTransitions = value?.Value?.Procedure is { } star ? [.. star.EnrouteTransitions.Select(t => t.Name).Where(n => n.Length > 0).Distinct()] : [];
        SelectedStarTransition = PickTransition(StarTransitions, Plan?.StarTransition);
        BuildProcedureLines();
    }

    partial void OnSelectedApproachChanged(Choice<ProcedureAdvice?>? value)
    {
        ApproachTransitions = value?.Value?.Approach is { } a ? [.. a.Transitions.Select(t => t.Name)] : [];
        SelectedApproachTransition = value?.Value?.Transition ?? ApproachTransitions.FirstOrDefault();
        (ApproachWhy, _) = Explain(value?.Value, ApproachChoices);
        BuildProcedureLines();
    }

    partial void OnSelectedApproachTransitionChanged(string? value) => BuildProcedureLines();

    [RelayCommand]
    private Task ReloadProcedures() => RefreshProceduresAsync();

    /// <summary>Lee (de la caché o del simulador) origen, destino y alterno, y evalúa los procedimientos.</summary>
    private async Task RefreshProceduresAsync()
    {
        var version = ++_procedureVersion;
        if (Plan is not { } p)
        {
            ClearProcedures("");
            return;
        }
        if (!_bridge.IsConnected)
        {
            ClearProcedures(Tr.T("Conecta MSFS para buscar los procedimientos en los datos del simulador."));
            return;
        }
        try
        {
            ProcedureStatus = Tr.F("Leyendo {0} y {1} del simulador…", p.Origin.Icao, p.Destination.Icao);
            var origin = await AirportFor(p.Origin.Icao);
            var dest = p.Destination.Icao == p.Origin.Icao ? origin : await AirportFor(p.Destination.Icao);
            AirportData? alternate = null;
            if (p.Alternate is { } altn && altn.Icao != p.Destination.Icao)
            {
                try
                {
                    alternate = await AirportFor(altn.Icao);
                }
                catch (Exception ex)
                {
                    Log(Tr.F("No se pudo leer el alterno {0} ({1}).", altn.Icao, ex.Message));
                }
            }
            if (version != _procedureVersion)
                return; // llegó otro plan mientras tanto
            _originData = origin;
            _destData = dest;
            if (dest.Icao != origin.Icao)
                _ = PrepareArrivalAsync(dest);
            PlanAirports = alternate is null ? [dest] : [dest, alternate];

            (SidChoices, SelectedSid) = Choices(ProcedureAdvisor.Sids(origin, p), origin.Departures);
            (StarChoices, SelectedStar) = Choices(ProcedureAdvisor.Stars(dest, p), dest.Arrivals);
            OnPropertyChanged(nameof(HasProcedures));
            RunwayNote = Tr.F("Pistas: despegue {0} y aterrizaje {1}, las del plan. ", p.Origin.PlannedRunway ?? "—", p.Destination.PlannedRunway ?? "—") +
                         Tr.T("Puedes cambiarlas en Salida y Llegada: el punto verde marca las que están en uso según el viento.");
            _runwayPickKey = null;
            RefreshRunwayPick();
            ProcedureStatus = Tr.F("Del simulador: {0} SID en {1}, {2} STAR y ", origin.Departures.Count, origin.Icao, dest.Arrivals.Count) +
                              Tr.F("{0} aproximaciones en {1}.", dest.Approaches.Count, dest.Icao);
        }
        catch (Exception ex)
        {
            ClearProcedures(Tr.F("No se pudieron leer los procedimientos: {0}", ex.Message));
        }
    }

    private async Task<AirportData> AirportFor(string icao)
    {
        if (Airport is { } loaded && loaded.Icao == icao)
            return loaded;
        return (await _airports.GetFreshAsync(icao, CancellationToken.None)).Airport;
    }

    /// <summary>Opciones: las evaluadas (la recomendada primero) y al final el resto, por si el piloto quiere otra.</summary>
    private static (IReadOnlyList<Choice<ProcedureAdvice?>>, Choice<ProcedureAdvice?>?) Choices(IReadOnlyList<ProcedureAdvice> advice,
        IReadOnlyList<Procedure> all)
    {
        var choices = new List<Choice<ProcedureAdvice?>> { new(null, Tr.T("Ninguna")) };
        choices.AddRange(advice.Select(a => new Choice<ProcedureAdvice?>(a, Label(a))));
        var listed = advice.Select(a => a.Procedure).ToHashSet();
        foreach (var p in all.Where(p => !listed.Contains(p)).OrderBy(p => p.Name))
        {
            var other = new ProcedureAdvice(p.Name, false, [], [Tr.T("no empalma con tu ruta")], [], 0) { Procedure = p };
            choices.Add(new Choice<ProcedureAdvice?>(other, Tr.F("{0} · no empalma con tu ruta", p.Name)));
        }
        var selected = choices.FirstOrDefault(c => c.Value?.Recommended == true) ?? choices[0];
        return (choices, selected);
    }

    private static string Label(ProcedureAdvice a) =>
        a.Recommended ? Tr.F("★ {0} · recomendada", a.Name) : a.Valid ? a.Name : Tr.F("{0} · no sirve para el plan", a.Name);

    /// <summary>Por qué la elegida (✓ a favor, ✗ en contra, • notas) y una línea por cada otra evaluada.</summary>
    private static (IReadOnlyList<string> Why, IReadOnlyList<string> Others) Explain(ProcedureAdvice? chosen,
        IReadOnlyList<Choice<ProcedureAdvice?>> choices)
    {
        if (chosen is null)
            return ([], []);
        var why = new List<string>();
        why.AddRange(chosen.Pros.Select(x => $"✓ {x}"));
        why.AddRange(chosen.Cons.Select(x => $"✗ {x}"));
        why.AddRange(chosen.Notes.Select(x => $"• {x}"));
        if (chosen.LengthNm > 0)
            why.Add(Tr.F("• {0:0} NM de trazado", chosen.LengthNm));
        var others = new List<string>();
        foreach (var c in choices)
        {
            if (c.Value is not { } a || ReferenceEquals(a, chosen) || (a.Pros.Count == 0 && a.Notes.Count == 0 && a.Cons.Count == 1
                    && a.Cons[0] == Tr.T("no empalma con tu ruta")))
                continue;
            var reason = a.Cons.Count > 0 ? $"✗ {a.Cons[0]}" : a.Notes.Count > 0 ? $"• {a.Notes[0]}" : Tr.T("✓ también sirve");
            others.Add($"{a.Name}: {reason}{(a.LengthNm > 0 ? $" ({a.LengthNm:0} NM)" : "")}");
        }
        return (why, others);
    }

    private void ClearProcedures(string status)
    {
        SidChoices = [];
        StarChoices = [];
        ApproachChoices = [];
        SelectedSid = null;
        SelectedStar = null;
        SelectedApproach = null;
        SidLegs = [];
        StarLegs = [];
        ApproachLegs = [];
        ProcedureLines = null;
        PlanAirports = null;
        RunwayNote = "";
        ProcedureStatus = status;
        OnPropertyChanged(nameof(HasProcedures));
    }

    private void BuildProcedureLines()
    {
        if (Plan is not { } p || _originData is not { } origin || _destData is not { } dest)
        {
            ProcedureLines = null;
            return;
        }
        // Las aproximaciones dependen de dónde termina la STAR elegida.
        var lines = new List<ProcedureLine>();
        ProcedureShape? starShape = null;
        // La SID que se vuela (ADR-113): autorizado, la que dio el ATC con su pista; si eligió la de LUAW, esa; si no, la del plan.
        var cleared = _session is { Clearance: { } c, Phase: < PilotPhase.EnRoute } ? c : null;
        var rec = cleared is null ? RecommendedDeparture() : null;
        Core.Airports.Procedure? flown;
        string? flownRunway;
        if (cleared is not null)
            (flown, flownRunway) = (origin.Departures.FirstOrDefault(d => d.Name == cleared.Sid), cleared.Runway);
        else if (_useAtcDeparture && rec is { } chosen)
            (flown, flownRunway) = (origin.Departures.FirstOrDefault(d => d.Name == chosen.Sid), chosen.Runway);
        else
            (flown, flownRunway) = (SelectedSid?.Value?.Procedure, p.Origin.PlannedRunway);
        if (flown is { } sid)
        {
            var shape = ProcedureGeometry.Sid(sid, origin, flownRunway, SelectedSidTransition ?? p.SidTransition);
            lines.Add(new ProcedureLine(ProcedureKind.Sid, sid.Name, shape));
            SidLegs = shape.Legs;
        }
        else
        {
            SidLegs = [];
        }
        // La recomendada por LUAW, punteada en blanco, mientras difiera de la del plan.
        if (ShowDepartureRecommendation && rec is { Sid: { } recSid } r2 && origin.Departures.FirstOrDefault(d => d.Name == recSid) is { } recProc)
            lines.Add(new ProcedureLine(ProcedureKind.SidRecommended, recProc.Name, ProcedureGeometry.Sid(recProc, origin, r2.Runway, p.SidTransition)));
        // En vuelo, la pista de llegada que dio el ATC (ADR-105); antes, la del plan.
        var atcArrival = _session is { Phase: >= PilotPhase.EnRoute and < PilotPhase.Landed } && _arrival is { } arr ? arr : null;
        if (SelectedStar?.Value?.Procedure is { } star)
        {
            starShape = ProcedureGeometry.Star(star, dest, atcArrival?.Runway ?? p.Destination.PlannedRunway, SelectedStarTransition ?? p.StarTransition);
            lines.Add(new ProcedureLine(ProcedureKind.Star, star.Name, starShape));
            StarLegs = starShape.Legs;
        }
        else
        {
            StarLegs = [];
        }
        var starEnd = starShape?.Waypoints.LastOrDefault(w => !w.Approximate)?.Ident;
        if (_approachesFor != starEnd || ApproachChoices.Count == 0)
        {
            _approachesFor = starEnd;
            var advice = ProcedureAdvisor.Approaches(dest, p, starEnd);
            var choices = new List<Choice<ProcedureAdvice?>> { new(null, Tr.T("Ninguna")) };
            choices.AddRange(advice.Select(a => new Choice<ProcedureAdvice?>(a, Label(a))));
            ApproachChoices = choices;
            SelectedApproach = choices.FirstOrDefault(c => c.Value?.Recommended == true) ?? choices[0];
            return; // el cambio de aproximación vuelve a llamar a este método
        }
        // La aproximación que dijo el ATC (no la del plan) y, después de un motor y al aire, su frustrada (ADR-105).
        if ((atcArrival?.Procedure ?? SelectedApproach?.Value?.Approach) is { } app)
        {
            var transition = atcArrival?.Procedure is not null ? atcArrival.Transition : SelectedApproachTransition;
            var shape = ProcedureGeometry.Approach(app, dest, transition, withMissed: _session?.MissedApproachActive == true);
            lines.Add(new ProcedureLine(ProcedureKind.Approach, ProcedureMatch.ApproachName(app), shape));
            ApproachLegs = shape.Legs;
        }
        else
        {
            ApproachLegs = [];
        }
        ProcedureLines = lines.Count > 0 ? lines : null;
    }

    private string? _approachesFor;
    private string? _atcProceduresKey;

    /// <summary>
    /// Del reloj de 1 s: si el ATC cambió la pista, la aproximación o la transición, o hay un motor y al aire, el radar
    /// vuelve a dibujar la llegada (ADR-105: seguía mostrando la ILS Z 06L del plan con la ILS 06R autorizada).
    /// </summary>
    private void RefreshAtcProcedures()
    {
        var key = _session is { Phase: >= PilotPhase.EnRoute and < PilotPhase.Landed } s && _arrival is { } a
            ? $"{a.Runway}|{a.ApproachName}|{a.Transition}|{s.MissedApproachActive}"
            : $"dep|{_session?.Clearance?.Runway}|{_session?.Clearance?.Sid}|{_useAtcDeparture}|{RecommendedDeparture()}";
        if (key != _atcProceduresKey)
        {
            OnPropertyChanged(nameof(ShowDepartureRecommendation));
            OnPropertyChanged(nameof(DepartureRecommendationText));
        }
        if (key == _atcProceduresKey)
            return;
        _atcProceduresKey = key;
        SyncPlanSelections();
        BuildProcedureLines();
        UpdatePlanViews();
    }

    /// <summary>
    /// Las pestañas del plan siguen lo que asignó el ATC o eligió el piloto (ADR-114, pedido de Marco 2026-11-09): la SID
    /// autorizada o la de LUAW, la STAR y la aproximación de la llegada. Así la SID, la llegada, la aproximación, sus tramos
    /// y el mini radar muestran lo mismo que el radar y el ATC. Solo si la opción está en la lista.
    /// </summary>
    private void SyncPlanSelections()
    {
        var sid = _session is { Clearance: { } c, Phase: < PilotPhase.EnRoute } ? c.Sid
            : _useAtcDeparture ? RecommendedDeparture()?.Sid : null;
        if (sid is not null && SelectedSid?.Value?.Procedure?.Name != sid
            && SidChoices.FirstOrDefault(x => x.Value?.Procedure?.Name == sid) is { } sidChoice)
            SelectedSid = sidChoice;
        if (_session is not { Phase: >= PilotPhase.EnRoute and < PilotPhase.Landed } || _arrival is not { } a)
            return;
        if (a.StarName is { } star && SelectedStar?.Value?.Procedure?.Name != star
            && StarChoices.FirstOrDefault(x => x.Value?.Procedure?.Name == star) is { } starChoice)
            SelectedStar = starChoice;
        if (a.Procedure is { } app && !ReferenceEquals(SelectedApproach?.Value?.Approach, app)
            && ApproachChoices.FirstOrDefault(x => x.Value?.Approach is { } o && ProcedureMatch.ApproachName(o) == ProcedureMatch.ApproachName(app)) is { } appChoice)
            SelectedApproach = appChoice;
        if (a.Transition is { } t && SelectedApproachTransition != t && ApproachTransitions.Contains(t))
            SelectedApproachTransition = t;
    }
}
