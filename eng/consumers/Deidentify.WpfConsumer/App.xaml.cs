using System.Windows;

namespace Deidentify.WpfConsumer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    public SensitiveDataSanitizer Sanitizer { get; }

    public App()
    {
        var ner = LocalNerRecognizer.Create();
        if (ner.IsFailure)
        {
            throw new InvalidOperationException($"{ner.Error.Code}: {ner.Error.SafeMessage}");
        }

        Sanitizer = new SensitiveDataSanitizer(new SensitiveDataAnalyzer([ner.Value]));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("DEIDENTIFY_SMOKE") == "1")
        {
            var result = Sanitizer.Sanitize("Sarah uses test.user@example.test in London.");
            Environment.Exit(
                result.IsSuccess &&
                result.Value.Text.Contains("<PERSON>", StringComparison.Ordinal) &&
                result.Value.Text.Contains("<EMAIL_ADDRESS>", StringComparison.Ordinal) &&
                result.Value.Text.Contains("<LOCATION>", StringComparison.Ordinal)
                    ? 0
                    : 1);
        }

        base.OnStartup(e);
    }
}
