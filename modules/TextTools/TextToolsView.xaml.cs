using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.TextTools;

public partial class TextToolsView : UserControl, ILocalizedModuleView, IAsyncDisposable
{
    private static readonly Brush NormalStatusBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));
    private static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<string>? _currentOperation;
    private bool _busy;
    private bool _disposed;

    public TextToolsView(ILocalizationService localization, string moduleId)
    {
        InitializeComponent(); _localization = localization; _moduleId = moduleId;
        RefreshLocalization(); SetStatus(T("status.ready", "Ready.")); UpdateButtonStates();
    }

    private string Input => InputTextBox.Text;
    private string T(string key, string fallback) => _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text=T("view.title","Text Tools"); SubtitleText.Text=T("view.subtitle","Format, encode, decode and transform text quickly.");
        InputLabelText.Text=T("view.input","Input"); OutputLabelText.Text=T("view.output","Output");
        SetButton(FormatJsonButton,"actions.formatJson","Format JSON"); SetButton(MinifyJsonButton,"actions.minifyJson","Minify JSON");
        SetButton(Base64EncodeButton,"actions.base64Encode","Base64 Encode"); SetButton(Base64DecodeButton,"actions.base64Decode","Base64 Decode");
        SetButton(UrlEncodeButton,"actions.urlEncode","URL Encode"); SetButton(UrlDecodeButton,"actions.urlDecode","URL Decode");
        SetButton(UppercaseButton,"actions.uppercase","Uppercase"); SetButton(LowercaseButton,"actions.lowercase","Lowercase");
        SetButton(RemoveEmptyLinesButton,"actions.removeEmptyLines","Remove Empty Lines"); SetButton(CopyOutputButton,"actions.copyOutput","Copy Result");
        SetButton(CopyOutputToInputButton,"actions.copyOutputToInput","Result to Input"); SetButton(SwapButton,"actions.swap","Swap");
        SetButton(ClearButton,"actions.clear","Clear");
        AutomationProperties.SetName(InputTextBox,T("automation.input","Input text"));
        AutomationProperties.SetName(OutputTextBox,T("automation.output","Output text"));
    }

    private void SetButton(Button button,string key,string fallback){var text=T(key,fallback);button.Content=text;AutomationProperties.SetName(button,text);}
    private void InputTextChanged(object sender,TextChangedEventArgs e)=>UpdateButtonStates();
    private void OutputTextChanged(object sender,TextChangedEventArgs e)=>UpdateButtonStates();
    private async void FormatJson_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.FormatJson);
    private async void MinifyJson_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.MinifyJson);
    private async void Base64Encode_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.Base64Encode);
    private async void Base64Decode_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.Base64Decode);
    private async void UrlEncode_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.UrlEncode);
    private async void UrlDecode_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.UrlDecode);
    private async void Uppercase_Click(object s,RoutedEventArgs e)=>await RunAsync(x=>x.ToUpperInvariant());
    private async void Lowercase_Click(object s,RoutedEventArgs e)=>await RunAsync(x=>x.ToLowerInvariant());
    private async void RemoveEmptyLines_Click(object s,RoutedEventArgs e)=>await RunAsync(TextOperations.RemoveEmptyLines);

    private async Task RunAsync(Func<string,string> operation)
    {
        if(_busy||string.IsNullOrEmpty(Input)){SetStatus(T("status.inputEmpty","Input is empty."));return;}
        _busy=true;SetStatus(T("status.processing","Processing…"));UpdateButtonStates();
        try{var input=Input;_currentOperation=Task.Run(()=>operation(input),_lifetime.Token);var output=await _currentOperation;if(!_lifetime.IsCancellationRequested){OutputTextBox.Text=output;SetStatus(T("status.done","Done."));}}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        catch(JsonException){SetError(T("errors.invalidJson","The input is not valid JSON."));}
        catch(UriFormatException){SetError(T("errors.invalidUrl","Invalid URL-encoded input."));}
        catch(FormatException){SetError(T("errors.invalidBase64","Invalid Base64 input."));}
        catch(Exception){SetError(T("errors.operationFailed","The operation could not be completed."));}
        finally{_currentOperation=null;_busy=false;UpdateButtonStates();}
    }

    private void CopyOutput_Click(object s,RoutedEventArgs e){try{Clipboard.SetText(OutputTextBox.Text);SetStatus(T("status.copied","Copied."));}catch(Exception){SetError(T("errors.copyFailed","The result could not be copied."));}}
    private void CopyOutputToInput_Click(object s,RoutedEventArgs e){InputTextBox.Text=OutputTextBox.Text;SetStatus(T("status.moved","Result moved to input."));}
    private void Swap_Click(object s,RoutedEventArgs e){(InputTextBox.Text,OutputTextBox.Text)=(OutputTextBox.Text,InputTextBox.Text);SetStatus(T("status.swapped","Input and result swapped."));}
    private void Clear_Click(object s,RoutedEventArgs e){InputTextBox.Clear();OutputTextBox.Clear();SetStatus(T("status.ready","Ready."));}
    private void UpdateButtonStates(){var hasInput=!string.IsNullOrEmpty(Input);var hasOutput=!string.IsNullOrEmpty(OutputTextBox.Text);foreach(var button in new[]{FormatJsonButton,MinifyJsonButton,Base64EncodeButton,Base64DecodeButton,UrlEncodeButton,UrlDecodeButton,UppercaseButton,LowercaseButton,RemoveEmptyLinesButton})button.IsEnabled=hasInput&&!_busy;CopyOutputButton.IsEnabled=hasOutput&&!_busy;CopyOutputToInputButton.IsEnabled=hasOutput&&!_busy;SwapButton.IsEnabled=(hasInput||hasOutput)&&!_busy;ClearButton.IsEnabled=(hasInput||hasOutput)&&!_busy;}
    private void SetStatus(string message){StatusTextBlock.Foreground=NormalStatusBrush;StatusTextBlock.Text=message;}
    private void SetError(string message){StatusTextBlock.Foreground=ErrorStatusBrush;StatusTextBlock.Text=message;}
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;_lifetime.Cancel();if(_currentOperation is not null)try{await _currentOperation;}catch(OperationCanceledException){}catch{} _lifetime.Dispose();}
}

internal static class TextOperations
{
    public static string FormatJson(string input){using var document=JsonDocument.Parse(input);return JsonSerializer.Serialize(document.RootElement,new JsonSerializerOptions{WriteIndented=true});}
    public static string MinifyJson(string input){using var document=JsonDocument.Parse(input);return JsonSerializer.Serialize(document.RootElement);}
    public static string Base64Encode(string input)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
    public static string Base64Decode(string input)=>Encoding.UTF8.GetString(Convert.FromBase64String(input));
    public static string UrlEncode(string input)=>Uri.EscapeDataString(input);
    public static string UrlDecode(string input)=>Uri.UnescapeDataString(input);
    public static string RemoveEmptyLines(string input)=>string.Join(Environment.NewLine,input.Replace("\r\n","\n").Split('\n').Where(line=>!string.IsNullOrWhiteSpace(line)));
}
