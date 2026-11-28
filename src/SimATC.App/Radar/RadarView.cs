using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;
using SimATC.Core.Taxi;

namespace SimATC.App.Radar;

/// <summary>
/// Radar 2D (RF-39): pistas, calles de rodaje, puestos, puntos de espera, el tráfico y el avión del piloto.
/// Rueda = zoom, arrastrar = desplazar, doble clic = centrar en el piloto, clic en un avión = seleccionarlo (se ve su
/// ruta; con <see cref="FollowSelected"/> el radar lo sigue).
/// Redibuja solo si algo cambió, a lo más <see cref="MaxFps"/> veces por segundo, y nunca con la ventana
/// minimizada u oculta (RF-42b).
/// </summary>
public sealed partial class RadarView : Control
{
    public static readonly StyledProperty<AirportData?> AirportProperty =
        AvaloniaProperty.Register<RadarView, AirportData?>(nameof(Airport));

    /// <summary>Envergadura del avión del piloto (m), para dibujarlo a escala con zoom cercano.</summary>
    public static readonly StyledProperty<double> UserWingspanMProperty =
        AvaloniaProperty.Register<RadarView, double>(nameof(UserWingspanM), 36);

    public double UserWingspanM
    {
        get => GetValue(UserWingspanMProperty);
        set => SetValue(UserWingspanMProperty, value);
    }

    /// <summary>Motores del avión del piloto (con 4 se dibuja como cuatrimotor).</summary>
    public static readonly StyledProperty<Core.Fleet.AircraftShape> UserShapeProperty =
        AvaloniaProperty.Register<RadarView, Core.Fleet.AircraftShape>(nameof(UserShape), Core.Fleet.AircraftShape.Narrow);

    public Core.Fleet.AircraftShape UserShape
    {
        get => GetValue(UserShapeProperty);
        set => SetValue(UserShapeProperty, value);
    }

    public static readonly StyledProperty<UserAircraftState?> UserStateProperty =
        AvaloniaProperty.Register<RadarView, UserAircraftState?>(nameof(UserState));

    /// <summary>Altitud de transición del aeropuerto (ft): sobre ella la etiqueta del piloto muestra nivel de vuelo.</summary>
    public static readonly StyledProperty<double> TransitionAltitudeFtProperty =
        AvaloniaProperty.Register<RadarView, double>(nameof(TransitionAltitudeFt), 18000);

    public double TransitionAltitudeFt
    {
        get => GetValue(TransitionAltitudeFtProperty);
        set => SetValue(TransitionAltitudeFtProperty, value);
    }

    public static readonly StyledProperty<IReadOnlyList<TrafficMarker>?> TrafficProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<TrafficMarker>?>(nameof(Traffic));

    public static readonly StyledProperty<uint?> SelectedIdProperty =
        AvaloniaProperty.Register<RadarView, uint?>(nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> FollowSelectedProperty =
        AvaloniaProperty.Register<RadarView, bool>(nameof(FollowSelected), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay); // el radar lo apaga al arrastrar

    /// <summary>Ruta que queda del avión seleccionado (se dibuja encima de las calles).</summary>
    public static readonly StyledProperty<IReadOnlyList<GeoPoint>?> SelectedRouteProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<GeoPoint>?>(nameof(SelectedRoute));

    /// <summary>La ruta del avión IA elegido: recorrido, por recorrer, fijos y puesto.</summary>
    public static readonly StyledProperty<SelectedFlightView?> SelectedFlightProperty =
        AvaloniaProperty.Register<RadarView, SelectedFlightView?>(nameof(SelectedFlight));

    public SelectedFlightView? SelectedFlight
    {
        get => GetValue(SelectedFlightProperty);
        set => SetValue(SelectedFlightProperty, value);
    }

    private static readonly IPen TrailPen = new ImmutablePen(new ImmutableSolidColorBrush(0xB37BE3A6), 2, new ImmutableDashStyle([2, 3], 0));
    private static readonly IPen AheadPen = new ImmutablePen(new ImmutableSolidColorBrush(0xFF4CC2FF), 2);
    private static readonly IImmutableSolidColorBrush FixBrush = new ImmutableSolidColorBrush(0xFFFFD166);
    private static readonly IPen StandHighlightPen = new ImmutablePen(new ImmutableSolidColorBrush(0xFFFFD166), 2.5);

    private void DrawSelectedFlight(DrawingContext ctx, SelectedFlightView f)
    {
        void Line(IReadOnlyList<GeoPoint> pts, IPen pen)
        {
            if (pts.Count < 2)
                return;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToScreen(ToWorld(pts[0])), false);
                for (var i = 1; i < pts.Count; i++)
                    g.LineTo(ToScreen(ToWorld(pts[i])));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }
        Line(f.Trail, TrailPen);
        Line(f.Ahead, AheadPen);
        foreach (var fix in f.Fixes)
        {
            var s = ToScreen(ToWorld(fix.Position));
            ctx.DrawRectangle(FixBrush, null, new Rect(s.X - 3, s.Y - 3, 6, 6));
            ctx.DrawText(Text(fix.Name, FixBrush), new Point(s.X + 6, s.Y - 7));
        }
        if (f.Stand is { } stand)
        {
            var c = ToScreen(ToWorld(stand));
            var r = Math.Max(8, f.StandRadiusM * _scale);
            ctx.DrawEllipse(null, StandHighlightPen, c, r, r);
        }
    }

    /// <summary>Ruta de rodaje del piloto autorizada por Ground (se pinta en magenta), y su punto de espera.</summary>
    public static readonly StyledProperty<IReadOnlyList<GeoPoint>?> UserRouteProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<GeoPoint>?>(nameof(UserRoute));

    public static readonly StyledProperty<GeoPoint?> UserHoldShortProperty =
        AvaloniaProperty.Register<RadarView, GeoPoint?>(nameof(UserHoldShort));

    /// <summary>Ruta del plan de vuelo (SimBrief, hito 4b): puntos con nombre.</summary>
    public static readonly StyledProperty<IReadOnlyList<PlanPoint>?> PlanRouteProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<PlanPoint>?>(nameof(PlanRoute));

    /// <summary>SID, STAR y aproximación elegidas (datos del simulador), como referencia.</summary>
    public static readonly StyledProperty<IReadOnlyList<ProcedureLine>?> ProceduresProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<ProcedureLine>?>(nameof(Procedures));

    /// <summary>Otros aeropuertos del plan (destino, alterno): pistas y calles, cada uno con su propia proyección.</summary>
    public static readonly StyledProperty<IReadOnlyList<AirportData>?> OtherAirportsProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<AirportData>?>(nameof(OtherAirports));

    public static readonly StyledProperty<bool> ShowStandsProperty =
        AvaloniaProperty.Register<RadarView, bool>(nameof(ShowStands), true);

    public static readonly StyledProperty<bool> ShowPlanProperty =
        AvaloniaProperty.Register<RadarView, bool>(nameof(ShowPlan), true);

    public static readonly StyledProperty<int> MaxFpsProperty =
        AvaloniaProperty.Register<RadarView, int>(nameof(MaxFps), 20);

    /// <summary>Cabeceras de llegada en uso ("17L"), para pintar la pista (ADR-102, bloque D).</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> ArrivalRunwaysProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<string>?>(nameof(ArrivalRunways));

    /// <summary>Cabeceras de salida en uso ("17R").</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> DepartureRunwaysProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<string>?>(nameof(DepartureRunways));

    public IReadOnlyList<string>? ArrivalRunways
    {
        get => GetValue(ArrivalRunwaysProperty);
        set => SetValue(ArrivalRunwaysProperty, value);
    }

