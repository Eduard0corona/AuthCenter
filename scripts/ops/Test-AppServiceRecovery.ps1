[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$AppName,
    [Parameter(Mandatory)][ValidatePattern('^https://')][string]$BaseUrl,
    [switch]$ExecuteRestart,
    [int]$RecoveryObjectiveSeconds = 300
)

$ErrorActionPreference = 'Stop'
$target = (az webapp show --resource-group $ResourceGroup --name $AppName --query id -o tsv)
if (-not $target) { throw 'The exact App Service target could not be resolved.' }
if (-not $ExecuteRestart) { Write-Output "DRY_RUN target=$target objectiveSeconds=$RecoveryObjectiveSeconds"; return }
if (-not $PSCmdlet.ShouldProcess($target, 'Restart App Service and measure liveness recovery')) { return }

$started = [DateTimeOffset]::UtcNow
az webapp restart --resource-group $ResourceGroup --name $AppName
if ($LASTEXITCODE -ne 0) { throw 'App Service restart failed.' }
do {
    Start-Sleep -Seconds 5
    try { $status = (Invoke-WebRequest -Uri "$($BaseUrl.TrimEnd('/'))/health/live" -UseBasicParsing -TimeoutSec 10).StatusCode } catch { $status = 0 }
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
} while ($status -ne 200 -and $elapsed -lt $RecoveryObjectiveSeconds)
if ($status -ne 200) { throw "Liveness did not recover within $RecoveryObjectiveSeconds seconds." }
Write-Output "RECOVERED seconds=$([math]::Round($elapsed, 1)) objectiveSeconds=$RecoveryObjectiveSeconds"
