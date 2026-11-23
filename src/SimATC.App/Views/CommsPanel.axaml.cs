using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SimATC.App.ViewModels;

namespace SimATC.App.Views;

/// <summary>
/// Teclas del campo de texto (RF-30c): TAB completa, ↑/↓ cambian de sugerencia, Enter envía, Esc borra. Se capturan
/// antes que el TextBox (túnel) para que TAB no mueva el foco. El historial baja solo con cada mensaje nuevo.
/// </summary>
public partial class CommsPanel : UserControl
{
    private CommsViewModel? _vm;

    public CommsPanel()
    {
        InitializeComponent();
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as CommsViewModel);
    }

    private void Attach(CommsViewModel? vm) => _vm = vm;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        switch (e.Key)
        {
            case Key.Tab:
                if (_vm.Complete())
                    Input.CaretIndex = _vm.InputText.Length;
                e.Handled = true;
                break;
            case Key.Up:
                _vm.Cycle(-1);
                e.Handled = true;
                break;
            case Key.Down:
                _vm.Cycle(1);
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.ClearCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<CommsPanel, bool>(nameof(IsCompact));

    /// <summary>Modo del panel compacto (RF-42c): la autorización en una línea y sin textos de ayuda.</summary>
    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public static readonly StyledProperty<bool> IsWideProperty = AvaloniaProperty.Register<CommsPanel, bool>(nameof(IsWide));

    /// <summary>La autorización y "Qué decir ahora" están en la tarjeta del radar (ADR-107): aquí no se repiten y el chat ocupa todo.</summary>
    public static readonly StyledProperty<bool> StepOnRadarProperty = AvaloniaProperty.Register<CommsPanel, bool>(nameof(StepOnRadar));

    public bool StepOnRadar
    {
        get => GetValue(StepOnRadarProperty);
        set => SetValue(StepOnRadarProperty, value);
    }

    /// <summary>Desde este ancho (px) el panel usa dos columnas aunque no sea la página Comunicaciones.</summary>
    public const double WideFromPx = 760;

    /// <summary>Página Comunicaciones: la autorización y "Qué decir ahora" en una columna aparte del chat.</summary>
    public bool IsWide
    {
        get => GetValue(IsWideProperty);
        set => SetValue(IsWideProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
            Classes.Set("compact", IsCompact);
        if (change.Property == IsWideProperty || change.Property == IsCompactProperty || change.Property == BoundsProperty)
            Classes.Set("wide", !IsCompact && (IsWide || Bounds.Width >= WideFromPx));
    }

    /// <summary>Pone el cursor en el campo de texto (p. ej. al abrir el panel).</summary>
    public void FocusInput() => Input.Focus();

    /// <summary>Tras enviar con el botón, el foco vuelve al campo para seguir escribiendo.</summary>
    private void OnSendClick(object? sender, RoutedEventArgs e) => Input.Focus();
}
