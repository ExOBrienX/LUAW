using Avalonia.Controls;

namespace SimATC.App.Views;

public partial class CompactWindow : Window
{
    public CompactWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Comms.FocusInput();
    }
}