    public IReadOnlyList<string>? DepartureRunways
    {
        get => GetValue(DepartureRunwaysProperty);
        set => SetValue(DepartureRunwaysProperty, value);
    }

    /// <summary>Flechas de sentido de las calles (ADR-102): posición y rumbo verdadero, del grafo de rodaje.</summary>
    public static readonly StyledProperty<IReadOnlyList<(GeoPoint Position, double HeadingDeg)>?> FlowArrowsProperty =
        AvaloniaProperty.Register<RadarView, IReadOnlyList<(GeoPoint Position, double HeadingDeg)>?>(nameof(FlowArrows));

    public IReadOnlyList<(GeoPoint Position, double HeadingDeg)>? FlowArrows
    {
        get => GetValue(FlowArrowsProperty);
        set => SetValue(FlowArrowsProperty, value);
    }

    private static readonly IPen FlowArrowPen = new ImmutablePen(new ImmutableSolidColorBrush(0xC05FD4C8), 3);
    private StreamGeometry? _flowArrows;

    /// <summary>Un chevrón por flecha, de 10 m de largo, apuntando en el sentido de la calle.</summary>
    private void BuildFlowArrows()
    {
        _flowArrows = null;
        if (Airport is null || FlowArrows is not { Count: > 0 } arrows)
            return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (var (p, h) in arrows)
            {
                var tip = ToWorld(GeoMath.Destination(p, h, 6));
                g.BeginFigure(ToWorld(GeoMath.Destination(p, h + 150, 8)), false);
                g.LineTo(tip);
                g.LineTo(ToWorld(GeoMath.Destination(p, h - 150, 8)));
                g.EndFigure(false);
            }
        }
        _flowArrows = geo;
    }

    // Pistas en uso por rol (ADR-102): llegadas verde, salidas azul, las dos (mixta) ámbar. Translúcidas sobre el gris.
    private static readonly IImmutableSolidColorBrush ArrivalRunwayBrush = new ImmutableSolidColorBrush(0xB03DDC84);
    private static readonly IImmutableSolidColorBrush DepartureRunwayBrush = new ImmutableSolidColorBrush(0xB04C9BFF);
    private static readonly IImmutableSolidColorBrush MixedRunwayBrush = new ImmutableSolidColorBrush(0xB0F5A524);
    private StreamGeometry? _arrivalRunways, _departureRunways, _mixedRunways;

    /// <summary>Las pistas en uso de cada rol, como geometría (se rearma al cambiar el aeropuerto o las pistas).</summary>
    private void BuildRunwayRoles(AirportData? airport)
    {
        _arrivalRunways = _departureRunways = _mixedRunways = null;
        if (airport is null)
            return;
        bool Has(IReadOnlyList<string>? list, Runway r) =>
            list is not null && (list.Contains(r.Primary.Ident, StringComparer.OrdinalIgnoreCase)
                                 || list.Contains(r.Secondary.Ident, StringComparer.OrdinalIgnoreCase));
        StreamGeometry? Build(Func<Runway, bool> which)
        {
            var geo = new StreamGeometry();
            var any = false;
            using (var g = geo.Open())
            {
                foreach (var r in airport.Runways)
                {
                    if (!which(r))
                        continue;
                    any = true;
                    var a = ToWorld(r.Primary.Position);
                    var b = ToWorld(r.Secondary.Position);
                    var along = b - a;
                    var len = Math.Sqrt(along.X * along.X + along.Y * along.Y);
                    var side = new Vector(-along.Y / len, along.X / len) * (r.WidthM / 2);
                    g.BeginFigure(a + side, true);
                    g.LineTo(b + side);
                    g.LineTo(b - side);
                    g.LineTo(a - side);
                    g.EndFigure(true);
                }
            }
            return any ? geo : null;
        }
        var arr = ArrivalRunways;
        var dep = DepartureRunways;
        _mixedRunways = Build(r => Has(arr, r) && Has(dep, r));
        _arrivalRunways = Build(r => Has(arr, r) && !Has(dep, r));
        _departureRunways = Build(r => Has(dep, r) && !Has(arr, r));
    }

    private static readonly IImmutableSolidColorBrush Background = new ImmutableSolidColorBrush(0xFF0E1621);
    private static readonly IImmutableSolidColorBrush RunwayBrush = new ImmutableSolidColorBrush(0xFF8A8F98);
    private static readonly IImmutableSolidColorBrush TaxiBrush = new ImmutableSolidColorBrush(0xFF3C4A5C);
    private static readonly IImmutableSolidColorBrush ParkingBrush = new ImmutableSolidColorBrush(0xFF6A7D94);
    private static readonly IImmutableSolidColorBrush HoldMarkedBrush = new ImmutableSolidColorBrush(0xFFE5484D);
    private static readonly IImmutableSolidColorBrush HoldInferredBrush = new ImmutableSolidColorBrush(0xFFF5A524);
    private static readonly IImmutableSolidColorBrush UserBrush = new ImmutableSolidColorBrush(0xFF3DDC84);
    private static readonly IImmutableSolidColorBrush TextBrush = new ImmutableSolidColorBrush(0xFFC8D1DC);
    private static readonly IImmutableSolidColorBrush TrafficBrush = new ImmutableSolidColorBrush(0xFF9DB4D3);
    private static readonly IImmutableSolidColorBrush PavementBrush = new ImmutableSolidColorBrush(0xFF1C2735);
    private static readonly IImmutableSolidColorBrush StandBrush = new ImmutableSolidColorBrush(0xFF8FA3BB);
    private static readonly IPen StandPen = new ImmutablePen(new ImmutableSolidColorBrush(0xFF4A5B70), 1);
    private static readonly IImmutableSolidColorBrush LabelBackBrush = new ImmutableSolidColorBrush(0xD00E1621);
    private static readonly IImmutableSolidColorBrush UserRouteBrush = new ImmutableSolidColorBrush(0xD9E879F9);
    private static readonly IImmutableSolidColorBrush HoldBarBrush = new ImmutableSolidColorBrush(0xFFFF5A5F);
    private static readonly IImmutableSolidColorBrush PlanBrush = new ImmutableSolidColorBrush(0xFFC77DFF);
    private static readonly IPen PlanPen = new ImmutablePen(new ImmutableSolidColorBrush(0xFFC77DFF), 2, new ImmutableDashStyle([4, 3], 0));
    // Mapa base: tonos apagados, por debajo del pavimento en brillo.
    private static readonly IImmutableSolidColorBrush CoastBrush = new ImmutableSolidColorBrush(0xFF3B5470);
    private static readonly IImmutableSolidColorBrush BorderBrush = new ImmutableSolidColorBrush(0xFF4A4F63);
    private static readonly Lazy<WorldMap?> World = new(() => WorldMap.Load(WorldMap.DefaultPath));
    private static readonly IImmutableSolidColorBrush SidBrush = new ImmutableSolidColorBrush(0xFF7BE3A6);
    private static readonly IImmutableSolidColorBrush StarBrush = new ImmutableSolidColorBrush(0xFFFF9F5A);
    private static readonly IImmutableSolidColorBrush ApproachBrush = new ImmutableSolidColorBrush(0xFF4CC2FF);
    private static readonly IPen SidPen = new ImmutablePen(SidBrush, 2);
    private static readonly IImmutableSolidColorBrush RecommendedBrush = new ImmutableSolidColorBrush(0xFFE8EDF3);
    private static readonly IPen RecommendedDashPen = new ImmutablePen(RecommendedBrush, 2, new ImmutableDashStyle([6, 4], 0));
    private static readonly IPen StarPen = new ImmutablePen(StarBrush, 2);
    private static readonly IPen ApproachPen = new ImmutablePen(ApproachBrush, 2);
    private static readonly IPen SidDashPen = new ImmutablePen(SidBrush, 2, new ImmutableDashStyle([3, 3], 0));
    private static readonly IPen StarDashPen = new ImmutablePen(StarBrush, 2, new ImmutableDashStyle([3, 3], 0));
    private static readonly IPen ApproachDashPen = new ImmutablePen(ApproachBrush, 2, new ImmutableDashStyle([3, 3], 0));
    private static readonly IImmutableSolidColorBrush ConstraintBrush = new ImmutableSolidColorBrush(0xFFC8D1DC);
    private static readonly IImmutableSolidColorBrush OtherAirportBrush = new ImmutableSolidColorBrush(0xFF8FA3BB);
    private static readonly IImmutableSolidColorBrush TaxiNameBrush = new ImmutableSolidColorBrush(0xFFB8A15A);
    private static readonly IImmutableSolidColorBrush HoldingBrush = new ImmutableSolidColorBrush(0xFFF5A524);
    private static readonly IImmutableSolidColorBrush SelectedBrush = new ImmutableSolidColorBrush(0xFF4CC2FF);
    private static readonly IPen SelectedPen = new ImmutablePen(SelectedBrush, 1.5);

    /// <summary>Distancia en píxeles para que un clic seleccione un avión, y movimiento máximo para que sea clic.</summary>
    private const double PickRadiusPx = 14;
    private const double ClickSlopPx = 4;

    /// <summary>Escala (px/m) desde la que se escriben las etiquetas del tráfico.</summary>
    private const double TrafficLabelMinScale = 0.6;

    /// <summary>Escala desde la que se dibujan los puestos con su nombre (px/m).</summary>
    private const double StandLabelMinScale = 0.7;

    /// <summary>Escala desde la que se escriben los nombres de las calles, y separación entre etiquetas de una calle (m).</summary>
    private const double TaxiNameMinScale = 0.25;
    private const double TaxiNameSpacingM = 250;

    private readonly DispatcherTimer _timer = new();
    private bool _dirty = true;

    // Movimiento parejo del tráfico (ADR-133): cuándo llegaron los últimos datos y si alguno se mueve.
    private readonly System.Diagnostics.Stopwatch _trafficClock = System.Diagnostics.Stopwatch.StartNew();
    private bool _trafficMoving;
    private bool _skipSmooth;

    /// <summary>Como mucho se adelanta esto (s) la posición de un avión desde el último dato.</summary>
    public const double MaxExtrapolateS = 0.6;

    /// <summary>Por debajo de esta velocidad (kt) un avión se dibuja donde dice el dato (parado o casi).</summary>
    public const double MovingKt = 3;

    // Geometría en metros locales con Y hacia abajo (= -norte), construida una vez por aeropuerto.
    private LocalProjection _projection;
    private MercatorProjection _mercator;
    private StreamGeometry? _taxiGeometry;
    private StreamGeometry? _runwayGeometry;
    private Point[] _parkings = [];
    private (Point Pos, bool Marked)[] _holds = [];
    private (Point Pos, FormattedText Label)[] _runwayLabels = [];
    private (Point Pos, FormattedText Label)[] _taxiLabels = [];
    // Pavimento de las calles con su ancho real (WidthM del simulador), agrupado por ancho.
    private (double WidthM, StreamGeometry Geometry)[] _pavement = [];
    private (Point Pos, double RadiusM, FormattedText Label)[] _stands = [];
    private IPen[] _pavementPens = [];

    // Vista: centro en metros (Y hacia abajo) y escala en píxeles por metro.
    private Point _center;
    private double _scale = 0.1;

    /// <summary>El mapa base se ve entero por debajo de esta escala (px/m, ~100 km en 1000 px) y desaparece por encima de
    /// <see cref="WorldMapHideScale"/> (~20 km): a escala de aeropuerto no se dibuja.</summary>
    private const double WorldMapFullScale = 0.01;
    private const double WorldMapHideScale = 0.05;

    /// <summary>Radio del mapa base alrededor del aeropuerto (m): el mundo entero (radar global, ADR-062; Mercator es conforme).</summary>
    private const double WorldMapRadiusM = 20_000_000;

    /// <summary>Restricciones de los procedimientos desde esta escala (px/m, ~170 km en 1000 px).</summary>
    private const double ConstraintMinScale = 0.006;

    /// <summary>Detalle de los otros aeropuertos desde esta escala (~100 km en 1000 px); más lejos, solo su nombre.</summary>
    private const double OtherRunwayLabelMinScale = 0.01;

    private HashSet<string> _procedureIdents = [];

    /// <summary>Geometría de otro aeropuerto (destino, alterno), ya ubicada en el plano del aeropuerto principal.</summary>
    private sealed class AirportLayer
    {
        public (double WidthM, StreamGeometry Geometry)[] Pavement = [];
        public IPen[] Pens = [];
        public StreamGeometry Taxi = new();
        public StreamGeometry Runways = new();
        public (Point Pos, FormattedText Label)[] RunwayLabels = [];
        public Point Center;
        public FormattedText Name = null!;
        public (Point Pos, double RadiusM, FormattedText Label)[] Stands = [];
        public (Point Pos, FormattedText Label)[] TaxiLabels = [];
        public (Point Pos, bool Marked)[] Holds = [];
    }

    private AirportLayer[] _others = [];

    private StreamGeometry? _coastGeometry;
    private StreamGeometry? _borderGeometry;
    // El mundo entero simplificado (un punto cada ~15 km): de lejos dibujar 1:50m completo hacía lento el zoom (ADR-064).
    private StreamGeometry? _coastFar;
    private StreamGeometry? _borderFar;

    /// <summary>Por debajo de esta escala (px/m, ~1000 km en pantalla) se usa el mapa simplificado.</summary>
    private const double FarMapScale = 0.001;

    /// <summary>Radio del mapa detallado alrededor del aeropuerto (m).</summary>
    private const double NearMapRadiusM = 1_500_000;
    private IPen _coastPen = new Pen(CoastBrush, 1);
    private IPen _borderPen = new Pen(BorderBrush, 1);

    /// <summary>Zoom mínimo (px/m): ~30.000 km en una ventana de 1000 px (el mundo entero, radar global).</summary>
    private const double MinScale = 0.00003;
    private const double MaxScale = 20;
    private bool _fitPending;
    private Point? _dragLast;
    private Point _pressAt;
    private bool _dragged;
    private double _penScale = double.NaN;
    private IPen? _taxiPen;

    static RadarView()
    {
        ClipToBoundsProperty.OverrideDefaultValue<RadarView>(true);
    }

    public RadarView()
    {
        _timer.Tick += OnTick;
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        DoubleTapped += (_, _) => CenterOnUser();
    }

    public AirportData? Airport
    {
        get => GetValue(AirportProperty);
        set => SetValue(AirportProperty, value);
    }

    public UserAircraftState? UserState
    {
        get => GetValue(UserStateProperty);
        set => SetValue(UserStateProperty, value);
    }

    public IReadOnlyList<TrafficMarker>? Traffic
    {
        get => GetValue(TrafficProperty);
        set => SetValue(TrafficProperty, value);
    }

    /// <summary>Clic sobre un avión (o en vacío: null), aunque sea el que ya estaba seleccionado.</summary>
    public event Action<uint?>? Picked;

    public uint? SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public bool FollowSelected
    {
        get => GetValue(FollowSelectedProperty);
        set => SetValue(FollowSelectedProperty, value);
    }

    public IReadOnlyList<GeoPoint>? SelectedRoute
    {
        get => GetValue(SelectedRouteProperty);
        set => SetValue(SelectedRouteProperty, value);
    }

    public IReadOnlyList<GeoPoint>? UserRoute
    {
        get => GetValue(UserRouteProperty);
        set => SetValue(UserRouteProperty, value);
    }

    public GeoPoint? UserHoldShort
    {
        get => GetValue(UserHoldShortProperty);
        set => SetValue(UserHoldShortProperty, value);
    }

    public IReadOnlyList<PlanPoint>? PlanRoute
    {
        get => GetValue(PlanRouteProperty);
        set => SetValue(PlanRouteProperty, value);
    }

    public IReadOnlyList<ProcedureLine>? Procedures
    {
        get => GetValue(ProceduresProperty);
        set => SetValue(ProceduresProperty, value);
    }

    public IReadOnlyList<AirportData>? OtherAirports
    {
        get => GetValue(OtherAirportsProperty);
        set => SetValue(OtherAirportsProperty, value);
    }

    public bool ShowStands
    {
        get => GetValue(ShowStandsProperty);
        set => SetValue(ShowStandsProperty, value);
    }

    public bool ShowPlan
    {
        get => GetValue(ShowPlanProperty);
        set => SetValue(ShowPlanProperty, value);
    }

    public int MaxFps
    {
        get => GetValue(MaxFpsProperty);
        set => SetValue(MaxFpsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AirportProperty)
        {
            BuildGeometry(Airport);
            BuildRunwayRoles(Airport);
            BuildFlowArrows();
            BuildOtherAirports();
            _fitPending = true;
        }
        else if (change.Property == ArrivalRunwaysProperty || change.Property == DepartureRunwaysProperty)
        {
            BuildRunwayRoles(Airport);
        }
        else if (change.Property == FlowArrowsProperty)
        {
            BuildFlowArrows();
        }
        else if (change.Property == OtherAirportsProperty)
        {
            BuildOtherAirports();
        }
        else if (change.Property == ProceduresProperty)
        {
            _procedureIdents = Procedures is { } procs
                ? [.. procs.SelectMany(l => l.Shape.Waypoints).Where(w => !w.Approximate).Select(w => w.Ident)]
                : [];
        }
        else if (change.Property == TrafficProperty)
        {
            _trafficClock.Restart();
            _trafficMoving = false;
            foreach (var t in Traffic ?? [])
            {
                if (t.SpeedKt > MovingKt)
                {
                    _trafficMoving = true;
                    break;
                }
            }
        }
        else if (change.Property == MaxFpsProperty)
        {
            _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(MaxFps, 1, 60));
        }
        else if (change.Property == BoundsProperty && _fitPending)
        {
            Fit();
        }
        _dirty = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(MaxFps, 1, 60));
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Zoom y arrastre se dibujan en el próximo cuadro de la pantalla, sin esperar el reloj del radar (que va a
    /// <see cref="MaxFps"/>): si no, el zoom avanzaba a saltos. Avalonia junta varias invalidaciones en un solo cuadro.
    /// </summary>
    private void RedrawNow()
    {
        _dirty = false;
        InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Las capas de navegación se rearman un momento después de terminar el zoom (mientras tanto se escalan).
        if (_navRebuildAt is { } at && DateTime.UtcNow >= at)
        {
            _navRebuildAt = null;
            _dirty = true;
        }
        // Con tráfico en movimiento se redibuja entre datos, un cuadro por medio (la mitad de MaxFps), para que avance parejo.
        if (!_dirty && _trafficMoving && _trafficClock.Elapsed.TotalSeconds < MaxExtrapolateS)
        {
            _skipSmooth = !_skipSmooth;
            _dirty = !_skipSmooth;
        }
        if (!_dirty || !IsEffectivelyVisible)
            return;
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized })
            return;
        _dirty = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Background, null, new Rect(Bounds.Size));
        if (_taxiGeometry is null || _runwayGeometry is null)
            return;

        if (_scale != _penScale)
        {
            _taxiPen = new Pen(TaxiBrush, 2.0 / _scale); // 2 px a cualquier zoom
            _coastPen = new Pen(CoastBrush, 1.2 / _scale);
            _borderPen = new Pen(BorderBrush, 1.0 / _scale, new DashStyle([4, 3], 0));
            _penScale = _scale;
        }

        // Mapa base (costas y fronteras) debajo de todo, y solo de lejos: se desvanece al acercarse y desaparece a
        // escala de aeropuerto, para no mezclarse nunca con calles y pistas.
        var mapOpacity = Math.Clamp((WorldMapHideScale - _scale) / (WorldMapHideScale - WorldMapFullScale), 0, 1);
        var far = _scale < FarMapScale && _coastFar is not null;
        var coast = far ? _coastFar : _coastGeometry;
        var border = far ? _borderFar : _borderGeometry;
        if (mapOpacity > 0 && coast is not null)
        {
            using (ctx.PushOpacity(mapOpacity))
            using (ctx.PushTransform(WorldToScreen()))
            {
                if (border is not null)
                    ctx.DrawGeometry(null, _borderPen, border);
                ctx.DrawGeometry(null, _coastPen, coast);
            }
        }

        // Capas de navegación del simulador (fijos, radioayudas, aerovías, aeropuertos del mundo).
        DrawNavigation(ctx);

        // Con "seguir", el centro va con el avión seleccionado.
        if (FollowSelected && SelectedId == UserId && UserState is { } me)
            _center = ToWorld(me.Position);
        else if (FollowSelected && SelectedId is { } follow && Traffic?.FirstOrDefault(t => t.Id == follow) is { } followed)
            _center = ToWorld(followed.Position);

        // Pavimento (en metros: escala con el zoom) y, encima, la línea central y las pistas.
        if (_pavementPens.Length != _pavement.Length)
            _pavementPens = [.. _pavement.Select(p => (IPen)new ImmutablePen(PavementBrush, p.WidthM, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round))];
        using (ctx.PushTransform(WorldToScreen()))
        {
            foreach (var other in _others)
            {
                for (var i = 0; i < other.Pavement.Length; i++)
                    ctx.DrawGeometry(null, other.Pens[i], other.Pavement[i].Geometry);
                ctx.DrawGeometry(null, _taxiPen, other.Taxi);
                ctx.DrawGeometry(RunwayBrush, null, other.Runways);
            }
            for (var i = 0; i < _pavement.Length; i++)
                ctx.DrawGeometry(null, _pavementPens[i], _pavement[i].Geometry);
            ctx.DrawGeometry(null, _taxiPen, _taxiGeometry);
            if (_flowArrows is not null)
                ctx.DrawGeometry(null, FlowArrowPen, _flowArrows);
            ctx.DrawGeometry(RunwayBrush, null, _runwayGeometry);
            if (_arrivalRunways is not null)
                ctx.DrawGeometry(ArrivalRunwayBrush, null, _arrivalRunways);
            if (_departureRunways is not null)
                ctx.DrawGeometry(DepartureRunwayBrush, null, _departureRunways);
            if (_mixedRunways is not null)
                ctx.DrawGeometry(MixedRunwayBrush, null, _mixedRunways);
        }

        if (ShowPlan && PlanRoute is { Count: > 1 } plan)
            DrawPlan(ctx, plan);
        if (ShowPlan && Procedures is { Count: > 0 } procedures)
        {
            foreach (var line in procedures)
                DrawProcedure(ctx, line);
        }
        if (UserRoute is { Count: > 1 } userRoute)
            DrawUserRoute(ctx, userRoute);
        if (ShowStands && _scale >= StandLabelMinScale)
        {
            foreach (var (pos, radius, label) in _stands)
            {
                var c = ToScreen(pos);
                ctx.DrawEllipse(null, StandPen, c, radius * _scale, radius * _scale);
                ctx.DrawText(label, new Point(c.X - label.Width / 2, c.Y + radius * _scale + 2));
            }
        }

        if (SelectedFlight is { } selectedFlight)
            DrawSelectedFlight(ctx, selectedFlight);
        if (SelectedRoute is { Count: > 1 } route)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToScreen(ToWorld(route[0])), false);
                for (var i = 1; i < route.Count; i++)
                    g.LineTo(ToScreen(ToWorld(route[i])));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, SelectedPen, geo);
        }

        var parkingRadius = Math.Clamp(_scale * 15, 1.5, 6);
        foreach (var p in _parkings)
            ctx.DrawEllipse(ParkingBrush, null, ToScreen(p), parkingRadius, parkingRadius);

        foreach (var (pos, marked) in _holds)
            ctx.DrawEllipse(marked ? HoldMarkedBrush : HoldInferredBrush, null, ToScreen(pos), 3, 3);

        if (_scale >= TaxiNameMinScale)
        {
            foreach (var (pos, label) in _taxiLabels)
            {
                var t = ToScreen(pos);
                ctx.DrawText(label, new Point(t.X - label.Width / 2, t.Y - label.Height / 2));
            }
        }

        foreach (var (pos, label) in _runwayLabels)
        {
            var s = ToScreen(pos);
            ctx.DrawText(label, new Point(s.X - label.Width / 2, s.Y - label.Height / 2));
        }
        foreach (var other in _others)
        {
            // De lejos, el nombre del aeropuerto; de cerca, el mismo detalle que el principal.
            if (_scale < OtherRunwayLabelMinScale)
            {
                var c = ToScreen(other.Center);
                ctx.DrawText(other.Name, new Point(c.X - other.Name.Width / 2, c.Y + 8));
                continue;
            }
            foreach (var (pos, label) in other.RunwayLabels)
            {
                var s = ToScreen(pos);
                ctx.DrawText(label, new Point(s.X - label.Width / 2, s.Y - label.Height / 2));
            }
            foreach (var (pos, marked) in other.Holds)
                ctx.DrawEllipse(marked ? HoldMarkedBrush : HoldInferredBrush, null, ToScreen(pos), 3, 3);
            if (_scale >= TaxiNameMinScale)
            {
                foreach (var (pos, label) in other.TaxiLabels)
                {
                    var t = ToScreen(pos);
                    ctx.DrawText(label, new Point(t.X - label.Width / 2, t.Y - label.Height / 2));
                }
            }
            if (ShowStands && _scale >= StandLabelMinScale)
            {
                foreach (var (pos, radius, label) in other.Stands)
                {
                    var c = ToScreen(pos);
                    ctx.DrawEllipse(null, StandPen, c, radius * _scale, radius * _scale);
                    ctx.DrawText(label, new Point(c.X - label.Width / 2, c.Y + radius * _scale + 2));
                }
            }
            else
            {
                var dot = Math.Clamp(_scale * 15, 1.5, 6);
                foreach (var (pos, _, _) in other.Stands)
                    ctx.DrawEllipse(ParkingBrush, null, ToScreen(pos), dot, dot);
            }
        }

        if (Traffic is { Count: > 0 } traffic)
        {
            // Entre dos actualizaciones de los datos (5 por segundo) cada avión avanza según su rumbo y velocidad: se mueve
            // parejo en vez de a saltos (ADR-133).
            var ahead = Math.Min(_trafficClock.Elapsed.TotalSeconds, MaxExtrapolateS);
            foreach (var t in traffic)
            {
                var at = t.SpeedKt > MovingKt ? GeoMath.Destination(t.Position, t.HeadingTrueDeg, t.SpeedKt * 0.514444 * ahead) : t.Position;
                var p = ToScreen(ToWorld(at));
                var selected = t.Id != 0 && t.Id == SelectedId;
                var brush = selected ? SelectedBrush : t.Holding ? HoldingBrush : TrafficBrush;
                if (!DrawToScale(ctx, p, t.HeadingTrueDeg, t.WingspanM, t.Shape, brush))
                    ctx.DrawGeometry(brush, null, Arrow(p, t.HeadingTrueDeg, 0.7));
                if (selected)
                    ctx.DrawEllipse(null, SelectedPen, p, 12, 12);
                if (_scale >= TrafficLabelMinScale || selected)
                    ctx.DrawText(Text(t.Label, brush), new Point(p.X + 9, p.Y - 7));
            }
        }

        if (UserState is { } user)
            DrawUser(ctx, user);
    }

    /// <summary>Ruta del piloto (magenta, ancho fijo en pantalla) y la barra roja del punto de espera.</summary>
    private void DrawUserRoute(DrawingContext ctx, IReadOnlyList<GeoPoint> route)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ToScreen(ToWorld(route[0])), false);
            for (var i = 1; i < route.Count; i++)
                g.LineTo(ToScreen(ToWorld(route[i])));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(UserRouteBrush, 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
        if (UserHoldShort is not { } hold)
            return;
        // Barra perpendicular a la ruta en el punto de espera.
        var h = ToScreen(ToWorld(hold));
        var before = ToScreen(ToWorld(route[Math.Max(0, route.Count - 2)]));
        var dir = h - before;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        var across = len > 0 ? new Vector(-dir.Y / len, dir.X / len) * 12 : new Vector(12, 0);
        ctx.DrawLine(new Pen(HoldBarBrush, 4), h - across, h + across);
    }

    /// <summary>
    /// Un procedimiento: línea del color de su tipo, continua donde es publicada y punteada donde es aproximada (rumbo
    /// hasta altitud, giros). Puntos con su nombre y, de cerca, sus restricciones de altitud y velocidad.
    /// </summary>
    private void DrawProcedure(DrawingContext ctx, ProcedureLine line)
    {
        var (pen, dashed, brush) = line.Kind switch
        {
            ProcedureKind.Sid => (SidPen, SidDashPen, SidBrush),
            ProcedureKind.Star => (StarPen, StarDashPen, StarBrush),
            ProcedureKind.SidRecommended => (RecommendedDashPen, RecommendedDashPen, RecommendedBrush),
            _ => (ApproachPen, ApproachDashPen, ApproachBrush),
        };
        foreach (var seg in line.Shape.Segments)
        {
            if (seg.Points.Count < 2)
                continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToScreen(ToWorld(seg.Points[0])), false);
                for (var i = 1; i < seg.Points.Count; i++)
                    g.LineTo(ToScreen(ToWorld(seg.Points[i])));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, seg.Approximate ? dashed : pen, geo);
        }
        var showConstraints = _scale >= ConstraintMinScale;
        var first = true;
        foreach (var w in line.Shape.Waypoints)
        {
            var s = ToScreen(ToWorld(w.Position));
            if (w.Approximate)
                ctx.DrawEllipse(null, pen, s, 3, 3);
            else
                ctx.DrawRectangle(brush, null, new Rect(s.X - 3, s.Y - 3, 6, 6));
            var name = Text(first ? $"{w.Ident}  {line.Name}" : w.Ident, brush);
            ctx.DrawText(name, new Point(s.X + 6, s.Y - 8));
            first = false;
            if (showConstraints && w.Constraints.Length > 0)
                ctx.DrawText(Text(w.Constraints, ConstraintBrush), new Point(s.X + 6, s.Y + 5));
        }
    }

    /// <summary>Ruta del plan de vuelo: línea violeta punteada, con arcos en los giros, y los nombres de los puntos.</summary>
    private IReadOnlyList<PlanPoint>? _flyByFor;
    private List<GeoPoint> _flyBy = [];

    private void DrawPlan(DrawingContext ctx, IReadOnlyList<PlanPoint> plan)
    {
        // La línea con los arcos de giro fly-by (como la vuela el avión); se calcula una vez por plan.
        if (!ReferenceEquals(_flyByFor, plan))
        {
            _flyByFor = plan;
            _flyBy = Core.Plan.RouteGeometry.FlyBy([.. plan.Select(p => p.Position)]);
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ToScreen(ToWorld(_flyBy[0])), false);
            for (var i = 1; i < _flyBy.Count; i++)
                g.LineTo(ToScreen(ToWorld(_flyBy[i])));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, PlanPen, geo);
        foreach (var p in plan)
        {
            var s = ToScreen(ToWorld(p.Position));
            ctx.DrawEllipse(PlanBrush, null, s, 3, 3);
            // Si un procedimiento ya nombra ese punto, no se repite la etiqueta.
            if (p.Name.Length > 0 && !(Procedures is not null && ShowPlan && _procedureIdents.Contains(p.Name)))
                ctx.DrawText(Text(p.Name, PlanBrush), new Point(s.X + 6, s.Y - 7));
        }
    }

    // Silueta de avión (proa, alas, estabilizadores) en unidades de "adelante, derecha"; mide 18 px, como la flecha.
    private static readonly (double F, double R)[] PlaneShape =
    [
        (10, 0), (4, 1.4), (1.5, 8), (-0.5, 8), (-1, 1.4), (-5.5, 1.2), (-7.5, 4), (-8.5, 4), (-8, 0),
        (-8.5, -4), (-7.5, -4), (-5.5, -1.2), (-1, -1.4), (-0.5, -8), (1.5, -8), (4, -1.4),
    ];

    /// <summary>Avión del piloto: silueta de avión apuntando al rumbo, centrada en <paramref name="p"/>.</summary>
    private static StreamGeometry Airplane(Point p, double headingDeg)
    {
        var h = headingDeg * GeoMath.DegToRad;
        var fwd = new Vector(Math.Sin(h), -Math.Cos(h));
        var right = new Vector(-fwd.Y, fwd.X);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(p + fwd * PlaneShape[0].F + right * PlaneShape[0].R, true);
            for (var i = 1; i < PlaneShape.Length; i++)
                g.LineTo(p + fwd * PlaneShape[i].F + right * PlaneShape[i].R);
            g.EndFigure(true);
        }
        return geo;
    }

    /// <summary>Flecha con forma de avión apuntando al rumbo, centrada en <paramref name="p"/>.</summary>
    private static StreamGeometry Arrow(Point p, double headingDeg, double size)
    {
        var h = headingDeg * GeoMath.DegToRad;
        var fwd = new Vector(Math.Sin(h), -Math.Cos(h));
        var right = new Vector(-fwd.Y, fwd.X);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(p + fwd * (10 * size), true);
            g.LineTo(p - fwd * (6 * size) + right * (6 * size));
            g.LineTo(p - fwd * (3 * size));
            g.LineTo(p - fwd * (6 * size) - right * (6 * size));
            g.EndFigure(true);
        }
        return geo;
    }

    /// <summary>Id con que se selecciona el avión del piloto (clic sobre él, "Seguirme").</summary>
    public const uint UserId = uint.MaxValue;

    private void DrawUser(DrawingContext ctx, UserAircraftState user)
    {
        var p = ToScreen(ToWorld(user.Position));
        if (!DrawToScale(ctx, p, user.HeadingTrueDeg, UserWingspanM, UserShape, UserBrush))
            ctx.DrawGeometry(UserBrush, null, Airplane(p, user.HeadingTrueDeg));
        if (SelectedId == UserId)
            ctx.DrawEllipse(null, SelectedPen, p, 12, 12);

        var inv = CultureInfo.InvariantCulture;
        var label = Text($"{user.AltitudeText(TransitionAltitudeFt)}  {user.GroundSpeedKt.ToString("0", inv)} kt", UserBrush);
        ctx.DrawRectangle(LabelBackBrush, null, new Rect(p.X + 9, p.Y - 10, label.Width + 6, label.Height + 4), 3, 3);
        ctx.DrawText(label, new Point(p.X + 12, p.Y - 8));
    }

    private void BuildGeometry(AirportData? airport)
    {
        _taxiGeometry = null;
        _runwayGeometry = null;
        _parkings = [];
        _holds = [];
        _runwayLabels = [];
        _taxiLabels = [];
        _pavement = [];
        _stands = [];
        _pavementPens = [];
        _coastGeometry = null;
        _borderGeometry = null;
        if (airport is null)
            return;

        _projection = new LocalProjection(airport.Reference);
        _mercator = new MercatorProjection(airport.Reference);
        BuildWorldMap(airport.Reference);
        var graph = TaxiGraph.Build(airport);

        var taxi = new StreamGeometry();
        using (var g = taxi.Open())
        {
            for (var n = 0; n < graph.NodeCount; n++)
            {
                foreach (var (to, type, _) in graph.Neighbors(n))
                {
                    if (to < n || type == TaxiPathType.Runway)
                        continue; // cada tramo una vez; los de pista quedan bajo el dibujo de la pista
                    g.BeginFigure(ToPoint(graph.Position(n)), false);
                    g.LineTo(ToPoint(graph.Position(to)));
                    g.EndFigure(false);
                }
            }
        }
        _taxiGeometry = taxi;

        // Pavimento: cada tramo con su ancho (redondeado a 2 m para agrupar); los de pista quedan bajo la pista.
        var byWidth = new Dictionary<double, List<(LocalPoint A, LocalPoint B)>>();
        var pointCount = airport.TaxiPoints.Count;
        foreach (var path in airport.TaxiPaths)
        {
            if (path.Type is not (TaxiPathType.Taxi or TaxiPathType.Path or TaxiPathType.Parking))
                continue;
            var to = path.Type == TaxiPathType.Parking ? pointCount + path.End : path.End;
            if (path.Start < 0 || path.Start >= pointCount || to < 0 || to >= graph.NodeCount)
                continue;
            var width = Math.Round(Math.Clamp(path.WidthM > 1 ? path.WidthM : 15, 4, 80) / 2) * 2;
            if (!byWidth.TryGetValue(width, out var list))
                byWidth[width] = list = [];
            list.Add((graph.Position(path.Start), graph.Position(to)));
        }
        _pavement = [.. byWidth.OrderByDescending(kv => kv.Key).Select(kv =>
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                foreach (var (a, b) in kv.Value)
                {
                    g.BeginFigure(ToPoint(a), false);
                    g.LineTo(ToPoint(b));
                    g.EndFigure(false);
                }
            }
            return (kv.Key, geo);
        })];

        // Puestos con su nombre ("Gate 16", "Gate C4", "Parking 58") y su radio.
        _stands = [.. airport.Parkings.Where(p => p.Type != ParkingType.Vehicle).Select(p =>
            (ToWorld(p.Position), p.RadiusM, new FormattedText(StandName(p), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 10, StandBrush)))];

        // Nombres de las calles: en el medio de los tramos de más de 40 m, sin repetir una calle a menos de 250 m.
        var taxiLabels = new List<(Point Pos, string Name)>();
        for (var n = 0; n < graph.NodeCount; n++)
        {
            foreach (var (to, type, name) in graph.Neighbors(n))
            {
                if (to < n || name.Length == 0 || type is TaxiPathType.Runway or TaxiPathType.Parking)
                    continue;
                var a = graph.Position(n);
                var b = graph.Position(to);
                if (LocalPoint.Distance(a, b) < 40)
                    continue;
                var mid = ToPoint(new LocalPoint((a.EastM + b.EastM) / 2, (a.NorthM + b.NorthM) / 2));
                var tooClose = false;
                foreach (var (pos, other) in taxiLabels)
                {
                    if (other == name && Math.Abs(pos.X - mid.X) + Math.Abs(pos.Y - mid.Y) < TaxiNameSpacingM)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                    taxiLabels.Add((mid, name));
            }
        }
        _taxiLabels = [.. taxiLabels.Select(l => (l.Pos, new FormattedText(l.Name, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, TaxiNameBrush)))];

        var runways = new StreamGeometry();
        var labels = new List<(Point, FormattedText)>();
        using (var g = runways.Open())
        {
            foreach (var r in airport.Runways)
            {
                var a = ToWorld(r.Primary.Position);
                var b = ToWorld(r.Secondary.Position);
                var along = b - a;
                var len = Math.Sqrt(along.X * along.X + along.Y * along.Y);
                var side = new Vector(-along.Y / len, along.X / len) * (r.WidthM / 2);
                g.BeginFigure(a + side, true);
                g.LineTo(b + side);
                g.LineTo(b - side);
                g.LineTo(a - side);
                g.EndFigure(true);

                // Etiquetas un poco antes de cada extremo, del lado de afuera.
                var unit = along / len;
                labels.Add((a - unit * 120, Text(r.Primary.Ident, TextBrush)));
                labels.Add((b + unit * 120, Text(r.Secondary.Ident, TextBrush)));
            }
        }
        _runwayGeometry = runways;
        _runwayLabels = [.. labels];

        _parkings = [.. airport.Parkings.Where(p => p.Type != ParkingType.Vehicle).Select(p => ToWorld(p.Position))];
        _holds = [.. graph.RunwayEntries
            .Select(e => (ToPoint(graph.Position(e.HoldNode)), e.HoldIsMarked))
            .Distinct()];
    }

    /// <summary>
    /// Destino y alterno: pavimento, calles y pistas con su propia proyección local (no se deforman con la distancia),
    /// trasladados a su lugar en el plano del aeropuerto principal. Una vez, al cambiar el plan.
    /// </summary>
    private void BuildOtherAirports()
    {
        _others = [];
        if (Airport is not { } main || OtherAirports is not { Count: > 0 } others)
            return;
        var layers = new List<AirportLayer>();
        foreach (var a in others)
        {
            if (a.Icao == main.Icao)
                continue;
            var origin = ToWorld(a.Reference);
            var graph = TaxiGraph.Build(a);
            var proj = graph.Projection;
            Point Map(LocalPoint lp) => ToWorld(proj.ToGeo(lp));
            var layer = new AirportLayer
            {
                Center = origin,
                Name = Text(a.Icao, OtherAirportBrush),
            };
            var taxi = new StreamGeometry();
            using (var g = taxi.Open())
            {
                for (var n = 0; n < graph.NodeCount; n++)
                {
                    foreach (var (to, type, _) in graph.Neighbors(n))
                    {
                        if (to < n || type == TaxiPathType.Runway)
                            continue;
                        g.BeginFigure(Map(graph.Position(n)), false);
                        g.LineTo(Map(graph.Position(to)));
                        g.EndFigure(false);
                    }
                }
            }
            layer.Taxi = taxi;
            var byWidth = new Dictionary<double, StreamGeometry>();
            var contexts = new Dictionary<double, StreamGeometryContext>();
            var pointCount = a.TaxiPoints.Count;
            foreach (var path in a.TaxiPaths)
            {
                if (path.Type is not (TaxiPathType.Taxi or TaxiPathType.Path or TaxiPathType.Parking))
                    continue;
                var to = path.Type == TaxiPathType.Parking ? pointCount + path.End : path.End;
                if (path.Start < 0 || path.Start >= pointCount || to < 0 || to >= graph.NodeCount)
                    continue;
                var width = Math.Round(Math.Clamp(path.WidthM > 1 ? path.WidthM : 15, 4, 80) / 2) * 2;
                if (!contexts.TryGetValue(width, out var ctxw))
                {
                    var geo = new StreamGeometry();
                    byWidth[width] = geo;
                    contexts[width] = ctxw = geo.Open();
                }
                ctxw.BeginFigure(Map(graph.Position(path.Start)), false);
                ctxw.LineTo(Map(graph.Position(to)));
                ctxw.EndFigure(false);
            }
            foreach (var c in contexts.Values)
                c.Dispose();
            layer.Pavement = [.. byWidth.OrderByDescending(kv => kv.Key).Select(kv => (kv.Key, kv.Value))];
            layer.Pens = [.. layer.Pavement.Select(pv => (IPen)new ImmutablePen(PavementBrush, pv.WidthM, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round))];
            var runways = new StreamGeometry();
            var labels = new List<(Point, FormattedText)>();
            using (var g = runways.Open())
            {
                foreach (var r in a.Runways)
                {
                    var p1 = ToWorld(r.Primary.Position);
                    var p2 = ToWorld(r.Secondary.Position);
                    var along = p2 - p1;
                    var len = Math.Sqrt(along.X * along.X + along.Y * along.Y);
                    if (len < 1)
                        continue;
                    var side = new Vector(-along.Y / len, along.X / len) * (r.WidthM / 2);
                    g.BeginFigure(p1 + side, true);
                    g.LineTo(p2 + side);
                    g.LineTo(p2 - side);
                    g.LineTo(p1 - side);
                    g.EndFigure(true);
                    var unit = along / len;
                    labels.Add((p1 - unit * 120, Text(r.Primary.Ident, TextBrush)));
                    labels.Add((p2 + unit * 120, Text(r.Secondary.Ident, TextBrush)));
                }
            }
            layer.Runways = runways;
            layer.RunwayLabels = [.. labels];

            // Lo mismo que el aeropuerto principal: puestos con nombre, nombres de calles y puntos de espera.
            layer.Stands = [.. a.Parkings.Where(p => p.Type != ParkingType.Vehicle).Select(p =>
                (ToWorld(p.Position), p.RadiusM, new FormattedText(StandName(p), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, StandBrush)))];
            var taxiLabels = new List<(Point Pos, string Name)>();
            for (var n = 0; n < graph.NodeCount; n++)
            {
                foreach (var (to, type, name) in graph.Neighbors(n))
                {
                    if (to < n || name.Length == 0 || type is TaxiPathType.Runway or TaxiPathType.Parking)
                        continue;
                    var pa = graph.Position(n);
                    var pb = graph.Position(to);
                    if (LocalPoint.Distance(pa, pb) < 40)
                        continue;
                    var mid = Map(new LocalPoint((pa.EastM + pb.EastM) / 2, (pa.NorthM + pb.NorthM) / 2));
                    var close = false;
                    foreach (var (pos, other) in taxiLabels)
                    {
                        if (other == name && Math.Abs(pos.X - mid.X) + Math.Abs(pos.Y - mid.Y) < TaxiNameSpacingM)
                        {
                            close = true;
                            break;
                        }
                    }
                    if (!close)
                        taxiLabels.Add((mid, name));
                }
            }
            layer.TaxiLabels = [.. taxiLabels.Select(l => (l.Pos, new FormattedText(l.Name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 10, TaxiNameBrush)))];
            layer.Holds = [.. graph.RunwayEntries.Select(e => (Map(graph.Position(e.HoldNode)), e.HoldIsMarked)).Distinct()];
            layers.Add(layer);
        }
        _others = [.. layers];
        _dirty = true;
    }

    /// <summary>Costas y fronteras cercanas, en metros locales (una vez por aeropuerto).</summary>
    private void BuildWorldMap(GeoPoint reference)
    {
        if (World.Value is not { } world)
            return;
        _coastGeometry = Lines(WorldMap.Near(world.Coast, reference, NearMapRadiusM));
        _borderGeometry = Lines(WorldMap.Near(world.Borders, reference, NearMapRadiusM));
        _coastFar = Lines(Simplify(WorldMap.Near(world.Coast, reference, WorldMapRadiusM), 15_000));
        _borderFar = Lines(Simplify(WorldMap.Near(world.Borders, reference, WorldMapRadiusM), 15_000));
    }

    /// <summary>Deja un punto cada <paramref name="minM"/> metros (y los extremos de cada línea).</summary>
    private static List<List<GeoPoint>> Simplify(List<List<GeoPoint>> lines, double minM)
    {
        var result = new List<List<GeoPoint>>(lines.Count);
        foreach (var line in lines)
        {
            var kept = new List<GeoPoint> { line[0] };
            for (var i = 1; i < line.Count - 1; i++)
            {
                if (GeoMath.DistanceM(kept[^1], line[i]) >= minM)
                    kept.Add(line[i]);
            }
            if (line.Count > 1)
                kept.Add(line[^1]);
            if (kept.Count > 1 && (kept.Count > 2 || GeoMath.DistanceM(kept[0], kept[^1]) >= minM))
                result.Add(kept);
        }
        return result;
    }

    private StreamGeometry Lines(List<List<GeoPoint>> lines)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        foreach (var line in lines)
        {
            g.BeginFigure(ToWorld(line[0]), false);
            for (var i = 1; i < line.Count; i++)
            {
                // Al cruzar el antimeridiano (180°) la línea se corta (si no, cruzaría todo el mapa).
                if (Math.Abs(line[i].LonDeg - line[i - 1].LonDeg) > 180)
                {
                    g.EndFigure(false);
                    g.BeginFigure(ToWorld(line[i]), false);
                    continue;
                }
                g.LineTo(ToWorld(line[i]));
            }
            g.EndFigure(false);
        }
        return geo;
    }

    /// <summary>Nombre legible de un puesto: "Gate 16", "Gate C4", "Parking 58".</summary>
    public static string StandName(Parking p) => p.DisplayName;

    private void Fit()
    {
        if (_taxiGeometry is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var box = _taxiGeometry.Bounds.Union(_runwayGeometry?.Bounds ?? default);
        _center = box.Center;
        _scale = 0.92 * Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
        _fitPending = false;
        _dirty = true;
    }

    /// <summary>Encuadra toda la ruta del plan (botón "Ver ruta" del radar).</summary>
    public void FitPlan()
    {
        if (PlanRoute is not { Count: > 1 } plan || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var first = ToWorld(plan[0].Position);
        var box = new Rect(first, first);
        foreach (var p in plan)
            box = box.Union(new Rect(ToWorld(p.Position), new Size(1, 1)));
        _center = box.Center;
        _scale = Math.Clamp(0.9 * Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height), MinScale, MaxScale);
        _dirty = true;
    }

    /// <summary>Encuadra los procedimientos de esos tipos (vista previa del plan); si no hay, la ruta completa.</summary>
    public void FitProcedures(params ProcedureKind[] kinds)
    {
        if (Procedures is not { Count: > 0 } procs || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            FitPlan();
            return;
        }
        Rect? box = null;
        foreach (var line in procs)
        {
            if (Array.IndexOf(kinds, line.Kind) < 0)
                continue;
            foreach (var seg in line.Shape.Segments)
            {
                foreach (var p in seg.Points)
                {
                    var r = new Rect(ToWorld(p), new Size(1, 1));
                    box = box is { } b ? b.Union(r) : r;
                }
            }
        }
        if (box is not { } fit)
        {
            FitPlan();
            return;
        }
        fit = fit.Inflate(Math.Max(fit.Width, fit.Height) * 0.1 + 500);
        _center = fit.Center;
        _scale = Math.Clamp(0.9 * Math.Min(Bounds.Width / fit.Width, Bounds.Height / fit.Height), MinScale, MaxScale);
        _fitPending = false;
        _dirty = true;
    }

    private void CenterOnUser()
    {
        if (UserState is not { } user)
            return;
        _center = ToWorld(user.Position);
        _dirty = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var mouse = e.GetPosition(this);
        var before = ToWorldFromScreen(mouse);
        _scale = Math.Clamp(_scale * Math.Pow(1.2, e.Delta.Y), MinScale, MaxScale);
        OnNavZoom();
        var after = ToWorldFromScreen(mouse);
        _center += before - after; // el punto bajo el cursor queda fijo
        RedrawNow();
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragLast = _pressAt = e.GetPosition(this);
        _dragged = false;
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLast is not { } last)
            return;
        var now = e.GetPosition(this);
        if (!_dragged && Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < ClickSlopPx)
            return;
        _dragged = true;
        FollowSelected = false; // al arrastrar a mano se deja de seguir
        _center -= (now - last) / _scale;
        _dragLast = now;
        RedrawNow();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragLast is not null && !_dragged)
        {
            var picked = Pick(e.GetPosition(this));
            SelectedId = picked;
            Picked?.Invoke(picked);
        }
        _dragLast = null;
        e.Pointer.Capture(null);
    }

    /// <summary>El avión más cercano al clic (dentro de <see cref="PickRadiusPx"/>), o null.</summary>
    private uint? Pick(Point screen)
    {
        uint? best = null;
        var bestD = PickRadiusPx;
        if (UserState is { } me)
        {
            var u = ToScreen(ToWorld(me.Position));
            var du = Math.Sqrt((u.X - screen.X) * (u.X - screen.X) + (u.Y - screen.Y) * (u.Y - screen.Y));
            if (du < bestD)
            {
                bestD = du;
                best = UserId;
            }
        }
        foreach (var t in Traffic ?? [])
        {
            if (t.Id == 0)
                continue;
            var p = ToScreen(ToWorld(t.Position));
            var d = Math.Sqrt((p.X - screen.X) * (p.X - screen.X) + (p.Y - screen.Y) * (p.Y - screen.Y));
            if (d < bestD)
            {
                bestD = d;
                best = t.Id;
            }
        }
        return best;
    }

    private Matrix WorldToScreen() =>
        Matrix.CreateTranslation(-_center.X, -_center.Y) *
        Matrix.CreateScale(_scale, _scale) *
        Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);

    private Point ToScreen(Point world) =>
        new((world.X - _center.X) * _scale + Bounds.Width / 2, (world.Y - _center.Y) * _scale + Bounds.Height / 2);

    private Point ToWorldFromScreen(Point screen) =>
        new((screen.X - Bounds.Width / 2) / _scale + _center.X, (screen.Y - Bounds.Height / 2) / _scale + _center.Y);

    /// <summary>
    /// Del mundo al plano del radar: Mercator centrada en el aeropuerto (conforme, ADR-026), y = norte hacia arriba.
    /// Todas las capas pasan por aquí: aeropuertos, procedimientos, plan, mapa base y tráfico.
    /// </summary>
    private Point ToWorld(GeoPoint p)
    {
        var m = _mercator.ToPlane(p);
        return new Point(m.EastM, -m.NorthM);
    }

    /// <summary>Un punto del grafo del aeropuerto principal (en su proyección local) al plano del radar.</summary>
    private Point ToPoint(LocalPoint p) => ToWorld(_projection.ToGeo(p));

    private static FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush);
}
