using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.Core.Engine;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Un tramo de calle o el rectángulo de una pista, en metros locales, para dibujar alrededor del puesto.</summary>
public readonly record struct MapSegment(LocalPoint A, LocalPoint B, double WidthM, bool IsRunway);

/// <summary>
/// Planificador del pushback del piloto (RF-21c). Dos formas de planear: arrastrar con el mouse el avión final hasta
/// donde se quiere dejar y girarlo con la rueda (se calcula la línea con <see cref="PilotPushback.Solve"/>), o ajustar
/// el tramo recto, el lado de la cola, el giro, el radio y el tramo final. La ventana dibuja la línea y cómo queda el
/// avión, en rojo si no se puede. Enter inicia (lo hace la ventana principal con el personal de tierra), Esc cierra o
/// detiene.
/// </summary>
public sealed partial class PushbackViewModel : ObservableObject
{
    private readonly TaxiGraph? _graph;

    public PushbackViewModel(TaxiGraph? graph, LocalPoint start, double headingDeg, IReadOnlyList<MapSegment> map, IReadOnlyList<LocalPoint> stands)
    {
        _graph = graph;
        Start = start;
        HeadingDeg = headingDeg;
        Map = map;
        Stands = stands;
        var d = PilotPushback.Default;
        _suppress = true;
        StraightM = d.StraightM;
        TurnDeg = d.TurnDeg;
        RadiusM = d.RadiusM;
        FinalStraightM = d.FinalStraightM;
        Turn = d.Turn;
        _suppress = false;
        Replan();
    }

    private bool _suppress;

    /// <summary>Distancia (m) alrededor del avión que dibuja el planificador.</summary>
    public const double MapRadiusM = 300;

    /// <summary>El planificador para el avión en <paramref name="position"/>, con las calles, pistas y puestos cercanos.</summary>
    public static PushbackViewModel ForStand(TaxiGraph graph, SimATC.Core.Airports.AirportData airport, GeoPoint position, double headingDeg,
        PushbackParams? initial = null)
    {
        var start = graph.Projection.ToLocal(position);
        var map = new List<MapSegment>();
        foreach (var (a, b, _) in graph.TaxiEdgesNear(start, MapRadiusM))
            map.Add(new MapSegment(graph.Position(a), graph.Position(b), 18, false));
        foreach (var r in airport.Runways)
        {
            var c = graph.Projection.ToLocal(r.Center);
            if (LocalPoint.Distance(c, start) > MapRadiusM + r.LengthM / 2)
                continue;
            map.Add(new MapSegment(Polyline.Offset(c, r.HeadingTrueDeg, r.LengthM / 2), Polyline.Offset(c, r.HeadingTrueDeg + 180, r.LengthM / 2),
                r.WidthM, true));
        }
        var stands = airport.Parkings.Select(p => graph.Projection.ToLocal(p.Position))
            .Where(p => LocalPoint.Distance(p, start) < MapRadiusM).ToList();
        var vm = new PushbackViewModel(graph, start, headingDeg, map, stands);
        if (initial is { } p)
            vm.Load(p);
        return vm;
    }

    /// <summary>Carga unos ajustes (p. ej., los del pushback automático) y vuelve a planear.</summary>
    public void Load(PushbackParams p)
    {
        _suppress = true;
        StraightM = p.StraightM;
        Turn = p.Turn;
        TurnDeg = p.Turn == PushTurn.Straight ? TurnDeg : p.TurnDeg;
        RadiusM = p.RadiusM;
        FinalStraightM = p.FinalStraightM;
        _suppress = false;
        Replan();
    }

    public LocalPoint Start { get; }
    public double HeadingDeg { get; }
    public IReadOnlyList<MapSegment> Map { get; }
    public IReadOnlyList<LocalPoint> Stands { get; }

    [ObservableProperty]
    public partial double StraightM { get; set; }

    [ObservableProperty]
    public partial double TurnDeg { get; set; }

    [ObservableProperty]
    public partial double RadiusM { get; set; }

