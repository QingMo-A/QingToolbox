param([string]$Action = 'snapshot', [switch]$LibraryOnly)
# This script is embedded in the executable. Only whitelisted action tokens enter
# the elevated process; no paths, commands or restore documents come from WebView.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$BackupPath = 'SOFTWARE\QingToolbox.WindowsTools\Backups'
$UpdatePath = 'SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'

function Read-Reg([string]$Path, [string]$Name) {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Path)
    if (-not $key) { return $null }
    try { return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
function Write-Reg([string]$Path, [string]$Name, $Value, [string]$Kind = 'DWord') {
    $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($Path)
    try {
        if ($null -eq $Value) { $key.DeleteValue($Name, $false) }
        else { $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::$Kind) }
    } finally { $key.Dispose() }
}
function Read-Backup([string]$Name) {
    $raw = Read-Reg $BackupPath $Name
    if ($null -eq $raw) { return $null }
    $b = $raw | ConvertFrom-Json
    if ($b.version -ne 1 -or $b.applied -notin @(0, 1) -or
        ($null -ne $b.original -and $b.original -notin @(0, 1, 2))) { throw 'backup_invalid' }
    return $b
}
function Store-Backup([string]$Name, $Original, [int]$Applied) {
    Write-Reg $BackupPath $Name (@{ version = 1; original = $Original; applied = $Applied } | ConvertTo-Json -Compress) 'String'
}
function Same-Setting($Left, $Right) {
    if ($null -eq $Left -or $null -eq $Right) { return $null -eq $Left -and $null -eq $Right }
    return [int]$Left -eq [int]$Right
}
function Get-Edition { return [string](Read-Reg 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'EditionID') }
function Test-SupportedEdition {
    return (Get-Edition) -match '^(Professional|Enterprise|Education|Server|IoTEnterprise)'
}
function Test-Managed {
    if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain) { return $true }
    $enrollments = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Enrollments')
    if ($enrollments) {
        try {
            foreach ($name in $enrollments.GetSubKeyNames()) {
                $key = $enrollments.OpenSubKey($name)
                # Windows also keeps active enrollment records for ordinary
                # Microsoft accounts. State=1 alone does NOT mean MDM managed.
                try {
                    if ($key.GetValue('EnrollmentState', 0) -eq 1 -and
                        ($key.GetValue('DiscoveryServiceFullURL', '') -or $key.GetValue('DMPCertThumbPrint', ''))) { return $true }
                }
                finally { $key.Dispose() }
            }
        } finally { $enrollments.Dispose() }
    }
    return $false
}
function Get-Update {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($UpdatePath)
    if (-not $key) { return $null }
    try {
        $value = $key.GetValue('NoAutoUpdate', $null)
        if ($null -ne $value -and $key.GetValueKind('NoAutoUpdate') -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'setting_conflict' }
        return $value
    } finally { $key.Dispose() }
}
function Set-Update($Value) { Write-Reg $UpdatePath 'NoAutoUpdate' $Value }
function Get-LocalFirewall([string]$Profile) {
    return [int](Get-NetFirewallProfile -PolicyStore PersistentStore -Name $Profile).Enabled
}
function Set-LocalFirewall([string]$Profile, [int]$Value) {
    Set-NetFirewallProfile -PolicyStore PersistentStore -Profile $Profile -Enabled $Value
}
function Get-ServiceInfo([string]$Name) {
    if ($Name -notin @('BFE', 'MpsSvc')) { throw 'invalid_action' }
    $service = Get-Service -Name $Name
    return @{ name = $Name; status = $service.Status.ToString(); start = (Read-Reg "SYSTEM\CurrentControlSet\Services\$Name" 'Start') }
}
function Repair-FirewallServices {
    $before = @((Get-ServiceInfo 'BFE'), (Get-ServiceInfo 'MpsSvc'))
    # Diagnostic backup only: deliberately no operation that stops these core
    # services again. Profile restore remains separate from service repair.
    if ($null -eq (Read-Reg $BackupPath 'serviceRepair.before')) {
        Write-Reg $BackupPath 'serviceRepair.before' (@{ version = 1; services = $before } | ConvertTo-Json -Depth 4 -Compress) 'String'
    }
    foreach ($name in @('BFE', 'MpsSvc')) {
        try {
            $info = Get-ServiceInfo $name
            if ($info.start -eq 4) { Set-Service -Name $name -StartupType Automatic }
            if ($info.status -ne 'Running') {
                Start-Service -Name $name
                (Get-Service -Name $name).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15))
            }
            if ((Get-ServiceInfo $name).status -ne 'Running') { throw 'verify_failed' }
        } catch { throw 'service_repair_failed' }
    }
}
function Invoke-Change([string]$Name, $Current, [int]$Target, [scriptblock]$Setter) {
    $backup = Read-Backup $Name
    if ($backup) {
        # Also accept the original value: a previous process may have exited
        # after saving the backup, before applying, or during restoration.
        if (-not (Same-Setting $Current $backup.applied) -and -not (Same-Setting $Current $backup.original)) { throw 'setting_conflict' }
        $original = $backup.original
    } else { $original = $Current }
    if (Same-Setting $Current $Target) { return }
    Store-Backup $Name $original $Target
    & $Setter $Target
}
function Invoke-Restore([string]$Name, $Current, [scriptblock]$Setter) {
    $backup = Read-Backup $Name
    if (-not $backup) { throw 'backup_missing' }
    if (-not (Same-Setting $Current $backup.applied) -and -not (Same-Setting $Current $backup.original)) { throw 'setting_conflict' }
    if (-not (Same-Setting $Current $backup.original)) { & $Setter $backup.original }
    Write-Reg $BackupPath $Name $null
}
function Invoke-Action([string]$Token) {
    if ($Token -notmatch '^(update\.(disable|enable|restore)|firewall\.repair|firewall\.(Domain|Private|Public)\.(disable|enable|restore))$') { throw 'invalid_action' }
    # Domain/MDM policy belongs to the organization's administrator, not us.
    if (Test-Managed) { throw 'managed_device' }
    if ($Token -eq 'firewall.repair') { Repair-FirewallServices; return }
    $parts = $Token.Split('.')
    if ($parts[0] -eq 'update') {
        if (-not (Test-SupportedEdition)) { throw 'unsupported_edition' }
        $current = Get-Update
        if ($null -ne $current -and $current -notin @(0, 1)) { throw 'setting_conflict' }
        if ($parts[1] -eq 'restore') { Invoke-Restore 'update' $current { param($v) Set-Update $v; if (-not (Same-Setting (Get-Update) $v)) { throw 'verify_failed' } } }
        else { Invoke-Change 'update' $current ([int]($parts[1] -eq 'disable')) { param($v) Set-Update $v; if (-not (Same-Setting (Get-Update) $v)) { throw 'verify_failed' } } }
    } else {
        if ((Get-ServiceInfo 'MpsSvc').status -ne 'Running') { throw 'firewall_service_unavailable' }
        $profile = $parts[1]
        $name = "firewall.$profile"
        $current = Get-LocalFirewall $profile
        if ($parts[2] -eq 'restore') { Invoke-Restore $name $current { param($v) Set-LocalFirewall $profile $v; if (-not (Same-Setting (Get-LocalFirewall $profile) $v)) { throw 'verify_failed' } } }
        else { Invoke-Change $name $current ([int]($parts[2] -eq 'enable')) { param($v) Set-LocalFirewall $profile $v; if (-not (Same-Setting (Get-LocalFirewall $profile) $v)) { throw 'verify_failed' } } }
    }
}
function Get-Snapshot {
    $problems = [Collections.Generic.List[string]]::new()
    $edition = Get-Edition
    $managed = $true
    try { $managed = Test-Managed } catch { $problems.Add('无法确认组织管理状态；为安全起见暂不允许修改。') }
    $update = $null; $updateBackup = $false; $updateKnown = $false
    $firewallService = 'unknown'
    $services = @()
    foreach ($name in @('BFE', 'MpsSvc')) {
        try { $services += Get-ServiceInfo $name }
        catch { $services += @{ name = $name; status = 'unknown'; start = $null } }
    }
    $firewallService = ($services | Where-Object name -eq 'MpsSvc').status
    try { $update = Get-Update; $updateBackup = $null -ne (Read-Backup 'update'); $updateKnown = $true }
    catch { $problems.Add('无法读取自动更新策略或备份。') }
    $firewall = @()
    foreach ($profile in @('Domain', 'Private', 'Public')) {
        try {
            if ($firewallService -ne 'Running') {
                $key = @{ Domain = 'DomainProfile'; Private = 'StandardProfile'; Public = 'PublicProfile' }[$profile]
                $saved = Read-Reg "SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\$key" 'EnableFirewall'
                $firewall += @{ name = $profile; enabled = $null; local = $saved; hasBackup = ($null -ne (Read-Backup "firewall.$profile")); error = $null; source = 'saved' }
                continue
            }
            $effective = [int](Get-NetFirewallProfile -PolicyStore ActiveStore -Name $profile).Enabled
            $local = Get-LocalFirewall $profile
            $firewall += @{ name = $profile; enabled = ($effective -eq 1); local = $local; hasBackup = ($null -ne (Read-Backup "firewall.$profile")); error = $null }
        } catch { $firewall += @{ name = $profile; enabled = $null; local = $null; hasBackup = $false; error = '无法读取此配置，请检查 Windows 防火墙服务是否可用。' } }
    }
    return @{ edition = $edition; managed = $managed; update = @{ known = $updateKnown; noAutoUpdate = $update; supported = (Test-SupportedEdition); hasBackup = $updateBackup }; firewall = $firewall; firewallService = $firewallService; firewallServices = $services; problems = @($problems.ToArray()) }
}
if ($LibraryOnly) { return }
# Restrict module auto-loading to the Windows-supplied modules, particularly
# when elevated. Never load a user's PowerShell profile or per-user modules.
$env:PSModulePath = Join-Path $PSHOME 'Modules'
if ($Action -eq 'snapshot') {
    try { Get-Snapshot | ConvertTo-Json -Depth 8 -Compress; exit 0 }
    catch { exit 20 }
}
$mutex = $null; $owned = $false
try {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { exit 21 }
    $mutex = [Threading.Mutex]::new($false, 'Global\QingToolbox.WindowsTools.v1')
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { exit 22 }
    Invoke-Action $Action
    exit 0
} catch {
    switch ($_.Exception.Message) {
        'managed_device' { exit 23 }
        'unsupported_edition' { exit 24 }
        'setting_conflict' { exit 25 }
        'backup_missing' { exit 26 }
        'backup_invalid' { exit 27 }
        'verify_failed' { exit 28 }
        'firewall_service_unavailable' { exit 29 }
        'service_repair_failed' { exit 30 }
        default { exit 20 }
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
