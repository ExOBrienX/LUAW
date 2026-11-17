using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimATC.Core.Comms;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una fila de la leyenda del radar: el color y qué es.</summary>
public sealed record LegendItem(string Text, IBrush Brush);

/// <summary>
/// Tarjeta del ATC sobre el radar, leyenda de colores y avisos de unos segundos (ADR-107, pedido de Marco 2026-11-09):
/// cambios de pista (aeropuerto actual, destino y la de salida del piloto) y motor y al aire. Los avisos quedan también en
/// el chat.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Cuánto se ve un aviso (s).</summary>
    public const double ToastSeconds = 6;

    /// <summary>La autorización, las instrucciones y "Qué decir ahora" van en la tarjeta del radar (radar en esta ventana).</summary>
    public bool ShowAtcCard => !RadarDetached;

    [ObservableProperty]
    public partial bool AtcCardCollapsed { get; set; }

    [ObservableProperty]
    public partial bool ShowRadarLegend { get; set; }

    /// <summary>Los colores del radar (los mismos de <c>RadarView</c>).</summary>
    public IReadOnlyList<LegendItem> RadarLegend { get; } =
    [
        new(Tr.T("Pista de llegadas"), Brush(0xFF3DDC84)),
        new(Tr.T("Pista de salidas"), Brush(0xFF4C9BFF)),
        new(Tr.T("Pista mixta (llegadas y salidas)"), Brush(0xFFF5A524)),
        new(Tr.T("Tu ruta de rodaje"), Brush(0xFFE879F9)),
        new(Tr.T("Punto de espera"), Brush(0xFFE5484D)),
        new(Tr.T("Ruta del plan"), Brush(0xFFC77DFF)),
        new("SID", Brush(0xFF7BE3A6)),
        new(Tr.T("SID recomendada por LUAW (punteada)"), Brush(0xFFE8EDF3)),
        new("STAR", Brush(0xFFFF9F5A)),
        new(Tr.T("Aproximación (la que dio el ATC) y frustrada"), Brush(0xFF4CC2FF)),
        new(Tr.T("Tu avión"), Brush(0xFF3DDC84)),
        new(Tr.T("Tráfico"), Brush(0xFF9DB4D3)),
    ];

    private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    private DispatcherTimer? _toastTimer;

    /// <summary>Muestra un aviso unos segundos sobre el radar (el siguiente reemplaza al anterior).</summary>
    private void ShowToast(string text)
    {
        ToastText = text;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ToastSeconds) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastText = null;
        };
        _toastTimer.Start();
    }

    // Lo que ya se avisó, para avisar solo los cambios.
    private string? _noticeDepRunway;
    private string? _noticeArrRunway;
    private bool _noticeMissed;

    /// <summary>
    /// Del reloj de 1 s: avisa los cambios del vuelo del piloto: pista de salida, pista de llegada en el destino y motor y
    /// al aire (con lo que tiene que hacer).
    /// </summary>
    private void NoticeTick()
    {
        if (_session is not { } s)
        {
            _noticeDepRunway = _noticeArrRunway = null;
            _noticeMissed = false;
            return;
        }
        if (s.Phase <= PilotPhase.TakeoffCleared && s.Clearance?.Runway is { } dep)
        {
            if (_noticeDepRunway is not null && _noticeDepRunway != dep)
                ShowToast(Tr.F("Cambio de pista de salida: ahora sales por la {0}{1}.", dep, (s.Clearance.Sid is { } sid ? $" ({sid})" : "")));
            _noticeDepRunway = dep;
        }
        if (s.Phase is >= PilotPhase.EnRoute and < PilotPhase.Landed && _arrival is { } arr)
        {
            if (_noticeArrRunway is not null && _noticeArrRunway != arr.Runway)
                ShowToast(Tr.F("{0}: cambio de pista de llegada a la {1}", arr.Airport.Icao, arr.Runway)
                          + (arr.ApproachName is { } app ? Tr.F(" ({0}{1}). Cambia la aproximación en el FMS.", app, (arr.Transition is { } t ? Tr.F(" vía {0}", t) : "")) : "."));
            _noticeArrRunway = arr.Runway;
        }
        if (s.MissedApproachActive != _noticeMissed)
        {
            _noticeMissed = s.MissedApproachActive;
            if (_noticeMissed)
                ShowToast(Tr.T("Motor y al aire: vuela la frustrada y sube a la altitud indicada. La Aproximación te dirá cómo volver."));
        }
    }
}