    [ObservableProperty]
    public partial double FinalStraightM { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TailLeft), nameof(TailRight), nameof(Straight), nameof(TurnEnabled))]
    public partial PushTurn Turn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsOk), nameof(IsWarning), nameof(IsBad), nameof(CanStart), nameof(LengthText),
        nameof(EndHeadingText), nameof(EndCardinalText), nameof(SideText))]
    public partial PushbackPlan Plan { get; private set; } = null!;

    /// <summary>El pushback está en curso (los ajustes quedan bloqueados).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanEdit), nameof(TurnEnabled), nameof(ShowPlanning))]
    public partial bool IsPushing { get; set; }

    /// <summary>Sin pushback en curso ni terminado: se ven los botones de planear e iniciar.</summary>
    public bool ShowPlanning => !IsPushing && !IsDone;

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(ShowPlanning));

    /// <summary>Por qué no se puede iniciar aunque el plan sirva (p. ej., falta la aprobación de Ground).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanStart))]
    public partial string? Blocker { get; set; }

    /// <summary>Lo que va haciendo el personal de tierra, en español.</summary>
    [ObservableProperty]
    public partial string CrewStatus { get; set; } = "";

    /// <summary>Dónde está el avión ahora (durante el pushback), para dibujarlo.</summary>
    [ObservableProperty]
    public partial (LocalPoint Position, double HeadingDeg)? Live { get; set; }

    /// <summary>Avance del pushback en curso (0 a 100), para la barra.</summary>
    [ObservableProperty]
    public partial double PushProgress { get; set; }

    /// <summary>El pushback terminó: la ventana muestra el aviso y se cierra sola (ADR-136).</summary>
    [ObservableProperty]
    public partial bool IsDone { get; set; }

    /// <summary>La ventana tiene que cerrarse (pushback terminado).</summary>
    public event Action? CloseRequested;

    /// <summary>El pushback terminó: aviso y cierre de la ventana unos segundos después.</summary>
    public void Complete(string status)
    {
        IsPushing = false;
        IsDone = true;
        PushProgress = 100;
        CrewStatus = status;
        CloseRequested?.Invoke();
    }

    public string LengthText => $"{Plan.Path.Length:0} m";
    public string EndHeadingText => $"{Plan.EndHeadingDeg:000}°";
    public string EndCardinalText => PilotPushback.Cardinal(Plan.EndHeadingDeg);
    public string SideText => Turn switch { PushTurn.TailLeft => Tr.T("Cola a la izquierda"), PushTurn.TailRight => Tr.T("Cola a la derecha"), _ => Tr.T("Recto") };

    public bool TailLeft => Turn == PushTurn.TailLeft;
    public bool TailRight => Turn == PushTurn.TailRight;
    public bool Straight => Turn == PushTurn.Straight;
    public bool TurnEnabled => Turn != PushTurn.Straight && CanEdit;
    public bool CanEdit => !IsPushing;

    public bool IsBad => !Plan.CanStart;
    public bool IsWarning => Plan.CanStart && !Plan.EndsOnTaxiway;
    public bool IsOk => Plan.CanStart && Plan.EndsOnTaxiway;
    public bool CanStart => Plan.CanStart && Blocker is null && !IsPushing;

    public string StatusText => Plan.CanStart && Blocker is not null ? Blocker : Plan.Message;

    /// <summary>El piloto pidió iniciar el pushback planeado.</summary>
    public event Action<PushbackPlan>? StartRequested;

    /// <summary>El piloto pidió detener el pushback en curso.</summary>
    public event Action? StopRequested;

    partial void OnStraightMChanged(double value) => Replan();
    partial void OnTurnDegChanged(double value) => Replan();
    partial void OnRadiusMChanged(double value) => Replan();
    partial void OnFinalStraightMChanged(double value) => Replan();

    partial void OnTurnChanged(PushTurn value)
    {
        OnPropertyChanged(nameof(TurnEnabled));
        Replan();
    }

    private void Replan()
    {
        if (!_suppress)
            Plan = PilotPushback.Plan(Start, HeadingDeg, new PushbackParams(StraightM, Turn, Turn == PushTurn.Straight ? 0 : TurnDeg, RadiusM, FinalStraightM), _graph);
    }

    /// <summary>
    /// Mouse: el avión final arrastrado a <paramref name="end"/> mirando a <paramref name="endHeadingDeg"/>. Si se alcanza
    /// con el radio actual, los ajustes pasan a ser los de esa línea; si no, se dibuja en rojo y no se puede iniciar.
    /// </summary>
    public void PlaceEnd(LocalPoint end, double endHeadingDeg)
    {
        if (!CanEdit)
            return;
        if (PilotPushback.Solve(Start, HeadingDeg, end, endHeadingDeg, RadiusM) is not { } p)
        {
            Plan = PilotPushback.Unreachable(Start, end, endHeadingDeg);
            return;
        }
        _suppress = true;
        StraightM = Math.Round(p.StraightM, 1);
        Turn = p.Turn;
        if (p.Turn != PushTurn.Straight)
            TurnDeg = Math.Round(p.TurnDeg);
        FinalStraightM = Math.Round(p.FinalStraightM, 1);
        _suppress = false;
        Plan = PilotPushback.Plan(Start, HeadingDeg, p, _graph);
    }

    [RelayCommand]
    private void SetTurn(string side)
    {
        if (CanEdit)
            Turn = side switch { "left" => PushTurn.TailLeft, "right" => PushTurn.TailRight, _ => PushTurn.Straight };
    }

    [RelayCommand]
    private void Reset()
    {
        if (!CanEdit)
            return;
        var d = PilotPushback.Default;
        _suppress = true;
        StraightM = d.StraightM;
        TurnDeg = d.TurnDeg;
        RadiusM = d.RadiusM;
        FinalStraightM = d.FinalStraightM;
        Turn = d.Turn;
        _suppress = false;
        Replan();
    }

    [RelayCommand]
    private void StartPushback()
    {
        if (CanStart)
            StartRequested?.Invoke(Plan);
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke();

    /// <summary>Teclas: ← → giro (5°), ↑ ↓ tramo recto (5 m). Devuelve true si la usó.</summary>
    public bool Nudge(int turnSteps, int straightSteps)
    {
        if (!CanEdit)
            return false;
        if (turnSteps != 0)
        {
            // El giro va de cola a la derecha (negativo) a cola a la izquierda (positivo), pasando por recto.
            var signed = (Turn == PushTurn.TailRight ? -TurnDeg : Turn == PushTurn.TailLeft ? TurnDeg : 0) + 5 * turnSteps;
            signed = Math.Clamp(signed, -PilotPushback.MaxTurnDeg, PilotPushback.MaxTurnDeg);
            Turn = signed > 0 ? PushTurn.TailLeft : signed < 0 ? PushTurn.TailRight : PushTurn.Straight;
            if (signed != 0)
                TurnDeg = Math.Abs(signed);
        }
        if (straightSteps != 0)
            StraightM = Math.Clamp(StraightM + 5 * straightSteps, 0, 80);
        return turnSteps != 0 || straightSteps != 0;
    }
}
