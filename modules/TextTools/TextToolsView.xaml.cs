using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QingToolbox.Modules.TextTools;

public partial class TextToolsView : UserControl
{
    private static readonly Brush NormalStatusBrush =
        new SolidColorBrush(Color.FromRgb(71, 85, 105));
    private static readonly Brush ErrorStatusBrush =
        new SolidColorBrush(Color.FromRgb(185, 28, 28));

    public TextToolsView()
    {
        InitializeComponent();
    }

    private string Input => InputTextBox.Text;

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
        RunOperation("JSON formatted.", () =>
        {
            using var document = JsonDocument.Parse(Input);
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        });
    }

    private void MinifyJson_Click(object sender, RoutedEventArgs e)
    {
        RunOperation("JSON minified.", () =>
        {
            using var document = JsonDocument.Parse(Input);
            return JsonSerializer.Serialize(document.RootElement);
        });
    }

    private void Base64Encode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(
            "Base64 encoded.",
            () => Convert.ToBase64String(Encoding.UTF8.GetBytes(Input)));
    }

    private void Base64Decode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = Convert.FromBase64String(Input);
            SetOutput(Encoding.UTF8.GetString(bytes), "Base64 decoded.");
        }
        catch (FormatException)
        {
            SetError("Invalid Base64 input.");
        }
    }

    private void UrlEncode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation("URL encoded.", () => Uri.EscapeDataString(Input));
    }

    private void UrlDecode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation("URL decoded.", () => Uri.UnescapeDataString(Input));
    }

    private void Uppercase_Click(object sender, RoutedEventArgs e)
    {
        SetOutput(Input.ToUpperInvariant(), "Converted to uppercase.");
    }

    private void Lowercase_Click(object sender, RoutedEventArgs e)
    {
        SetOutput(Input.ToLowerInvariant(), "Converted to lowercase.");
    }

    private void RemoveEmptyLines_Click(object sender, RoutedEventArgs e)
    {
        var lines = Input
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line));

        SetOutput(
            string.Join(Environment.NewLine, lines),
            "Empty lines removed.");
    }

    private void CopyOutputToInput_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Text = OutputTextBox.Text;
        SetStatus("Output copied to input.");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Clear();
        OutputTextBox.Clear();
        SetStatus("Ready.");
    }

    private void RunOperation(string successMessage, Func<string> operation)
    {
        try
        {
            SetOutput(operation(), successMessage);
        }
        catch (JsonException exception)
        {
            SetError($"Invalid JSON: {exception.Message}");
        }
        catch (UriFormatException exception)
        {
            SetError($"Invalid URL input: {exception.Message}");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void SetOutput(string output, string status)
    {
        OutputTextBox.Text = output;
        SetStatus(status);
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Foreground = NormalStatusBrush;
        StatusTextBlock.Text = message;
    }

    private void SetError(string message)
    {
        StatusTextBlock.Foreground = ErrorStatusBrush;
        StatusTextBlock.Text = message;
    }
}
