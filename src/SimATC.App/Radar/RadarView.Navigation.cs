using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SimATC.Core.Geo;
using SimATC.Core.World;

namespace SimATC.App.Radar;

/// <summary>
/// Radar global (ADR-062, ADR-063): capas de navegación del simulador en cualquier parte del mundo, con filtros. Fijos, VOR
/// y NDB salen del índice mundial (<c>RequestAllFacilities</c>); las aerovías, de los fijos ya pedidos al simulador (crecen al
/// mirar una zona y se guardan); los aeropuertos, de la base mundial.
/// <para>Rendimiento: todo se arma una vez por nivel de zoom para un recuadro el doble de grande que la pantalla, en
/// coordenadas del mapa, y se dibuja con una transformación: arrastrar el mapa no recalcula nada. Se rearma al cambiar el
/// zoom, al salir del recuadro, al cambiar un filtro o al llegar aerovías nuevas. Los nombres se ubican con una grilla de
/// colisión en pantalla (nunca se pisan) y cada aerovía lleva su nombre como mucho una vez cada ~250 px.</para>
/// </summary>
public sealed partial class RadarView
{
    public static readonly StyledProperty<NavIndex?> NavProperty = AvaloniaProperty.Register<RadarView, NavIndex?>(nameof(Nav));
    public static readonly StyledProperty<AirwayDatabase?> AirwaysProperty = AvaloniaProperty.Register<RadarView, AirwayDatabase?>(nameof(Airways));
    public static readonly StyledProperty<WorldAirports?> WorldAirportsProperty =
        AvaloniaProperty.Register<RadarView, WorldAirports?>(nameof(WorldAirports));
    public static readonly StyledProperty<bool> ShowFixesProperty = AvaloniaProperty.Register<RadarView, bool>(nameof(ShowFixes));
    public static readonly StyledProperty<bool> ShowNavaidsProperty = AvaloniaProperty.Register<RadarView, bool>(nameof(ShowNavaids), true);
    public static readonly StyledProperty<bool> ShowAirwaysProperty = AvaloniaProperty.Register<RadarView, bool>(nameof(ShowAirways));
    public static readonly StyledProperty<bool> ShowAirportsProperty = AvaloniaProperty.Register<RadarView, bool>(nameof(ShowAirports), true);

    public NavIndex? Nav
    {
        get => GetValue(NavProperty);
        set => SetValue(NavProperty, value);
    }

    public AirwayDatabase? Airways
    {
        get => GetValue(AirwaysProperty);
        set => SetValue(AirwaysProperty, value);
    }

    public WorldAirports? WorldAirports
    {
        get => GetValue(WorldAirportsProperty);
        set => SetValue(WorldAirportsProperty, value);
    }

    public bool ShowFixes
    {
        get => GetValue(ShowFixesProperty);
        set => SetValue(ShowFixesProperty, value);
    }

    public bool ShowNavaids
    {
        get => GetValue(ShowNavaidsProperty);
        set => SetValue(ShowNavaidsProperty, value);
    }

    public bool ShowAirways
    {
        get => GetValue(ShowAirwaysProperty);
        set => SetValue(ShowAirwaysProperty, value);
    }

    public bool ShowAirports
    {
        get => GetValue(ShowAirportsProperty);
        set => SetValue(ShowAirportsProperty, value);
    }

    /// <summary>Lo que se ve cambió (recuadro en grados: sur, norte, oeste, este), para pedir las aerovías de esa zona.</summary>
    public event Action<double, double, double, double>? ViewChanged;

    private static readonly IImmutableSolidColorBrush NavFixBrush = new ImmutableSolidColorBrush(0xFF6F8299);
    private static readonly IImmutableSolidColorBrush VorBrush = new ImmutableSolidColorBrush(0xFF5FB3D9);
    private static readonly IImmutableSolidColorBrush NdbBrush = new ImmutableSolidColorBrush(0xFFB38FD9);
    private static readonly IImmutableSolidColorBrush AirwayHighBrush = new ImmutableSolidColorBrush(0xB04A7FB0);
    private static readonly IImmutableSolidColorBrush AirwayLowBrush = new ImmutableSolidColorBrush(0xB0679466);
    private static readonly IImmutableSolidColorBrush AirwayLabelBrush = new ImmutableSolidColorBrush(0xFF7A95B5);
    private static readonly IImmutableSolidColorBrush AirwayLabelBack = new ImmutableSolidColorBrush(0xD90E1621);
    private static readonly IImmutableSolidColorBrush WorldAirportBrush = new ImmutableSolidColorBrush(0xFF8FA3BB);

