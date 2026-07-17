[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
function Convert-SemVer([string]$value) {
    if ($value -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>alpha|beta|rc))?$') { throw "Invalid SemVer: $value" }
    $pre = if ($Matches.ContainsKey('pre')) { $Matches['pre'] } else { '' }
    $rank = if (-not $pre) { 3 } elseif ($pre -eq 'alpha') { 0 } elseif ($pre -eq 'beta') { 1 } else { 2 }
    [pscustomobject]@{ Major=[int]$Matches.major; Minor=[int]$Matches.minor; Patch=[int]$Matches.patch; Rank=$rank }
}
function Compare-SemVer([string]$left,[string]$right) {
    $l=Convert-SemVer $left; $r=Convert-SemVer $right
    foreach($name in 'Major','Minor','Patch','Rank'){ if($l.$name -lt $r.$name){return -1}; if($l.$name -gt $r.$name){return 1} }; return 0
}
$cases=@(
    @('0.1.0-alpha','0.2.0-alpha',-1), @('0.2.0-alpha','0.2.0-alpha',0),
    @('0.2.0-alpha','0.2.0-beta',-1), @('0.2.0-beta','0.2.0',-1), @('0.2.0','0.3.0-alpha',-1))
foreach($case in $cases){if((Compare-SemVer $case[0] $case[1]) -ne $case[2]){throw "SemVer comparison failed: $($case -join ' ')"}}
$rejected=$false; try{Convert-SemVer 'broken'}catch{$rejected=$true}; if(-not $rejected){throw 'Malformed version was accepted.'}
Write-Host 'Installer SemVer downgrade guard contracts passed.'
