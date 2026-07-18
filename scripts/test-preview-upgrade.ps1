[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousInstallerPath,
    [Parameter(Mandatory = $true)][string]$CurrentInstallerPath,
    [string]$PreviousHostManifestPath,
    [string]$TestRoot,
    [switch]$KeepTestFiles
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$appId = '{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$markerKey = 'HKCU:\Software\QingMo-A\QingToolbox'
if ([string]::IsNullOrWhiteSpace($PreviousHostManifestPath)) {
    $PreviousHostManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\baselines\0.1.0-alpha-host-payload.json'
}
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Join-Path $env:TEMP ("QingToolbox-upgrade-" + [guid]::NewGuid().ToString('N')) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
$isCi = $env:GITHUB_ACTIONS -eq 'true'
if (-not $isCi) {
    throw 'The real upgrade test requires a disposable GitHub Actions Windows profile; local Known Folders cannot be safely redirected.'
}
$allowedTempRoots = @([IO.Path]::GetFullPath($env:TEMP))
if ($isCi -and -not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    $allowedTempRoots += [IO.Path]::GetFullPath($env:RUNNER_TEMP)
}
$isUnderAllowedTemp = $false
foreach ($root in $allowedTempRoots) {
    $prefix = $root.TrimEnd('\') + '\'
    if ($TestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $isUnderAllowedTemp = $true; break }
}
if (-not $isUnderAllowedTemp) { throw 'Upgrade TestRoot must be under a trusted temporary directory.' }
if (Test-Path $uninstallKey) { throw 'A real QingToolbox uninstall entry exists; refusing isolated upgrade test.' }
if (Get-Process QingToolbox.Shell -ErrorAction SilentlyContinue) { throw 'A QingToolbox.Shell process is running; refusing isolated upgrade test.' }
$previous = [IO.Path]::GetFullPath($PreviousInstallerPath); $current = [IO.Path]::GetFullPath($CurrentInstallerPath)
foreach($installer in @($previous,$current)){if(-not(Test-Path $installer -PathType Leaf)-or(Get-Item $installer).Length-le 0){throw "Installer is missing: $installer"}}
$install = Join-Path $TestRoot 'Custom Install\QingToolbox'; $profile = Join-Path $TestRoot 'Profile'
$logDirectory = Join-Path $TestRoot 'Installer Logs With Spaces'
$oldLocal=$env:LOCALAPPDATA; $oldRoaming=$env:APPDATA
function ConvertTo-WindowsCommandLineArgument([AllowEmptyString()][string]$Value) {
    if ($null -eq $Value) { throw 'A process argument cannot be null.' }
    $builder=[Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes=0
    foreach($character in $Value.ToCharArray()) {
        if($character-eq'\'){$backslashes++;continue}
        if($character-eq'"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
        } else {
            if($backslashes-gt 0){[void]$builder.Append(('\' * $backslashes))}
            [void]$builder.Append($character)
        }
        $backslashes=0
    }
    if($backslashes-gt 0){[void]$builder.Append(('\' * ($backslashes * 2)))}
    [void]$builder.Append('"')
    return $builder.ToString()
}
function Invoke-Setup(
    [string]$path,
    [string]$log,
    [bool]$SpecifyDirectory,
    [AllowEmptyString()][string]$Directory=$install,
    [string]$LoadInfPath,
    [string]$SaveInfPath){
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=english',("/LOG=$log"))
    if ($SpecifyDirectory) { $arguments += "/DIR=$Directory" }
    if (-not [string]::IsNullOrWhiteSpace($LoadInfPath)) { $arguments += "/LOADINF=$LoadInfPath" }
    if (-not [string]::IsNullOrWhiteSpace($SaveInfPath)) { $arguments += "/SAVEINF=$SaveInfPath" }
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$path
    $startInfo.Arguments=($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ }) -join ' '
    $startInfo.UseShellExecute=$false
    $process=$null
    try {
        $process=[Diagnostics.Process]::Start($startInfo)
        if($null-eq$process){throw 'Process.Start returned no installer process.'}
        $process.WaitForExit()
        return $process.ExitCode
    } catch {
        throw "Failed to execute installer '$path': $($_.Exception.Message)"
    } finally {
        if($null-ne$process){$process.Dispose()}
    }
}
function New-LoadInfWithDirectory([string]$SourcePath,[string]$DestinationPath,[string]$Directory) {
    if(-not(Test-Path -LiteralPath $SourcePath -PathType Leaf)){throw "Saved installer INF is missing: $SourcePath"}
    $lines=[IO.File]::ReadAllLines($SourcePath)
    $found=$false
    for($index=0;$index-lt$lines.Length;$index++){
        if($lines[$index]-match '^Dir='){$lines[$index]="Dir=$Directory";$found=$true}
    }
    if(-not$found){throw "Saved installer INF does not contain an official Dir field: $SourcePath"}
    [IO.File]::WriteAllLines($DestinationPath,$lines,[Text.UTF8Encoding]::new($false))
}
function Assert-FileVersion([string]$expected){$info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'QingToolbox.Shell.exe'));if($info.FileVersion-ne$expected){throw "Shell FileVersion mismatch: $($info.FileVersion)"};return $info}
function Wait-Until {
    param([scriptblock]$Condition,[int]$TimeoutSeconds,[string]$FailureMessage)
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do { if(& $Condition){return}; Start-Sleep -Milliseconds 200 } while([DateTimeOffset]::UtcNow-lt$deadline)
    throw $FailureMessage
}
function Get-ShellProcessesAtInstallPath([string]$InstallPath=$install) {
    $expected=[IO.Path]::GetFullPath((Join-Path $InstallPath 'QingToolbox.Shell.exe'))
    return @(Get-CimInstance Win32_Process -Filter "Name='QingToolbox.Shell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals($expected,[StringComparison]::OrdinalIgnoreCase) })
}
function Invoke-RegisteredUninstall([string]$LogPath) {
    if(-not(Test-Path $uninstallKey)){throw 'The registered QingToolbox uninstaller is missing.'}
    $uninstaller=([string](Get-ItemProperty $uninstallKey).UninstallString).Trim('"')
    $process=Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',("/LOG=`"$LogPath`"")) -Wait -PassThru
    try { if($process.ExitCode-ne 0){throw "Registered uninstall failed with exit code $($process.ExitCode)."} }
    finally { $process.Dispose() }
}
function Assert-InstallerIdentityAndShortcuts {
    param([string]$Phase)
    if (-not (Test-Path $uninstallKey)) { throw "$Phase removed the fixed Inno uninstall entry." }
    $entries = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { $_.PSChildName -eq $appId })
    if ($entries.Count -ne 1) { throw "$Phase expected one QingToolbox uninstall entry, found $($entries.Count)." }
    $currentPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $commonPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
    if ([string]::IsNullOrWhiteSpace($currentPrograms) -or [string]::IsNullOrWhiteSpace($commonPrograms)) {
        throw "$Phase could not resolve Windows Start Menu Known Folders."
    }
    $currentGroup = Join-Path $currentPrograms 'QingToolbox'
    $currentShortcuts = @(Get-ChildItem -LiteralPath $currentGroup -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($currentShortcuts.Count -ne 2 -or @($currentShortcuts.Name | Sort-Object -Unique).Count -ne 2) {
        throw "$Phase expected one application and one uninstall shortcut, found $($currentShortcuts.Count)."
    }
    $commonGroup = Join-Path $commonPrograms 'QingToolbox'
    $commonShortcuts = @(Get-ChildItem -LiteralPath $commonGroup -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($commonShortcuts.Count -ne 0) { throw "$Phase left $($commonShortcuts.Count) unexpected public Start Menu shortcuts." }
    return [pscustomobject]@{ CurrentGroup = $currentGroup; CommonGroup = $commonGroup }
}
$newShell=$null
try {
    New-Item -ItemType Directory -Path $TestRoot,$profile,$logDirectory -Force | Out-Null
    $env:LOCALAPPDATA=Join-Path $profile 'LocalAppData'; $env:APPDATA=Join-Path $profile 'AppData'
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA,$env:APPDATA -Force | Out-Null
    $savedInf=Join-Path $logDirectory 'official-saveinf-baseline.inf'
    if((Invoke-Setup $previous (Join-Path $logDirectory 'preview1-install.log') $true $install $null $savedInf)-ne 0){throw 'Preview 1 installation failed.'}
    if(-not(Test-Path -LiteralPath $savedInf -PathType Leaf)){throw 'Preview 1 installer did not create the requested SAVEINF baseline.'}
    [void](Assert-FileVersion '0.1.0.0')
    if(-not(Test-Path $uninstallKey)){throw 'Preview 1 fixed Inno uninstall entry is missing.'}
    $preview1Entry=Get-ItemProperty $uninstallKey
    if(-not [IO.Path]::GetFullPath([string]$preview1Entry.InstallLocation).TrimEnd('\').Equals(
        $install.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Preview 1 did not register the custom installation directory.'}
    $preview1Process = Start-Process -FilePath (Join-Path $install 'QingToolbox.Shell.exe') -PassThru
    Wait-Until { -not $preview1Process.HasExited } 10 'Preview 1 Shell exited before the in-place upgrade could exercise process replacement.'
    $oldPid=$preview1Process.Id
    $oldPath=[IO.Path]::GetFullPath($preview1Process.Path)
    $settings=Join-Path $env:APPDATA 'QingToolbox\settings.json'; $module=Join-Path $env:LOCALAPPDATA 'QingToolbox\Modules\sentinel\module.json'
    $data=Join-Path $env:APPDATA 'QingToolbox\Data\sentinel.dat'; $cache=Join-Path $env:LOCALAPPDATA 'QingToolbox\Cache\sentinel.cache'
    foreach($parent in @((Split-Path $settings -Parent),(Split-Path $module -Parent),(Split-Path $data -Parent),(Split-Path $cache -Parent))){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
    '{"language":"zh-CN","startupPresentationMode":0,"mainWindowCloseBehavior":1,"startupModules":[{"moduleId":"sentinel","version":"1.0.0","payloadSha256":"AA","payloadFileCount":1}]}'|Set-Content $settings -Encoding UTF8
    'module'|Set-Content $module -Encoding ASCII; 'data'|Set-Content $data -Encoding ASCII; 'cache'|Set-Content $cache -Encoding ASCII
    $unknown=Join-Path $install 'user-owned-unknown.txt'; 'unknown'|Set-Content $unknown -Encoding ASCII
    $sentinels=@($settings,$module,$data,$cache,$unknown);$hashes=@{};foreach($item in $sentinels){$hashes[$item]=(Get-FileHash $item -Algorithm SHA256).Hash}
    $upgradeExitCode=Invoke-Setup $current (Join-Path $logDirectory 'upgrade.log') $false
    if($upgradeExitCode-ne 0){throw "Preview 2 in-place upgrade without /DIR failed with exit code $upgradeExitCode."}
    Wait-Until { $preview1Process.Refresh(); $preview1Process.HasExited } 20 'Preview 1 Shell remained running after the in-place upgrade.'
    $newShellProcesses=@()
    Wait-Until { $script:newShellProcesses=@(Get-ShellProcessesAtInstallPath); $script:newShellProcesses.Count-eq 1 } 20 'Preview 2 Shell was not restored exactly once in the original custom directory.'
    $newShell=$newShellProcesses[0]
    if($newShell.ProcessId-eq$oldPid){throw 'Preview 2 Shell reused the old process ID.'}
    if(-not [IO.Path]::GetFullPath([string]$newShell.ExecutablePath).Equals($oldPath,[StringComparison]::OrdinalIgnoreCase)){throw 'Preview 2 Shell restarted from a different installation directory.'}
    $info=Assert-FileVersion '0.2.0.0';if($info.ProductVersion-notlike'0.2.0-alpha*'){throw "Shell ProductVersion mismatch: $($info.ProductVersion)"}
    $defaultInstall=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\QingToolbox'
    if(-not [IO.Path]::GetFullPath($defaultInstall).TrimEnd('\').Equals($install.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)-and
        (Test-Path -LiteralPath (Join-Path $defaultInstall 'QingToolbox.Shell.exe'))){throw 'Upgrade created a second QingToolbox installation in the default directory.'}
    if(-not(Test-Path (Join-Path $install 'host-payload.manifest.json'))){throw 'Host payload manifest is missing after upgrade.'}
    foreach($item in $sentinels){if(-not(Test-Path $item)-or(Get-FileHash $item -Algorithm SHA256).Hash-ne$hashes[$item]){throw "Upgrade did not preserve sentinel: $item"}}
    if(-not(Test-Path $uninstallKey)){throw 'Fixed Inno uninstall entry is missing after upgrade.'}
    $entry=Get-ItemProperty $uninstallKey;if($entry.DisplayVersion-ne'0.2.0-alpha'){throw "Uninstall DisplayVersion mismatch: $($entry.DisplayVersion)"}
    if ([IO.Path]::GetFullPath([string]$entry.InstallLocation).TrimEnd('\') -ne $install.TrimEnd('\')) { throw 'Upgrade registered a different installation directory.' }
    $shortcutRoots = Assert-InstallerIdentityAndShortcuts 'Upgrade'
    $previousManifest = Get-Content -LiteralPath $PreviousHostManifestPath -Raw | ConvertFrom-Json
    $currentManifest = Get-Content -LiteralPath (Join-Path $install 'host-payload.manifest.json') -Raw | ConvertFrom-Json
    $currentOwned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($owned in $currentManifest.entries) { [void]$currentOwned.Add(([string]$owned.relativePath).Replace('\','/')) }
    $obsolete = @($previousManifest.entries | Where-Object {
        -not $currentOwned.Contains(([string]$_.relativePath).Replace('\','/'))
    })
    if ($obsolete.Count -eq 0) { throw 'The Preview 1 baseline did not exercise obsolete host cleanup.' }
    foreach ($owned in $obsolete) {
        $relative = ([string]$owned.relativePath).Replace('/', '\')
        if (Test-Path -LiteralPath (Join-Path $install $relative)) { throw "Obsolete Preview 1 host file remained after upgrade: $relative" }
    }
    Stop-Process -Id $newShell.ProcessId -ErrorAction Stop
    Wait-Until { -not(Get-Process -Id $newShell.ProcessId -ErrorAction SilentlyContinue) } 10 'Preview 2 Shell did not stop before repair validation.'
    if((Invoke-Setup $current (Join-Path $logDirectory 'repair.log') $false)-ne 0){throw 'Preview 2 repair installation without /DIR failed.'}
    foreach($item in $sentinels){if(-not(Test-Path $item)-or(Get-FileHash $item -Algorithm SHA256).Hash-ne$hashes[$item]){throw "Repair did not preserve sentinel: $item"}}
    $repairEntry = Get-ItemProperty $uninstallKey
    if ($repairEntry.DisplayVersion -ne '0.2.0-alpha' -or
        [IO.Path]::GetFullPath([string]$repairEntry.InstallLocation).TrimEnd('\') -ne $install.TrimEnd('\')) {
        throw 'Repair changed the registered version or installation directory.'
    }
    [void](Assert-InstallerIdentityAndShortcuts 'Repair')
    $conflictingInstall=Join-Path $TestRoot 'Conflicting Record\QingToolbox'
    New-Item -ItemType Directory -Path $conflictingInstall -Force|Out-Null
    $conflictingShell=Join-Path $conflictingInstall 'QingToolbox.Shell.exe'
    Copy-Item -LiteralPath (Join-Path $install 'QingToolbox.Shell.exe') -Destination $conflictingShell
    $primaryHash=(Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash
    $conflictingHash=(Get-FileHash $conflictingShell -Algorithm SHA256).Hash
    Set-ItemProperty $markerKey InstallLocation $conflictingInstall
    $conflictLog=Join-Path $logDirectory 'conflict-without-dir.log'
    $conflictExitCode=Invoke-Setup $current $conflictLog $false
    if($conflictExitCode-eq 0){throw 'Installer accepted conflicting trusted records without /DIR.'}
    if(-not(Select-String -LiteralPath $conflictLog -Pattern 'conflicting valid installation records' -Quiet)){
        throw 'Conflict rejection log did not record the expected reason.'
    }
    if((Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash-ne$primaryHash){throw 'Rejected conflict changed the real installation.'}
    if((Get-FileHash $conflictingShell -Algorithm SHA256).Hash-ne$conflictingHash){throw 'Rejected conflict changed the secondary candidate.'}
    $emptyDirLog=Join-Path $logDirectory 'empty-explicit-dir.log'
    if((Invoke-Setup $current $emptyDirLog $true '')-eq 0){throw 'Installer accepted an explicitly empty /DIR.'}
    if(-not(Select-String -LiteralPath $emptyDirLog -Pattern 'Rejected empty install directory candidate from explicit /DIR' -Quiet)){
        throw 'Empty explicit /DIR rejection was not recorded.'
    }
    $unsafeDirLog=Join-Path $logDirectory 'unsafe-explicit-dir.log'
    $unsafeDirectory=[IO.Path]::GetPathRoot($TestRoot)
    if((Invoke-Setup $current $unsafeDirLog $true $unsafeDirectory)-eq 0){throw 'Installer accepted a drive root as explicit /DIR.'}
    if(-not(Select-String -LiteralPath $unsafeDirLog -Pattern 'Rejected unsafe install directory candidate from explicit /DIR' -Quiet)){
        throw 'Unsafe explicit /DIR rejection was not recorded.'
    }
    if((Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash-ne$primaryHash){throw 'Rejected explicit directories changed the real installation.'}
    if((Get-FileHash $conflictingShell -Algorithm SHA256).Hash-ne$conflictingHash){throw 'Rejected explicit directories changed the secondary candidate.'}
    $explicitOldProcess=Start-Process -FilePath (Join-Path $install 'QingToolbox.Shell.exe') -PassThru
    Wait-Until { -not $explicitOldProcess.HasExited } 10 'Shell exited before explicit /DIR process replacement could be tested.'
    $explicitOldPid=$explicitOldProcess.Id
    $explicitLog=Join-Path $logDirectory 'explicit-dir-overrides-conflict.log'
    if((Invoke-Setup $current $explicitLog $true $install)-ne 0){throw 'Explicit /DIR did not override conflicting discovered records.'}
    Wait-Until { $explicitOldProcess.Refresh(); $explicitOldProcess.HasExited } 20 'Explicit /DIR upgrade did not close the Shell from its final target directory.'
    $explicitNewShellProcesses=@()
    Wait-Until { $script:explicitNewShellProcesses=@(Get-ShellProcessesAtInstallPath); $script:explicitNewShellProcesses.Count-eq 1 } 20 'Explicit /DIR upgrade did not restore exactly one Shell from its final target directory.'
    $explicitNewShell=$explicitNewShellProcesses[0]
    if($explicitNewShell.ProcessId-eq$explicitOldPid){throw 'Explicit /DIR upgrade reused the old Shell process ID.'}
    $explicitEntry=Get-ItemProperty $uninstallKey
    $explicitMarker=Get-ItemProperty $markerKey
    foreach($registeredPath in @([string]$explicitEntry.InstallLocation,[string]$explicitMarker.InstallLocation)){
        if(-not [IO.Path]::GetFullPath($registeredPath).TrimEnd('\').Equals($install.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){
            throw 'Explicit /DIR did not unify the registered installation directory.'
        }
    }
    [void](Assert-FileVersion '0.2.0.0')
    [void](Assert-InstallerIdentityAndShortcuts 'Explicit DIR repair')
    if((Get-FileHash $conflictingShell -Algorithm SHA256).Hash-ne$conflictingHash){throw 'Explicit /DIR overwrote the secondary discovered directory.'}
    Stop-Process -Id $explicitNewShell.ProcessId -ErrorAction Stop
    Wait-Until { -not(Get-Process -Id $explicitNewShell.ProcessId -ErrorAction SilentlyContinue) } 10 'Explicit /DIR replacement Shell did not stop before downgrade validation.'

    $wizardTarget=Join-Path $TestRoot 'Wizard Selected Target\QingToolbox'
    $unsafeFinalInf=Join-Path $logDirectory 'invalid-final-directory.inf'
    New-LoadInfWithDirectory $savedInf $unsafeFinalInf ([IO.Path]::GetPathRoot($TestRoot))
    $unsafeFinalLog=Join-Path $logDirectory 'invalid-final-directory.log'
    $beforeUnsafeFinal=(Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash
    if((Invoke-Setup $current $unsafeFinalLog $false $install $unsafeFinalInf $null)-eq 0){throw 'Installer accepted an unsafe final wizard directory.'}
    if((Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash-ne$beforeUnsafeFinal){throw 'Rejected final wizard directory changed the initial installation.'}
    if(-not(Select-String -LiteralPath $unsafeFinalLog -Pattern 'Rejected the final wizard installation directory' -Quiet)){
        throw 'Unsafe final wizard directory rejection was not recorded.'
    }

    $changedFinalInf=Join-Path $logDirectory 'final-directory-changed.inf'
    New-LoadInfWithDirectory $savedInf $changedFinalInf $wizardTarget
    $initialDirectoryProcess=Start-Process -FilePath (Join-Path $install 'QingToolbox.Shell.exe') -PassThru
    Wait-Until { -not $initialDirectoryProcess.HasExited } 10 'Initial-directory Shell exited before final-directory change validation.'
    $changedFinalLog=Join-Path $logDirectory 'final-directory-changed.log'
    if((Invoke-Setup $current $changedFinalLog $false $install $changedFinalInf $null)-ne 0){throw 'Installer failed after the final wizard directory changed from A to B.'}
    $initialDirectoryProcess.Refresh()
    if($initialDirectoryProcess.HasExited){throw 'Changing the final directory incorrectly closed the Shell from the initial directory.'}
    if(@(Get-ShellProcessesAtInstallPath $wizardTarget).Count-ne 0){throw 'Changing A to a new B incorrectly auto-started the final-directory Shell.'}
    if(-not(Test-Path -LiteralPath (Join-Path $wizardTarget 'QingToolbox.Shell.exe') -PathType Leaf)){throw 'Final wizard directory B did not receive the installation.'}
    $changedEntry=Get-ItemProperty $uninstallKey
    if(-not [IO.Path]::GetFullPath([string]$changedEntry.InstallLocation).TrimEnd('\').Equals($wizardTarget.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){
        throw 'Final wizard directory B was not registered as InstallLocation.'
    }
    if(-not(Select-String -LiteralPath $changedFinalLog -Pattern 'final installation directory changed from' -Quiet)){
        throw 'Final A-to-B directory change was not recorded.'
    }
    Stop-Process -Id $initialDirectoryProcess.Id -ErrorAction Stop
    Wait-Until { -not(Get-Process -Id $initialDirectoryProcess.Id -ErrorAction SilentlyContinue) } 10 'Initial-directory Shell did not stop after A-to-B validation.'
    Invoke-RegisteredUninstall (Join-Path $logDirectory 'final-directory-changed-uninstall.log')
    if((Invoke-Setup $current (Join-Path $logDirectory 'restore-initial-registration.log') $true $install $null $null)-ne 0){throw 'Failed to restore the initial installation registration after A-to-B validation.'}

    if(Test-Path -LiteralPath $wizardTarget){Remove-Item -LiteralPath $wizardTarget -Recurse -Force}
    New-Item -ItemType Directory -Path $wizardTarget -Force|Out-Null
    Copy-Item -Path (Join-Path $install '*') -Destination $wizardTarget -Recurse -Force
    $finalDirectoryProcess=Start-Process -FilePath (Join-Path $wizardTarget 'QingToolbox.Shell.exe') -PassThru
    Wait-Until { -not $finalDirectoryProcess.HasExited } 10 'Final-directory Shell exited before running-target validation.'
    $finalDirectoryOldPid=$finalDirectoryProcess.Id
    $runningFinalInf=Join-Path $logDirectory 'final-directory-running-shell.inf'
    New-LoadInfWithDirectory $savedInf $runningFinalInf $wizardTarget
    $runningFinalLog=Join-Path $logDirectory 'final-directory-running-shell.log'
    if((Invoke-Setup $current $runningFinalLog $false $install $runningFinalInf $null)-ne 0){throw 'Installer failed while replacing the Shell from final wizard directory B.'}
    Wait-Until { $finalDirectoryProcess.Refresh(); $finalDirectoryProcess.HasExited } 20 'Final-directory B old Shell remained running after installation.'
    $finalDirectoryNewProcesses=@()
    Wait-Until { $script:finalDirectoryNewProcesses=@(Get-ShellProcessesAtInstallPath $wizardTarget); $script:finalDirectoryNewProcesses.Count-eq 1 } 20 'Final-directory B Shell was not restored exactly once.'
    $finalDirectoryNewProcess=$finalDirectoryNewProcesses[0]
    if($finalDirectoryNewProcess.ProcessId-eq$finalDirectoryOldPid){throw 'Final-directory B replacement reused the old PID.'}
    if(@(Get-ShellProcessesAtInstallPath $install).Count-ne 0){throw 'Final-directory B replacement incorrectly started the initial-directory A Shell.'}
    $wizardVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $wizardTarget 'QingToolbox.Shell.exe'))
    if($wizardVersion.FileVersion-ne'0.2.0.0'){throw "Final-directory B Shell FileVersion mismatch: $($wizardVersion.FileVersion)"}
    Stop-Process -Id $finalDirectoryNewProcess.ProcessId -ErrorAction Stop
    Wait-Until { -not(Get-Process -Id $finalDirectoryNewProcess.ProcessId -ErrorAction SilentlyContinue) } 10 'Final-directory B replacement Shell did not stop after validation.'
    Invoke-RegisteredUninstall (Join-Path $logDirectory 'final-directory-running-shell-uninstall.log')
    if((Invoke-Setup $current (Join-Path $logDirectory 'restore-initial-after-running-final.log') $true $install $null $null)-ne 0){throw 'Failed to restore the initial installation after running-final-directory validation.'}

    New-Item -Path $markerKey -Force|Out-Null;Set-ItemProperty $markerKey InstalledVersion '0.3.0-alpha'
    $before=(Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash
    $downgrade=Invoke-Setup $current (Join-Path $logDirectory 'downgrade.log') $false
    if($downgrade-eq 0){throw 'Downgrade guard accepted a newer installed version.'}
    if((Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash-ne$before){throw 'Rejected downgrade changed host files.'}
    Set-ItemProperty $markerKey InstalledVersion '0.2.0-alpha'; Remove-Item -LiteralPath $unknown -Force
    $uninstaller=(Get-ItemProperty $uninstallKey).UninstallString.Trim('"');$p=Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if($p.ExitCode-ne 0){throw 'Preview 2 uninstall failed.'}
    foreach($item in @($settings,$module,$data,$cache)){if(-not(Test-Path $item)){throw "Uninstall removed user data: $item"}}
    if(Test-Path $uninstallKey){throw 'Uninstall entry remained after uninstall.'}
    if ((Test-Path -LiteralPath $shortcutRoots.CurrentGroup) -or
        (Test-Path -LiteralPath $shortcutRoots.CommonGroup)) {
        throw 'Uninstall left a QingToolbox Start Menu group behind.'
    }
    Write-Host 'Preview upgrade, repair, DIR precedence, unsafe DIR rejection, downgrade guard, and user-state preservation passed.'
}
finally {
    foreach($process in @(Get-ShellProcessesAtInstallPath)){
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    $env:LOCALAPPDATA=$oldLocal;$env:APPDATA=$oldRoaming
    if(-not$KeepTestFiles -and (Test-Path $TestRoot)){Remove-Item -LiteralPath $TestRoot -Recurse -Force}
}
