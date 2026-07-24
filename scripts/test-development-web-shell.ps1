[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[int]$TimeoutSeconds=45,[switch]$NoBuild)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$probeId=[Guid]::NewGuid();$profile="WebShellProbe-$($probeId.ToString('N').Substring(0,12))"
$info=Resolve-LocalEnvironmentProfile -Environment Development -Profile $profile
$result=Join-Path $info.ProfileRoot "temp\web-shell-probe-$($probeId.ToString('D')).json"
$process=$null
try {
  Assert-NoLocalProfileReparsePoints -ProfileInfo $info
  if(-not $NoBuild){& (Join-Path $PSScriptRoot 'build-web-ui.ps1');if($LASTEXITCODE){throw 'WebUI build failed.'};dotnet build (Join-Path $info.RepoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj') -c $Configuration;if($LASTEXITCODE){throw 'Shell build failed.'}}
  $exe=Join-Path $info.RepoRoot "QingToolbox.Shell\bin\$Configuration\net10.0-windows\QingToolbox.Shell.exe"
  $arguments=@('--environment','Development','--profile',$profile,'--repo-root',$info.RepoRoot,'--web-shell-probe',$probeId.ToString('D'))
  $process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
  $deadline=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
  while(-not(Test-Path -LiteralPath $result)-and-not $process.HasExited-and[DateTimeOffset]::UtcNow-lt$deadline){Start-Sleep -Milliseconds 200;$process.Refresh()}
  if(-not(Test-Path -LiteralPath $result)){if($process.HasExited){throw "Development Web Shell exited before its probe completed (exit $($process.ExitCode))."};throw "Development Web Shell probe timed out after $TimeoutSeconds seconds."}
  $probe=Get-Content -LiteralPath $result -Raw -Encoding UTF8|ConvertFrom-Json
  if($probe.probeId-ne$probeId-or$probe.protocolVersion-ne 4-or-not$probe.navigationSucceeded-or-not$probe.readyChallengeIssued-or-not$probe.snapshotValidated-or-not$probe.activationPingSucceeded-or-not$probe.sessionTokenIssued-or-not$probe.repeatedPingSucceeded-or-not$probe.workspaceActivated-or$probe.usedMockTransport){throw "Development Web Shell probe failed; failureCode=$($probe.failureCode); navigation=$($probe.navigationSucceeded); readyChallenge=$($probe.readyChallengeIssued); snapshotValidated=$($probe.snapshotValidated); activationPing=$($probe.activationPingSucceeded); sessionToken=$($probe.sessionTokenIssued); repeatedPing=$($probe.repeatedPingSucceeded); workspaceActivated=$($probe.workspaceActivated); mock=$($probe.usedMockTransport)."}
  Write-Host 'Development Web Shell canary passed.';Write-Host "Probe ID: $probeId";Write-Host "Asset Build ID: $($probe.assetBuildId)";Write-Host 'Mock Transport: false'
} finally {
  if($process){$process.Refresh();if(-not$process.HasExited){$previous=$ErrorActionPreference;$ErrorActionPreference='SilentlyContinue';& taskkill.exe /PID $process.Id /T /F 2>$null|Out-Null;$ErrorActionPreference=$previous}}
  if(Test-Path -LiteralPath $info.ProfileRoot){Assert-NoLocalProfileReparsePoints -ProfileInfo $info -IncludeDescendants;for($attempt=1;$attempt-le 10-and(Test-Path -LiteralPath $info.ProfileRoot);$attempt++){Start-Sleep -Milliseconds (100*$attempt);Remove-Item -LiteralPath $info.ProfileRoot -Recurse -Force -ErrorAction SilentlyContinue};if(Test-Path -LiteralPath $info.ProfileRoot){throw 'Development Web Shell probe profile cleanup did not complete.'}}
}
