[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$shellRoot = Join-Path $repoRoot 'QingToolbox.Shell'
$mainWindow = Get-Content -LiteralPath (Join-Path $shellRoot 'MainWindow.xaml') -Raw
$mainWindowCode = Get-Content -LiteralPath (Join-Path $shellRoot 'MainWindow.xaml.cs') -Raw
$themeManager = Get-Content -LiteralPath (Join-Path (Join-Path $shellRoot 'Windowing') 'WindowTitleBarThemeManager.cs') -Raw
$shellTheme = Get-Content -LiteralPath (Join-Path (Join-Path $shellRoot 'Resources') 'ShellTheme.xaml') -Raw
$smoke = Get-Content -LiteralPath (Join-Path (Join-Path $repoRoot 'QingToolbox.DevTools.WebShellSmokeTest') 'Program.cs') -Raw

function Require-Text {
    param([string]$Text, [string]$Needle, [string]$Message = "Missing native title-bar contract: $Needle")
    if ($Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw $Message }
}

Require-Text $mainWindow 'FontFamily="{Binding NativeFontFamily}"' 'MainWindow must bind the native title bar to FontSettingsService output.'
Require-Text $mainWindow 'Title="{Binding Title}"' 'MainWindow title must preserve the host execution-environment display name.'
Require-Text $mainWindowCode '_viewModel.PropertyChanged += OnViewModelPropertyChanged' 'Native title-bar palette must observe host-confirmed appearance changes.'
Require-Text $mainWindowCode 'Dispatcher.CheckAccess()' 'Native title-bar resources must only be updated on the WPF dispatcher thread.'
Require-Text $mainWindowCode 'Dispatcher.BeginInvoke(ApplyTheme)' 'Off-thread appearance changes must be marshalled to the WPF dispatcher.'
Require-Text $mainWindowCode 'WindowTitleBarThemeManager.Apply(mode, _viewModel.AppearancePresetId)' 'Web light/dark changes must be combined with the host preset, never used as a preset source.'
Require-Text $mainWindowCode 'WindowTitleBarThemeManager.Apply(WebShellThemeMode.System, _viewModel.AppearancePresetId)' 'System theme fallback must retain the host preset.'
if ($mainWindowCode -match 'WindowTitleBarThemeManager\.Apply\(mode\)') { throw 'A DOM theme notification must not overwrite the host appearance projection.' }

foreach ($preset in @('QingDefault', 'NeonCircuit', 'Greenline', 'AuroraFlow', 'QingNova')) {
    Require-Text $themeManager "AppearancePresetIds.$preset" "Native title bar has no palette branch for $preset."
}
foreach ($token in @(
        'WindowTitleBarHoverBrush', 'WindowTitleBarPressedBrush', 'WindowTitleBarAccentBrush',
        'WindowTitleBarDisabledBrush', 'WindowTitleActionBackgroundBrush',
        'WindowTitleActionHoverBrush', 'WindowTitleActionPressedBrush', 'WindowTitleActionFocusBrush',
        'DynamicResource WindowTitleBarHoverBrush', 'DynamicResource WindowTitleBarPressedBrush',
        'DynamicResource WindowTitleBarAccentBrush', 'DynamicResource WindowTitleActionBackgroundBrush')) {
    Require-Text $shellTheme $token
}
foreach ($contract in @('WindowCloseButtonStyle', 'Background="#E81123"', 'Background="#C50F1F"', 'WindowTitleBarDisabledBrush')) {
    Require-Text $shellTheme $contract "Native caption safety/state contract is missing: $contract"
}

Require-Text $smoke 'WindowTitleBarThemeManager.Project' 'Web shell smoke must exercise native preset projection.'
Require-Text $smoke 'titleBarFallback' 'Web shell smoke must verify invalid preset fallback.'

Write-Host 'Native title-bar/font/appearance contracts passed.'
