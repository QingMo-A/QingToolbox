# Isolated tests: replace every system reader/writer before invoking operations.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.ServiceProcess
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/system.ps1'), [Text.Encoding]::UTF8))) -LibraryOnly
$script:Values = @{}
$script:Backups = @{}
$script:Managed = $false
$script:Supported = $true
$script:Services = @{ BFE = @{ name='BFE'; status='Running'; start=2 }; MpsSvc = @{ name='MpsSvc'; status='Running'; start=2 } }
$script:ServiceCalls = [Collections.Generic.List[string]]::new()
$script:FailStart = ''
function Read-Reg($Path, $Name) { return $script:Backups[$Name] }
function Write-Reg($Path, $Name, $Value, $Kind = 'DWord') { if ($null -eq $Value) { $script:Backups.Remove($Name) } else { $script:Backups[$Name] = $Value } }
function Test-Managed { return $script:Managed }
function Test-SupportedEdition { return $script:Supported }
function Get-Update { return $script:Values['update'] }
function Set-Update($Value) { $script:Values['update'] = $Value }
function Get-LocalFirewall($Profile) { return $script:Values["firewall.$Profile"] }
function Set-LocalFirewall($Profile, $Value) { $script:Values["firewall.$Profile"] = $Value }
function Get-ServiceInfo($Name) { return $script:Services[$Name].Clone() }
function Get-Service($Name) {
    $value = [pscustomobject]@{ Status = $script:Services[$Name].status }
    $value | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($Status, $Timeout) }
    return $value
}
function Set-Service($Name, $StartupType) { $script:ServiceCalls.Add("configure:$Name"); $script:Services[$Name].start = 2 }
function Start-Service($Name) { $script:ServiceCalls.Add("start:$Name"); if ($script:FailStart -eq $Name) { throw 'mock_failure' }; $script:Services[$Name].status = 'Running' }
function Assert($Condition, $Message) { if (-not $Condition) { throw "ASSERTION: $Message" } }
function Expect-Error([string]$Token, [string]$Expected) {
    $actual = ''
    try { Invoke-Action $Token } catch { $actual = $_.Exception.Message }
    Assert ($actual -eq $Expected) "$Token expected $Expected but got $actual"
}
foreach ($original in @($null, 0, 1)) {
    $script:Values.Clear(); $script:Backups.Clear(); $script:Values['update'] = $original
    Invoke-Action 'update.disable'
    Assert ($script:Values['update'] -eq 1) 'disable sets policy 1'
    if ($original -eq 1) { Assert ($script:Backups.Count -eq 0) 'no-op creates no backup'; continue }
    Assert (Same-Setting (Read-Backup 'update').original $original) 'preserve first value including absence'
    Invoke-Action 'update.enable'
    Assert (Same-Setting (Read-Backup 'update').original $original) 'second operation preserves first backup'
    Invoke-Action 'update.restore'
    Assert (Same-Setting $script:Values['update'] $original) 'restore exact original'
    Assert ($script:Backups.Count -eq 0) 'clear backup only after restore'
}
$script:Values.Clear(); $script:Backups.Clear(); $script:Values['update'] = 0
Invoke-Action 'update.disable'
$script:Values['update'] = 99
Expect-Error 'update.restore' 'setting_conflict'
Assert ($script:Values['update'] -eq 99 -and $script:Backups.Count -eq 1) 'external edits not overwritten'
foreach ($profile in @('Domain', 'Private', 'Public')) {
    foreach ($original in @(0, 1, 2)) {
        $script:Values.Clear(); $script:Backups.Clear(); $script:Values["firewall.$profile"] = $original
        Invoke-Action "firewall.$profile.disable"
        Assert ($script:Values["firewall.$profile"] -eq 0) 'only requested profile disabled'
        Assert ($script:Values.Count -eq 1) 'other profiles untouched'
        if ($original -eq 0) { continue }
        Invoke-Action "firewall.$profile.restore"
        Assert ($script:Values["firewall.$profile"] -eq $original) 'restore including NotConfigured'
    }
}
$script:Managed = $true
Expect-Error 'update.disable' 'managed_device'
Expect-Error 'firewall.Public.disable' 'managed_device'
$script:Managed = $false; $script:Supported = $false
Expect-Error 'update.disable' 'unsupported_edition'
$script:Supported = $true
Expect-Error 'update.disable; whoami' 'invalid_action'
Expect-Error 'firewall.All.disable' 'invalid_action'
$script:Values.Clear(); $script:Backups.Clear(); $script:Values['update'] = 0
Store-Backup 'update' $null 1
$script:Values['update'] = $null # simulated crash before applying
Invoke-Action 'update.restore'
Assert ($null -eq $script:Values['update'] -and $script:Backups.Count -eq 0) 'recover interrupted operation'
$script:Backups['update'] = '{"version":1,"original":9,"applied":1}'
Expect-Error 'update.restore' 'backup_invalid'
$script:Values.Clear(); $script:Backups.Clear(); $script:Values['update'] = 0
function Set-Update($Value) { } # simulated OS rejects the write without an error
Expect-Error 'update.disable' 'verify_failed'
Assert ($script:Backups.Count -eq 1) 'retain backup when read-back fails'
$script:Backups.Clear(); $script:ServiceCalls.Clear()
$script:Services.BFE = @{ name='BFE'; status='Stopped'; start=4 }
$script:Services.MpsSvc = @{ name='MpsSvc'; status='Stopped'; start=4 }
Expect-Error 'firewall.Public.enable' 'firewall_service_unavailable'
$script:Managed = $true; Expect-Error 'firewall.repair' 'managed_device'; $script:Managed = $false
Assert ($script:ServiceCalls.Count -eq 0) 'no repair without consent/eligibility'
Invoke-Action 'firewall.repair'
Assert (($script:ServiceCalls -join ',') -eq 'configure:BFE,start:BFE,configure:MpsSvc,start:MpsSvc') 'repair dependencies before firewall'
Assert ($script:Services.MpsSvc.status -eq 'Running' -and $script:Services.MpsSvc.start -eq 2) 'repair stopped and disabled service'
Assert ($script:Backups.ContainsKey('serviceRepair.before')) 'repair diagnostic snapshot preserved'
$originalServiceBackup = $script:Backups['serviceRepair.before']
$script:ServiceCalls.Clear(); Invoke-Action 'firewall.repair'
Assert ($script:ServiceCalls.Count -eq 0) 'repair is idempotent and never restarts healthy services'
Assert ($script:Backups['serviceRepair.before'] -eq $originalServiceBackup) 'first service snapshot is not overwritten'
$script:Services.MpsSvc.status = 'Stopped'; $script:FailStart = 'MpsSvc'
Expect-Error 'firewall.repair' 'service_repair_failed'
Assert ($script:Services.MpsSvc.status -eq 'Stopped') 'failed start not reported as success'
Expect-Error 'firewall.repair;stop-service' 'invalid_action'
function Get-Edition { return 'Professional' }
function Get-NetFirewallProfile { throw 'must not query unavailable service' }
$script:Backups.Clear(); $script:Values.Clear()
$script:Backups['EnableFirewall'] = 0
$snapshot = Get-Snapshot
Assert ($snapshot.firewall.Count -eq 3) 'all saved profiles still appear when service stopped'
Assert ($snapshot.firewallServices.Count -eq 2) 'both fixed services diagnosed'
foreach ($profile in $snapshot.firewall) {
    Assert ($profile.source -eq 'saved' -and $null -eq $profile.enabled -and $profile.local -eq 0 -and $null -eq $profile.error) 'saved disabled state is not misreported as effective or repeated errors'
}
Assert ($script:ServiceCalls.Count -eq 1) 'reading stopped service never repairs it implicitly'
Write-Host 'PASS: update/firewall backup and restore, service repair order, no-op, failure and saved-state fallback (mocked; no system settings changed).'
