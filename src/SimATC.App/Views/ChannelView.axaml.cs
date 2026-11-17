using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimATC.App.ViewModels;

using SimATC.Core.Text;

namespace SimATC.App.Views;

/// <summary>
/// El chat de una radio. Si el piloto está al final, baja solo con cada mensaje; si subió a leer, no se mueve: aparece
/// "↓ N mensajes nuevos" y una barra "mensajes nuevos" encima del primero que no vio, que se atenúa al leerla (como
/// WhatsApp; prueba de Marco, 2026-11-01). El menú "Sintonizar" pide la frecuencia elegida.
/// </summary>
public partial class ChannelView : UserControl
{
    private ChannelViewModel? _vm;

    /// <summary>Margen (px) para considerar que el piloto está al final del chat.</summary>
    private const double BottomSlackPx = 40;

    /// <summary>Segundos que la barra "mensajes nuevos" queda fuerte después de leerla.</summary>
    private const double DividerFadeS = 8;

    private bool _atBottom = true;
    private int _unseen;
    private int _unseenForMe;
    private CommsMessage? _divider;

    public ChannelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ChannelViewModel);
        HistoryScroll.ScrollChanged += (_, _) => OnScrolled();
    }

    private bool IsAtBottom() =>
        HistoryScroll.Offset.Y >= HistoryScroll.Extent.Height - HistoryScroll.Viewport.Height - BottomSlackPx;

    private void OnScrolled()
    {
        var atBottom = IsAtBottom();
        if (atBottom == _atBottom)
            return;
        _atBottom = atBottom;
        if (atBottom && _unseen > 0)
        {
            // Llegó a los nuevos: se ocultan el aviso y, al rato, la barra queda tenue.
            _unseen = 0;
            _unseenForMe = 0;
            NewPill.IsVisible = false;
            var divider = _divider;
            DispatcherTimer.RunOnce(() =>
            {
                if (divider is not null)
                    divider.DividerSeen = true;
            }, TimeSpan.FromSeconds(DividerFadeS));
        }
        else if (!atBottom && _divider is { } d && _unseen == 0)
        {
            d.DividerSeen = true; // volvió a subir: la barra ya cumplió
        }
    }

    private void OnNewPill(object? sender, RoutedEventArgs e) => HistoryScroll.ScrollToEnd();

    private void Attach(ChannelViewModel? vm)
    {
        if (_vm is not null)
            _vm.History.CollectionChanged -= OnHistoryChanged;
        _vm = vm;
        if (_vm is not null)
        {
            _vm.History.CollectionChanged += OnHistoryChanged;
            Dispatcher.UIThread.Post(() => HistoryScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;
        if (_atBottom)
        {
            Dispatcher.UIThread.Post(() => HistoryScroll.ScrollToEnd(), DispatcherPriority.Background);
            return;
        }
        foreach (var item in e.NewItems)
        {
            if (item is not CommsMessage m)
                continue;
            if (_unseen == 0)
            {
                // Primer mensaje que no ve: la barra va aquí (y se quita la anterior).
                if (_divider is not null)
                    _divider.NewDivider = false;
                m.NewDivider = true;
                m.DividerSeen = false;
                _divider = m;
            }
            _unseen++;
            if (m.ForMe)
                _unseenForMe++;
        }
        NewPillText.Text = _unseenForMe > 0
            ? Tr.F("↓ {0} {1} · {2} para ti", _unseen, (_unseen == 1 ? Tr.T("mensaje nuevo") : Tr.T("mensajes nuevos")), _unseenForMe)
            : $"↓ {_unseen} {(_unseen == 1 ? "mensaje nuevo" : "mensajes nuevos")}";
        NewPill.IsVisible = true;
    }

    private void OnTune(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TuneOption option } && _vm is not null)
        {
            _vm.RequestTune(option.Target);
            // Cierra el menú.
            if (this.FindDescendantOfType<Button>() is { Flyout: { } flyout })
                flyout.Hide();
        }
    }
}
