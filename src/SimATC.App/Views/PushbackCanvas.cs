using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimATC.App.ViewModels;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.App.Views;

/// <summary>
/// Dibujo del planificador de pushback (RF-21c), con el norte arriba: calles y pistas cercanas, puestos, la línea del
/// pushback (verde: se puede; amarillo: termina lejos de una calle; rojo: no se puede), el avión al inicio (contorno),
/// cómo quedará al final y, durante el pushback, dónde está ahora. Con el mouse: arrastrar el avión final lo mueve,
/// la rueda (o arrastrar con el botón derecho) lo gira, Ctrl + rueda acerca o aleja, y arrastrar con la rueda apretada
/// desplaza el mapa. La vista se ajusta sola al abrir y con <see cref="Recenter"/>; después la maneja el piloto.
/// </summary>
public sealed class PushbackCanvas : Control
{
    /// <summary>Silueta genérica de un jet mediano (m; x a la derecha, y hacia la nariz). Solo para orientarse.</summary>
    private static readonly (double X, double Y)[] Silhouette =
    [
        (0, 19), (2, 15), (2, 4), (17, -2), (17, -4), (2, -1), (2, -12), (6, -17), (6, -19), (0, -18),
        (-6, -19), (-6, -17), (-2, -12), (-2, -1), (-17, -4), (-17, -2), (-2, 4), (-2, 15),
    ];

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B111A"));
    private static readonly IPen TaxiPen = new Pen(new SolidColorBrush(Color.Parse("#243142")), 1, lineCap: PenLineCap.Round);
    private static readonly IPen TaxiLine = new Pen(new SolidColorBrush(Color.Parse("#B8962E")), 1);
    private static readonly IBrush RunwayBrush = new SolidColorBrush(Color.Parse("#3A4250"));
    private static readonly IBrush StandBrush = new SolidColorBrush(Color.Parse("#4A5566"));
    private static readonly IPen StartPen = new Pen(new SolidColorBrush(Color.Parse("#C9D4E0")), 1.5);
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4FB3FF"));
    private static readonly Color Ok = Color.Parse("#7BE3A6");
    private static readonly Color Warn = Color.Parse("#F5A524");
    private static readonly Color Bad = Color.Parse("#FF5C5C");
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#8A97A8"));

    public static readonly StyledProperty<PushbackViewModel?> ModelProperty =
        AvaloniaProperty.Register<PushbackCanvas, PushbackViewModel?>(nameof(Model));

    public PushbackViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Distancia (m) al avión final para "agarrarlo" con el mouse.</summary>
    private const double GrabRadiusM = 25;

    /// <summary>Paso de giro con la rueda (°).</summary>
    private const double WheelStepDeg = 5;

    private (double Cx, double Cy, double Scale) _view;
    private bool _fitPending = true;
    private bool _panning;
    private Point _panFrom;
    private bool _dragging;
    private bool _rotating;
    private Point _rotateFrom;
    private double _rotateStartHeading;

