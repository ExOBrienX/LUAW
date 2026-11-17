using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using SimATC.App.Radar;

namespace SimATC.App.Views;

/// <summary>
/// El radar con su barra, la ficha del avión y el aviso de seguir (ver el .axaml). La ficha se mueve arrastrándola por
/// el título, dentro del radar.
/// </summary>
public partial class RadarPanel : UserControl
{
    private Point? _dragFrom;

    public RadarPanel()
    {
        InitializeComponent();
        // Clic en el avión que ya se sigue con la ficha cerrada: la vuelve a abrir.
        Radar.ViewChanged += (s, n, w, e) => (DataContext as ViewModels.MainWindowViewModel)?.OnRadarView(s, n, w, e);
        Radar.Picked += id =>
        {
            if (id is not null && DataContext is ViewModels.MainWindowViewModel vm)
                vm.InspectorOpen = true;
        };
        CardHandle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(CardHandle);
        };
        CardHandle.PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from)
                return;
            var now = e.GetPosition(this);
            var m = Card.Margin;
            // Alineada arriba a la derecha: mover a la derecha achica el margen derecho.
            var right = Math.Clamp(m.Right - (now.X - from.X), 0, Math.Max(0, Bounds.Width - Card.Bounds.Width));
            var top = Math.Clamp(m.Top + (now.Y - from.Y), 0, Math.Max(0, Bounds.Height - Card.Bounds.Height));
            Card.Margin = new Thickness(m.Left, top, right, m.Bottom);
            _dragFrom = now;
        };
        CardHandle.PointerReleased += (_, e) =>
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        };
        // La tarjeta del ATC (abajo a la derecha) se arrastra igual por su título (ADR-107).
        AtcCardHandle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<ToggleButton>(true) is not null)
                return;
            _atcDragFrom = e.GetPosition(this);
            e.Pointer.Capture(AtcCardHandle);
        };
        AtcCardHandle.PointerMoved += (_, e) =>
        {
            if (_atcDragFrom is not { } from)
                return;
            var now = e.GetPosition(this);
            var m = AtcCard.Margin;
            // Alineada abajo a la derecha: mover a la derecha o hacia abajo achica esos márgenes.
            var right = Math.Clamp(m.Right - (now.X - from.X), 0, Math.Max(0, Bounds.Width - AtcCard.Bounds.Width));
            var bottom = Math.Clamp(m.Bottom - (now.Y - from.Y), 0, Math.Max(0, Bounds.Height - AtcCard.Bounds.Height));
            AtcCard.Margin = new Thickness(m.Left, m.Top, right, bottom);
            _atcDragFrom = now;
        };
        AtcCardHandle.PointerReleased += (_, e) =>
        {
            _atcDragFrom = null;
            e.Pointer.Capture(null);
        };
    }

    private Point? _atcDragFrom;

    /// <summary>El radar de este panel (para encuadrar el plan).</summary>
    public RadarView RadarView => Radar;
}
