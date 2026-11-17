using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SimATC.App.ViewModels;
using SimATC.Core;
using SimATC.Core.Airports;
using SimATC.Sim;

using SimATC.Core.Text;

namespace SimATC.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Idioma de la interfaz antes de crear cualquier ventana (las traducciones se resuelven al cargar el XAML).
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        Core.Text.Tr.Load(Path.Combine(AppContext.BaseDirectory, "data"), Core.Text.Tr.Resolve(settings.UiLanguage));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime demoLifetime && Views.PushbackDemo.TryCreate(demoLifetime.Args) is { } demo)
        {
            // Modo demo del planificador de pushback (sin simulador).
            demoLifetime.MainWindow = demo;
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Pantalla de carga (ADR-084): primero el logo y el avance; la ventana principal, cuando está lista.
            var splash = new Views.SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            _ = StartAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Los pasos reales del arranque, con un mínimo de tiempo por paso para que se alcancen a leer (~3 s en total).</summary>
    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, Views.SplashWindow splash)
    {
        static Task Pause() => Task.Delay(600);
        splash.Step(Tr.T("Cargando configuración…"), 10);
        await Pause();
        var bridge = new SimConnectBridge(m => System.Diagnostics.Trace.WriteLine($"[sim] {m}"));
        var airports = new AirportProvider(bridge, new AirportCache(AppPaths.AirportCache));
        // Al cerrar se suelta la conexión con SimConnect (RF-03, RF-04).
        desktop.Exit += (_, _) => bridge.Dispose();
        splash.Step(Tr.T("Cargando datos: fraseología, aerolíneas, FIR y Centros…"), 35);
        await Pause();
        // Sin aeropuerto fijo: se carga el aeropuerto donde está el piloto (ADR-030).
        var vm = new MainWindowViewModel(bridge, airports, Path.Combine(AppContext.BaseDirectory, "data"));
        // Un error sin capturar no cierra la app (crash del 2026-11-15): queda en el log y se sigue.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            vm.ReportUnhandled(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            vm.ReportUnhandled(e.Exception);
            e.SetObserved();
        };
        splash.Step(Tr.T("Preparando radar, comunicaciones y voces…"), 60);
        await Pause();
        var main = new MainWindow { DataContext = vm };
        splash.Step(Tr.T("Conectando con Microsoft Flight Simulator…"), 85);
        bridge.Start();
        await Pause();
        splash.Step(Tr.T("Listo"), 100);
        await Task.Delay(300);
        desktop.MainWindow = main;
        main.Show();
        splash.Close();
        // ¿Hay una versión nueva? (ADR-161; como mucho una vez al día, en segundo plano)
        _ = vm.CheckForUpdateAsync();
    }
}
