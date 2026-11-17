using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using SimATC.Core.Text;

namespace SimATC.App.Views;

/// <summary>
/// Pantalla de carga al abrir LUAW (ADR-084): el logo, el paso del arranque que se está haciendo y una barra de progreso
/// abajo. Sin bordes, centrada; la cierra <see cref="App"/> cuando la ventana principal está lista.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;

    public SplashWindow()
    {
        Width = 480;
        Height = 520;
        CanResize = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Title = "LUAW";
        Background = Brushes.White;
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LUAW/Assets/simatc.ico")));
        }
        catch (Exception)
        {
            // Sin ícono: no impide abrir.
        }

        Image? logo = null;
        try
        {
            logo = new Image
            {
                Source = new Bitmap(AssetLoader.Open(new Uri("avares://LUAW/Assets/luaw-logo.webp"))),
                Width = 380,
                Height = 380,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        catch (Exception)
        {
            // Sin logo: queda el texto.
        }
        _status = new TextBlock
        {
            Text = Tr.T("Iniciando…"),
            Foreground = new SolidColorBrush(Color.FromRgb(70, 86, 104)),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 10),
        };
        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            Foreground = new SolidColorBrush(Color.FromRgb(46, 150, 245)),
            Background = new SolidColorBrush(Color.FromRgb(225, 232, 240)),
        };
        var panel = new DockPanel { Margin = new Thickness(36, 28, 36, 26) };
        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(_bar);
        bottom.Children.Add(new TextBlock
        {
            Text = Tr.T("ATC y tráfico para Microsoft Flight Simulator 2024"),
            Foreground = new SolidColorBrush(Color.FromRgb(140, 152, 166)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        if (logo is not null)
            panel.Children.Add(logo);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(214, 222, 232)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    /// <summary>Muestra un paso del arranque y su avance (0-100).</summary>
    public void Step(string text, double percent)
    {
        _status.Text = text;
        _bar.Value = percent;
    }
}
