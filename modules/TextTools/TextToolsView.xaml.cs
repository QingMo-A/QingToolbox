using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.TextTools;

public partial class TextToolsView : UserControl, ILocalizedModuleView
{
    private static readonly Brush NormalStatusBrush =
        new SolidColorBrush(Color.FromRgb(71, 85, 105));
    private static readonly Brush ErrorStatusBrush =
        new SolidColorBrush(Color.FromRgb(185, 28, 28));

    private readonly ILocalizationService _localization;
    private readonly string _moduleId;

    public TextToolsView(ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _localization = localization;
        _moduleId = moduleId;
        RefreshLocalization();
    }

    private string Input => InputTextBox.Text;
    private string T(string key, string fallback) =>
        _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "Text Tools");
        SubtitleText.Text = T(
            "view.subtitle",
            "Format, encode, decode and transform text quickly.");
        InputLabelText.Text = T("view.input", "Input");
        OutputLabelText.Text = T("view.output", "Output");
        FormatJsonButton.Content = T("actions.formatJson", "Format JSON");
        MinifyJsonButton.Content = T("actions.minifyJson", "Minify JSON");
        Base64EncodeButton.Content = T("actions.base64Encode", "Base64 Encode");
        Base64DecodeButton.Content = T("actions.base64Decode", "Base64 Decode");
        UrlEncodeButton.Content = T("actions.urlEncode", "URL Encode");
        UrlDecodeButton.Content = T("actions.urlDecode", "URL Decode");
        UppercaseButton.Content = T("actions.uppercase", "Uppercase");
        LowercaseButton.Content = T("actions.lowercase", "Lowercase");
        RemoveEmptyLinesButton.Content = T(
            "actions.removeEmptyLines",
            "Remove Empty Lines");
        CopyOutputToInputButton.Content = T(
            "actions.copyOutputToInput",
            "Copy Output To Input");
        ClearButton.Content = T("actions.clear", "Clear");
    }

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(T("status.done", "Done."), () =>
        {
            using var document = JsonDocument.Parse(Input);
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        });
    }

    private void MinifyJson_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(T("status.done", "Done."), () =>
        {
            using var document = JsonDocument.Parse(Input);
            return JsonSerializer.Serialize(document.RootElement);
        });
    }

    private void Base64Encode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(
            T("status.done", "Done."),
            () => Convert.ToBase64String(Encoding.UTF8.GetBytes(Input)));
    }

    private void Base64Decode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = Convert.FromBase64String(Input);
            SetOutput(Encoding.UTF8.GetString(bytes), T("status.done", "Done."));
        }
        catch (FormatException)
        {
            SetError(T("errors.invalidBase64", "Invalid Base64 input."));
        }
    }

    private void UrlEncode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(T("status.done", "Done."), () => Uri.EscapeDataString(Input));
    }

    private void UrlDecode_Click(object sender, RoutedEventArgs e)
    {
        RunOperation(T("status.done", "Done."), () => Uri.UnescapeDataString(Input));
    }

    private void Uppercase_Click(object sender, RoutedEventArgs e)
    {
        SetOutput(Input.ToUpperInvariant(), T("status.done", "Done."));
    }

    private void Lowercase_Click(object sender, RoutedEventArgs e)
    {
        SetOutput(Input.ToLowerInvariant(), T("status.done", "Done."));
    }

    private void RemoveEmptyLines_Click(object sender, RoutedEventArgs e)
    {
        var lines = Input
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line));

        SetOutput(
            string.Join(Environment.NewLine, lines),
            T("status.done", "Done."));
    }

    private void CopyOutputToInput_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Text = OutputTextBox.Text;
        SetStatus(T("status.copied", "Copied."));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Clear();
        OutputTextBox.Clear();
        SetStatus(T("status.ready", "Ready."));
    }

    private void RunOperation(string successMessage, Func<string> operation)
    {
        try
        {
            SetOutput(operation(), successMessage);
        }
        catch (JsonException exception)
        {
            SetError(_localization.GetModuleString(
                _moduleId,
                "errors.invalidJson",
                "Invalid JSON: {0}",
                exception.Message));
        }
        catch (UriFormatException exception)
        {
            SetError(_localization.GetModuleString(
                _moduleId,
                "errors.operationFailed",
                "Operation failed: {0}",
                exception.Message));
        }
        catch (Exception exception)
        {
            SetError(_localization.GetModuleString(
                _moduleId,
                "errors.operationFailed",
                "Operation failed: {0}",
                exception.Message));
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