    /// <summary>Escalas (px/m) desde las que se dibuja cada capa y sus nombres. 1000 px de ancho: 0.0005 ≈ 2000 km.</summary>
    private const double NavaidMinScale = 0.0004;
    private const double FixMinScale = 0.0015;
    private const double NavLabelMinScale = 0.003;
    private const double FixLabelMinScale = 0.006;
    private const double AirwayMinScale = 0.0006;
    private const double LowAirwayMinScale = 0.002;
    private const double AirwayLabelMinScale = 0.003;
    private const double WorldAirportLabelMinScale = 0.0008;
    private const int MaxNavPoints = 3000;

    /// <summary>Una etiqueta ya ubicada: posición en el mapa, desplazamiento en pantalla y su texto.</summary>
    private readonly record struct NavLabel(Point World, Vector Offset, FormattedText Text, bool Boxed);

    /// <summary>Lo armado para un zoom y un recuadro.</summary>
    private sealed class NavLayer
    {
        public double Scale;
        public (double S, double N, double W, double E) Box;
        public StreamGeometry? Fixes, Vors, Ndbs, AirportDots, High, Low;
        public List<NavLabel> Labels = [];
        public int SegmentsVersion = -1;
        public int Flags;
        public NavIndex? Nav;
        public WorldAirports? AirportsRef;
    }

    private NavLayer? _navLayer;
    private readonly List<NavFix> _navBuffer = [];
    private readonly Dictionary<(string, int), FormattedText> _textCache = [];
    private (double S, double N, double W, double E) _lastBox;
    private double _airwayPenScale = double.NaN;
    private IPen? _highPen, _lowPen, _vorPen, _ndbPen;
    // Índice de tramos de aerovía (se arma en segundo plano cada 5 s si llegaron fijos nuevos).
    private AirwaySegments? _segments;
    private int _segmentsVersion;
    private int _segmentsFixCount = -1;
    private bool _segmentsBuilding;
    private DateTime _segmentsBuiltAt;
    private readonly List<AirwaySegment> _segmentBuffer = [];

    /// <summary>Hasta cuándo se espera para rearmar las capas tras un zoom (mientras tanto se escalan).</summary>
    private DateTime? _navRebuildAt;

    /// <summary>Lo llama la rueda del ratón: las capas se rearman cuando el zoom se detiene.</summary>
    private void OnNavZoom() => _navRebuildAt = DateTime.UtcNow.AddMilliseconds(250);