    public PushbackCanvas()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private LocalPoint ToLocal(Point p) =>
        new(_view.Cx + (p.X - Bounds.Width / 2) / _view.Scale, _view.Cy - (p.Y - Bounds.Height / 2) / _view.Scale);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var at = e.GetPosition(this);
        if (props.IsMiddleButtonPressed && _view.Scale > 0)
        {
            _panning = true;
            _panFrom = at;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (Model is not { CanEdit: true } vm || _view.Scale <= 0)
            return;
        if (props.IsRightButtonPressed)
        {
            _rotating = true;
            _rotateFrom = at;
            _rotateStartHeading = vm.Plan.EndHeadingDeg;
        }
        else if (props.IsLeftButtonPressed)
        {
            // Agarrar el avión final (o, si se hace clic en otro lugar, llevarlo ahí).
            _dragging = true;
            if (LocalPoint.Distance(ToLocal(at), vm.Plan.Path.End) > GrabRadiusM)
                vm.PlaceEnd(ToLocal(at), vm.Plan.EndHeadingDeg);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_panning)
        {
            _view = (_view.Cx - (at.X - _panFrom.X) / _view.Scale, _view.Cy + (at.Y - _panFrom.Y) / _view.Scale, _view.Scale);
            _panFrom = at;
            InvalidateVisual();
            return;
        }
        if (Model is not { } vm)
            return;
        if (_dragging)
            vm.PlaceEnd(ToLocal(at), vm.Plan.EndHeadingDeg);
        else if (_rotating)
            vm.PlaceEnd(vm.Plan.Path.End, GeoMath.NormalizeDeg(_rotateStartHeading + (at.X - _rotateFrom.X) * 0.5));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _rotating = false;
        _panning = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ZoomBy(e.Delta.Y > 0 ? 1.25 : 0.8, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (Model is not { CanEdit: true } vm)
            return;
        vm.PlaceEnd(vm.Plan.Path.End, GeoMath.NormalizeDeg(vm.Plan.EndHeadingDeg + Math.Sign(e.Delta.Y) * WheelStepDeg));
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ModelProperty)
            return;
        if (change.OldValue is PushbackViewModel old)
            old.PropertyChanged -= OnModelChanged;
        if (change.NewValue is PushbackViewModel vm)
            vm.PropertyChanged += OnModelChanged;
        _fitPending = true;
        InvalidateVisual();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>Acerca (factor > 1) o aleja la vista, dejando quieto el punto <paramref name="around"/> (o el centro).</summary>
    public void ZoomBy(double factor, Point? around = null)
    {
        if (_view.Scale <= 0)
            return;
        var at = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var fixedPoint = ToLocal(at);
        var scale = Math.Clamp(_view.Scale * factor, 0.3, 40);
        _view = (fixedPoint.EastM - (at.X - Bounds.Width / 2) / scale, fixedPoint.NorthM + (at.Y - Bounds.Height / 2) / scale, scale);
        InvalidateVisual();
    }

    /// <summary>Vuelve a encuadrar el avión, la línea y el final.</summary>
    public void Recenter()
    {
        _fitPending = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Background, new Rect(0, 0, w, h));
        if (Model is not { } vm || w < 10 || h < 10)
            return;

        // Vista: el inicio, la línea y el final, con margen; al menos 160 m.
        var pts = vm.Plan.Path.Points;
        double minE = vm.Start.EastM, maxE = minE, minN = vm.Start.NorthM, maxN = minN;
        foreach (var p in pts)
        {
            minE = Math.Min(minE, p.EastM);
            maxE = Math.Max(maxE, p.EastM);
            minN = Math.Min(minN, p.NorthM);
            maxN = Math.Max(maxN, p.NorthM);
        }
        // Encuadre automático solo al abrir y al pedirlo ("Centrar"): después la vista es del piloto.
        if (_fitPending || _view.Scale <= 0)
        {
            _fitPending = false;
            var span = Math.Max(160, Math.Max(maxE - minE, maxN - minN) + 70);
            _view = ((minE + maxE) / 2, (minN + maxN) / 2, Math.Min(w, h) / span);
        }
        var (cx, cy, scale) = _view;
        Point P(LocalPoint p) => new(w / 2 + (p.EastM - cx) * scale, h / 2 - (p.NorthM - cy) * scale);

        // Pistas y calles.
        foreach (var s in vm.Map)
        {
            if (s.IsRunway)
                ctx.DrawLine(new Pen(RunwayBrush, Math.Max(2, s.WidthM * scale)), P(s.A), P(s.B));
        }
        var taxiWidth = new Pen(TaxiPen.Brush, Math.Max(3, 18 * scale), lineCap: PenLineCap.Round);
        foreach (var s in vm.Map)
        {
            if (!s.IsRunway)
                ctx.DrawLine(taxiWidth, P(s.A), P(s.B));
        }
        foreach (var s in vm.Map)
        {
            if (!s.IsRunway)
                ctx.DrawLine(TaxiLine, P(s.A), P(s.B));
        }
        foreach (var st in vm.Stands)
            ctx.DrawEllipse(StandBrush, null, P(st), 3, 3);

        // La línea del pushback.
        var color = vm.IsBad ? Bad : vm.IsWarning ? Warn : Ok;
        var pathPen = new Pen(new SolidColorBrush(color), 3, new DashStyle([2, 1.5], 0), PenLineCap.Round);
        for (var i = 1; i < pts.Count; i++)
            ctx.DrawLine(pathPen, P(pts[i - 1]), P(pts[i]));

        // Aviones: inicio (contorno), final (color del estado) y en vivo.
        DrawAircraft(ctx, P, vm.Start, vm.HeadingDeg, null, StartPen);
        DrawAircraft(ctx, P, vm.Plan.Path.End, vm.Plan.EndHeadingDeg, new SolidColorBrush(color, 0.35), new Pen(new SolidColorBrush(color), 1.5));
        if (vm.Live is { } live)
            DrawAircraft(ctx, P, live.Position, live.HeadingDeg, LiveBrush, null);

        // Norte y escala.
        var label = new FormattedText(Tr.F("N ↑   ·   escala: {0:0} px = 50 m", 50 * scale), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, TextBrush);
        ctx.DrawText(label, new Point(10, 8));
        ctx.DrawLine(new Pen(TextBrush, 2), new Point(10, 28), new Point(10 + 50 * scale, 28));
    }

    private static void DrawAircraft(DrawingContext ctx, Func<LocalPoint, Point> toScreen, LocalPoint at, double headingDeg, IBrush? fill, IPen? pen)
    {
        var rad = headingDeg * GeoMath.DegToRad;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < Silhouette.Length; i++)
            {
                var (x, y) = Silhouette[i];
                var p = toScreen(new LocalPoint(at.EastM + x * cos + y * sin, at.NorthM - x * sin + y * cos));
                if (i == 0)
                    g.BeginFigure(p, fill is not null);
                else
                    g.LineTo(p);
            }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, pen, geometry);
    }
}
