using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimATC.App.ViewModels;

/// <summary>
/// Diseño adaptable al ancho de la ventana (pedido de Marco, 2026-11-06: textos encimados fuera de 1920×1080). Con menos de
/// <see cref="CompactWidth"/> px el panel de comunicaciones deja de ir pegado al radar (queda en "Comunicaciones"); con menos
/// de <see cref="NarrowWidth"/> px el menú lateral queda solo con íconos.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public const double CompactWidth = 1450;
    public const double NarrowWidth = 1250;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactLayout), nameof(NavCollapsed), nameof(NavWidth), nameof(CommsColumnWidth),
        nameof(ShowDockedComms))]
    public partial double WindowWidth { get; set; } = 1400;

    /// <summary>Ventana angosta: el radar va sin el panel de comunicaciones al lado.</summary>
    public bool IsCompactLayout => WindowWidth < CompactWidth;

    /// <summary>Menú lateral solo con íconos (nombre en el globo de ayuda).</summary>
    public bool NavCollapsed => WindowWidth < NarrowWidth;

    public GridLength NavWidth => new(NavCollapsed ? 64 : 200);

    public GridLength CommsColumnWidth => new(IsCompactLayout ? 0 : 400);

    public bool ShowDockedComms => !IsCompactLayout;
}
