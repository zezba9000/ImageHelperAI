using Avalonia.Controls;

namespace ImageHelperAI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => Sides.Dispose();
    }
}
