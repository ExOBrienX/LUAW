using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimATC.App.Radar;
using SimATC.App.ViewModels;
using SimATC.App.Views;

using SimATC.Core.Text;

namespace SimATC.App;

public partial class MainWindow : Window
{
    private CompactWindow? _compact;

    public MainWindow()
    {
        InitializeComponent();
        // Diseño adaptable: el ancho de la ventana decide si el panel de comunicaciones va al lado del radar y el menú.
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainWindowViewModel layout)
                layout.WindowWidth = e.NewSize.Width;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            vm.CompactRequested += () => OpenCompact(vm);
            vm.PushbackWindowRequested += pvm => OpenPushback(vm, pvm);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsPlan) && vm.IsPlan)
                    Dispatcher.UIThread.Post(PreviewRadar.FitPlan, DispatcherPriority.Background);
            };
            // Después del cambio de panel: el radar recién visible ya tiene tamaño.
            vm.FitPlanRequested += () => Dispatcher.UIThread.Post(RadarPanelView.RadarView.FitPlan, DispatcherPriority.Background);
            vm.RadarWindowRequested += () => OpenRadarWindow(vm);
            vm.RadarDockRequested += () => _radarWindow?.Close();
            // El log baja solo mientras "Seguir el final" esté marcado.
            vm.VisibleLog.CollectionChanged += (_, e) =>
            {
                if (vm.LogAutoScroll && vm.IsDiag && e.Action == NotifyCollectionChangedAction.Add)
                    Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
            };
        };
        Closing += OnClosing;
        Opened += async (_, _) =>
        {
            // LUAW se cerró de golpe con un vuelo en curso: ¿retomarlo donde quedó?
            if (DataContext is MainWindowViewModel { ResumeOffer: not null } vm)
            {
                if (await AskResumeAsync(vm.ResumeText))
                    vm.AcceptResume();
                else
                    vm.DeclineResume();
            }
        };
        Closed += (_, _) =>
        {
            (DataContext as ViewModels.MainWindowViewModel)?.SaveAirwayCache();
            (DataContext as ViewModels.MainWindowViewModel)?.DisposeMic();
            (DataContext as ViewModels.MainWindowViewModel)?.FlushSessionLog();
            _compact?.Close();
            _radarWindow?.Close();
        };
    }

    private bool _closeConfirmed;

    /// <summary>Cerrar pide confirmación (Marco, 2026-10-31): al cerrar se borra el tráfico del simulador y se pierde la conversación con el ATC.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
            return;
        e.Cancel = true;
        if (await ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            (DataContext as MainWindowViewModel)?.ForgetResume(); // cierre normal: nada que retomar

            Close();
        }
    }

    private Task<bool> AskResumeAsync(string flight)
    {
        var dialog = new Window
        {
            Title = "LUAW",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon,
        };
        var yes = new Button { Content = Tr.T("Retomar vuelo"), Classes = { "primary" }, IsDefault = true, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var no = new Button { Content = Tr.T("Empezar de nuevo"), IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = Tr.F("LUAW se cerró de forma inesperada durante el vuelo {0}.\n\n", flight) +
                           Tr.T("¿Retomarlo donde quedó? Se carga el mismo plan y el ATC sigue con la misma autorización."),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        return dialog.ShowDialog<bool>(this);
    }

    private Task<bool> ConfirmCloseAsync()
    {
        var dialog = new Window
        {
            Title = "LUAW",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Icon = Icon,
        };
        var yes = new Button { Content = Tr.T("Cerrar LUAW"), Classes = { "primary" }, IsDefault = true, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var no = new Button { Content = Tr.T("Cancelar"), IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = Tr.T("¿Cerrar LUAW? Se borra el tráfico que inyectó en el simulador y se pierde la conversación con el ATC."),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        return dialog.ShowDialog<bool>(this);
    }

    // El radar es uno solo (RadarPanelView): en la ventana principal o en la suya, nunca dos dibujando. Hereda los datos
    // de la ventana donde está (no se le asignan a mano: con Hot Reload ese código no se vuelve a ejecutar).
    private Window? _radarWindow;

    /// <summary>Saca el radar de la ventana principal para ponerlo en otra.</summary>
    private RadarPanel TakeRadar()
    {
        var panel = RadarPanelView;
        RadarHost.Content = null;
        return panel;
    }

    private void OpenRadarWindow(MainWindowViewModel vm)
    {
        if (_radarWindow is not null)
        {
            _radarWindow.Activate();
            return;
        }
        _radarWindow = new Window
        {
            Title = Tr.T("LUAW · radar"),
            Width = 1100,
            Height = 800,
            MinWidth = 500,
            MinHeight = 400,
            Background = Background,
            DataContext = vm,
            Content = TakeRadar(),
            Icon = Icon,
        };
        _radarWindow.Closed += (_, _) =>
        {
            _radarWindow.Content = null;
            _radarWindow = null;
            RadarHost.Content = RadarPanelView;
            vm.RadarDetached = false;
        };
        vm.RadarDetached = true;
        _radarWindow.Show();
    }

    /// <summary>Abre (o trae al frente) el panel compacto; comparte la frecuencia con la ventana principal.</summary>
    private PushbackWindow? _pushback;

    private void OpenPushback(MainWindowViewModel vm, PushbackViewModel pvm)
    {
        if (_pushback is not null)
        {
            _pushback.DataContext = pvm;
            _pushback.Activate();
            return;
        }
        _pushback = new PushbackWindow { DataContext = pvm, Topmost = vm.CompactAlwaysOnTop };
        _pushback.Closed += (_, _) =>
        {
            _pushback = null;
            vm.PushbackWindowClosed();
        };
        _pushback.Show();
    }

    private void OpenCompact(MainWindowViewModel vm)
    {
        if (_compact is not null)
        {
            _compact.Topmost = vm.CompactAlwaysOnTop;
            _compact.Activate();
            return;
        }
        _compact = new CompactWindow { DataContext = vm.Comms, Topmost = vm.CompactAlwaysOnTop };
        _compact.Closed += (_, _) => _compact = null;
        _compact.Show();
    }

    // Vista previa del plan: encuadra cada parte del vuelo.
    /// <summary>Clic en una fila de Tráfico (fuera de sus botones): la despliega con su ficha.</summary>
    private void OnTrafficRowTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (sender is Control { DataContext: TrafficRow row } && DataContext is MainWindowViewModel vm)
            vm.ToggleTrafficDetailCommand.Execute(row);
    }

    private void OnPreviewSid(object? sender, RoutedEventArgs e) => PreviewRadar.FitProcedures(ProcedureKind.Sid);

    private void OnPreviewRoute(object? sender, RoutedEventArgs e) => PreviewRadar.FitPlan();

    private void OnPreviewArrival(object? sender, RoutedEventArgs e) => PreviewRadar.FitProcedures(ProcedureKind.Star, ProcedureKind.Approach);

    private void OnPreviewApproach(object? sender, RoutedEventArgs e) => PreviewRadar.FitProcedures(ProcedureKind.Approach);

    /// <summary>Cada pestaña del plan encuadra el mapa en su parte del vuelo.</summary>
    private void OnPlanTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != PlanTabs || PreviewRadar is null)
            return;
        Action fit = PlanTabs.SelectedIndex switch
        {
            1 => () => PreviewRadar.FitProcedures(ProcedureKind.Sid),
            2 => () => PreviewRadar.FitProcedures(ProcedureKind.Star, ProcedureKind.Approach),
            3 => () => PreviewRadar.FitProcedures(ProcedureKind.Approach),
            _ => PreviewRadar.FitPlan,
        };
        Dispatcher.UIThread.Post(fit, DispatcherPriority.Background);
    }

    /// <summary>Configuración → Modelos de tráfico: elige una carpeta con paquetes FSLTL/AIG.</summary>
    private async void OnAddModelFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Tr.T("Carpeta con modelos de tráfico (FSLTL, AIG)"),
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.AddModelFolder(path);
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.VisibleLogText());
    }
}