    private void RefreshSegments(AirwayDatabase db)
    {
        if (_segmentsBuilding || db.Count == _segmentsFixCount || (DateTime.UtcNow - _segmentsBuiltAt).TotalSeconds < 5)
            return;
        _segmentsBuilding = true;
        var count = db.Count;
        Task.Run(() =>
        {
            var fixes = new List<AirwayFix>();
            db.Completed(fixes);
            return AirwaySegments.Build(fixes);
        }).ContinueWith(t =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _segmentsBuilding = false;
                _segmentsBuiltAt = DateTime.UtcNow;
                if (t.IsCompletedSuccessfully)
                {
                    _segments = t.Result;
                    _segmentsFixCount = count;
                    _segmentsVersion++;
                    _dirty = true;
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>El recuadro visible en grados (sur, norte, oeste, este).</summary>
    private (double S, double N, double W, double E) VisibleBox()
    {
        var a = _mercator.ToGeo(PlaneOf(ToWorldFromScreen(new Point(0, 0))));
        var b = _mercator.ToGeo(PlaneOf(ToWorldFromScreen(new Point(Bounds.Width, Bounds.Height))));
        return (Math.Max(-85, b.LatDeg), Math.Min(85, a.LatDeg), a.LonDeg, b.LonDeg);
    }

    private static LocalPoint PlaneOf(Point world) => new(world.X, -world.Y);

    private int LayerFlags => (ShowFixes ? 1 : 0) | (ShowNavaids ? 2 : 0) | (ShowAirways ? 4 : 0) | (ShowAirports ? 8 : 0);

    private void DrawNavigation(DrawingContext ctx)
    {
        if (Bounds.Width < 1)
            return;
        var box = VisibleBox();
        if (box != _lastBox)
        {
            _lastBox = box;
            ViewChanged?.Invoke(box.S, box.N, box.W, box.E);
        }
        if (LayerFlags == 0)
            return;
        var layer = _navLayer;
        if (ShowAirways && Airways is { } db)
            RefreshSegments(db);
        var mustRebuild = layer is null || layer.Flags != LayerFlags || layer.Nav != Nav || layer.AirportsRef != WorldAirports
                          || !Inside(box, layer.Box) || ShowAirways && layer.SegmentsVersion != _segmentsVersion;
        // Durante el zoom se reutiliza lo armado (escalado) si no cambió más de 2 veces; se rearma al detenerse.
        if (!mustRebuild && layer!.Scale != _scale)
        {
            var ratio = _scale / layer.Scale;
            mustRebuild = _navRebuildAt is null || ratio < 0.5 || ratio > 2;
        }
        if (mustRebuild)
            layer = _navLayer = BuildNavLayer(box);

        if (_airwayPenScale != _scale)
        {
            _airwayPenScale = _scale;
            _highPen = new ImmutablePen(AirwayHighBrush, 1.0 / _scale);
            _lowPen = new ImmutablePen(AirwayLowBrush, 1.0 / _scale, new ImmutableDashStyle([5, 3], 0));
            _vorPen = new ImmutablePen(VorBrush, 1.2 / _scale);
            _ndbPen = new ImmutablePen(NdbBrush, 1.2 / _scale);
        }
        using (ctx.PushTransform(WorldToScreen()))
        {
            if (layer!.Low is not null)
                ctx.DrawGeometry(null, _lowPen, layer.Low);
            if (layer.High is not null)
                ctx.DrawGeometry(null, _highPen, layer.High);
            if (layer.Fixes is not null)
                ctx.DrawGeometry(NavFixBrush, null, layer.Fixes);
            if (layer.Vors is not null)
                ctx.DrawGeometry(null, _vorPen, layer.Vors);
            if (layer.Ndbs is not null)
                ctx.DrawGeometry(null, _ndbPen, layer.Ndbs);
            if (layer.AirportDots is not null)
                ctx.DrawGeometry(WorldAirportBrush, null, layer.AirportDots);
        }
        var bounds = new Rect(Bounds.Size).Inflate(40);
        foreach (var l in layer.Labels)
        {
            var p = ToScreen(l.World) + l.Offset;
            if (!bounds.Contains(p))
                continue;
            if (l.Boxed)
                ctx.DrawRectangle(AirwayLabelBack, null, new Rect(p.X - 2, p.Y - 1, l.Text.Width + 4, l.Text.Height + 2));
            ctx.DrawText(l.Text, p);
        }
    }

    private static bool Inside((double S, double N, double W, double E) view, (double S, double N, double W, double E) cached) =>
        view.S >= cached.S && view.N <= cached.N && view.W >= cached.W && view.E <= cached.E;

    private FormattedText CachedText(string text, int kind, IBrush brush, double size)
    {
        if (_textCache.Count > 20_000)
            _textCache.Clear();
        if (!_textCache.TryGetValue((text, kind), out var t))
            _textCache[(text, kind)] = t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        return t;
    }

    /// <summary>Grilla de colisión de etiquetas en pantalla (celdas de 8 px), para el zoom de esta capa.</summary>
    private sealed class LabelGrid(double scale, Point origin)
    {
        private readonly HashSet<(int, int)> _used = [];

        /// <summary>Más allá de esto (px desde el centro) un nombre no se coloca: está fuera de cualquier pantalla.</summary>
        private const double LabelRangePx = 100_000;

        public bool TryPlace(Point world, Vector offset, double width, double height)
        {
            var x0 = (world.X - origin.X) * scale + offset.X;
            var y0 = (world.Y - origin.Y) * scale + offset.Y;
            // Muy lejos de la pantalla (el medio de una aerovía larga con mucho zoom, prueba de Marco 2026-11-09): la celda
            // se saturaba a int.MaxValue y "c <= c1" no terminaba nunca (la app quedaba congelada). No se muestra.
            if (!(Math.Abs(x0) < LabelRangePx && Math.Abs(y0) < LabelRangePx && width < LabelRangePx && height < LabelRangePx))
                return false;
            int c0 = (int)Math.Floor(x0 / 8), c1 = (int)Math.Floor((x0 + width) / 8);
            int r0 = (int)Math.Floor(y0 / 8), r1 = (int)Math.Floor((y0 + height) / 8);
            for (var c = c0; c <= c1; c++)
            {
                for (var r = r0; r <= r1; r++)
                {
                    if (_used.Contains((c, r)))
                        return false;
                }
            }
            for (var c = c0; c <= c1; c++)
            {
                for (var r = r0; r <= r1; r++)
                    _used.Add((c, r));
            }
            return true;
        }

        /// <summary>Marca un punto (el símbolo) como ocupado.</summary>
        public void Reserve(Point world, double radiusPx) => TryPlace(world, new Vector(-radiusPx, -radiusPx), 2 * radiusPx, 2 * radiusPx);
    }

    private NavLayer BuildNavLayer((double S, double N, double W, double E) view)
    {
        // El doble de la pantalla: se puede arrastrar sin rearmar.
        var dLat = (view.N - view.S) / 2;
        var dLon = (view.E - view.W) / 2;
        var box = (S: Math.Max(-85, view.S - dLat), N: Math.Min(85, view.N + dLat), W: view.W - dLon, E: view.E + dLon);
        var layer = new NavLayer { Scale = _scale, Box = box, Flags = LayerFlags, Nav = Nav, AirportsRef = WorldAirports };
        var grid = new LabelGrid(_scale, _center);
        var px = 1.0 / _scale; // un píxel en metros del mapa

        // Aeropuertos primero: sus nombres tienen prioridad.
        if (ShowAirports && WorldAirports is { } airports)
        {
            var center = new GeoPoint((box.S + box.N) / 2, (box.W + box.E) / 2);
            var list = airports.Within(center, GeoMath.DistanceM(center, new GeoPoint(box.N, box.E)));
            var dots = new StreamGeometry();
            using (var g = dots.Open())
            {
                foreach (var a in list)
                {
                    if (a.Icao == Airport?.Icao)
                        continue;
                    var w = ToWorld(a.Position);
                    Diamond(g, w, 2.5 * px);
                    grid.Reserve(w, 3);
                    if (_scale >= WorldAirportLabelMinScale)
                    {
                        var t = CachedText(a.Icao, 0, WorldAirportBrush, 10);
                        var off = new Vector(4, 1);
                        if (grid.TryPlace(w, off, t.Width, t.Height))
                            layer.Labels.Add(new NavLabel(w, off, t, false));
                    }
                }
            }
            layer.AirportDots = dots;
        }

        // Aerovías: del índice de tramos (cada tramo una vez), altas y bajas por separado; de lejos, solo las altas.
        if (ShowAirways && _scale >= AirwayMinScale && _segments is { } segments)
        {
            layer.SegmentsVersion = _segmentsVersion;
            segments.InBox(box.S, box.N, box.W, box.E, _segmentBuffer);
            var high = new StreamGeometry();
            var low = new StreamGeometry();
            var labelled = new Dictionary<string, List<Point>>();
            var showLow = _scale >= LowAirwayMinScale;
            using (var gh = high.Open())
            using (var gl = low.Open())
            {
                foreach (var seg in _segmentBuffer)
                {
                    var isLow = seg.Level == 1;
                    if (isLow && !showLow)
                        continue;
                    var a = ToWorld(seg.From);
                    var b = ToWorld(seg.To);
                    var g = isLow ? gl : gh;
                    g.BeginFigure(a, false);
                    g.LineTo(b);
                    g.EndFigure(false);
                    if (_scale < AirwayLabelMinScale)
                        continue;
                    var lengthPx = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) * _scale;
                    if (lengthPx < 90)
                        continue;
                    var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    // Una sola vez cada ~250 px por aerovía.
                    if (labelled.TryGetValue(seg.Airway, out var placed)
                        && placed.Any(p => Math.Abs(p.X - mid.X) * _scale < 250 && Math.Abs(p.Y - mid.Y) * _scale < 250))
                        continue;
                    var t = CachedText(seg.Airway, isLow ? 2 : 1, AirwayLabelBrush, 9);
                    var off = new Vector(-t.Width / 2, -t.Height / 2);
                    if (!grid.TryPlace(mid, off, t.Width, t.Height))
                        continue;
                    (labelled.TryGetValue(seg.Airway, out var ps) ? ps : labelled[seg.Airway] = []).Add(mid);
                    layer.Labels.Add(new NavLabel(mid, off, t, true));
                }
            }
            layer.High = high;
            layer.Low = showLow ? low : null;
        }

        if (Nav is { } nav)
        {
            // Radioayudas.
            if (ShowNavaids && _scale >= NavaidMinScale)
            {
                nav.InBox(box.S, box.N, box.W, box.E, _navBuffer, MaxNavPoints, null, navaids: true);
                var vors = new StreamGeometry();
                var ndbs = new StreamGeometry();
                using (var gv = vors.Open())
                using (var gn = ndbs.Open())
                {
                    foreach (var f in _navBuffer)
                    {
                        var w = ToWorld(f.Position);
                        if (f.Kind == NavKind.Vor)
                            Hexagon(gv, w, 4.5 * px);
                        else
                            Octagon(gn, w, 4 * px);
                        grid.Reserve(w, 5);
                    }
                }
                layer.Vors = vors;
                layer.Ndbs = ndbs;
                if (_scale >= NavLabelMinScale)
                {
                    foreach (var f in _navBuffer)
                    {
                        var w = ToWorld(f.Position);
                        var t = CachedText(f.Ident, f.Kind == NavKind.Vor ? 3 : 4, f.Kind == NavKind.Vor ? VorBrush : NdbBrush, 10);
                        var off = new Vector(6, -6);
                        if (grid.TryPlace(w, off, t.Width, t.Height))
                            layer.Labels.Add(new NavLabel(w, off, t, false));
                    }
                }
            }
            // Waypoints (de cerca).
            if (ShowFixes && _scale >= FixMinScale)
            {
                nav.InBox(box.S, box.N, box.W, box.E, _navBuffer, MaxNavPoints, NavKind.Waypoint);
                var fixes = new StreamGeometry();
                using (var g = fixes.Open())
                {
                    foreach (var f in _navBuffer)
                    {
                        var w = ToWorld(f.Position);
                        Triangle(g, w, 3 * px);
                        grid.Reserve(w, 3);
                    }
                }
                layer.Fixes = fixes;
                if (_scale >= FixLabelMinScale)
                {
                    foreach (var f in _navBuffer)
                    {
                        // Los terminales ("EL070", "DGO22") solo bien de cerca.
                        if (f.Ident.Length != 5 && _scale < FixLabelMinScale * 3)
                            continue;
                        var w = ToWorld(f.Position);
                        var t = CachedText(f.Ident, 5, NavFixBrush, 9.5);
                        var off = new Vector(5, -5);
                        if (grid.TryPlace(w, off, t.Width, t.Height))
                            layer.Labels.Add(new NavLabel(w, off, t, false));
                    }
                }
            }
        }
        return layer;
    }

    private static void Triangle(StreamGeometryContext g, Point c, double r)
    {
        g.BeginFigure(new Point(c.X, c.Y - r), true);
        g.LineTo(new Point(c.X + r * 0.87, c.Y + r * 0.5));
        g.LineTo(new Point(c.X - r * 0.87, c.Y + r * 0.5));
        g.EndFigure(true);
    }

    private static void Diamond(StreamGeometryContext g, Point c, double r)
    {
        g.BeginFigure(new Point(c.X, c.Y - r), true);
        g.LineTo(new Point(c.X + r, c.Y));
        g.LineTo(new Point(c.X, c.Y + r));
        g.LineTo(new Point(c.X - r, c.Y));
        g.EndFigure(true);
    }

    private static void Hexagon(StreamGeometryContext g, Point c, double r) => Polygon(g, c, r, 6, 0);

    private static void Octagon(StreamGeometryContext g, Point c, double r) => Polygon(g, c, r, 8, Math.PI / 8);

    private static void Polygon(StreamGeometryContext g, Point c, double r, int sides, double start)
    {
        g.BeginFigure(new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start)), false);
        for (var i = 1; i < sides; i++)
        {
            var a = start + i * 2 * Math.PI / sides;
            g.LineTo(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
        }
        g.EndFigure(true);
    }
}
