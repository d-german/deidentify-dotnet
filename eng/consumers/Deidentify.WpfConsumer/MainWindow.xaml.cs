using System.Windows;

namespace Deidentify.WpfConsumer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        var result = ((App)Application.Current).Sanitizer.Sanitize(
            "Sarah uses test.user@example.test in London.");
        Title = result.IsSuccess ? result.Value.Text : result.Error.Code.ToString();
    }
}