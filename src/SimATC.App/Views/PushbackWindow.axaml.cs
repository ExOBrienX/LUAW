using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimATC.App.ViewModels;

namespace SimATC.App.Views;

/// <summary>Ventana del planificador de pushback (RF-21c). Teclas: ← → giro, ↑ ↓ tramo recto, Enter inicia, Esc cierra o detiene.</summary>
public partial class PushbackWindow : Window
{
    public PushbackWindow()
    {
        InitializeComponent();
        // Túnel: las flechas no las toman los sliders.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.CloseRequested -= OnFinished;
            _vm = DataContext as PushbackViewModel;
            Canvas.Model = _vm;
            if (_vm is not null)
                _vm.CloseRequested += OnFinished;
        };
    }

    private PushbackViewModel? _vm;

    /// <summary>Segundos que se ve el "terminado" antes de que la ventana se cierre sola (ADR-136).</summary>
    public const double CloseAfterDoneS = 3;

    private void OnFinished() =>
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (_vm is { IsDone: true })
                Close();
        }, TimeSpan.FromSeconds(CloseAfterDoneS));

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(0.8);

    private void OnRecenter(object? sender, RoutedEventArgs e) => Canvas.Recenter();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PushbackViewModel vm)
            return;
        switch (e.Key)
        {
            case Key.Left:
                e.Handled = vm.Nudge(-1, 0);
                break;
            case Key.Right:
                e.Handled = vm.Nudge(1, 0);
                break;
            case Key.Up:
                e.Handled = vm.Nudge(0, 1);
                break;
            case Key.Down:
                e.Handled = vm.Nudge(0, -1);
                break;
            case Key.Enter:
                vm.StartPushbackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (vm.IsPushing)
                    vm.StopCommand.Execute(null);
                else
                    Close();
                e.Handled = true;
                break;
        }
    }
}
