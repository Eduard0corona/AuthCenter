[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ServerName,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$DatabaseName,
    [switch]$Execute,
    [switch]$KeepRestoredDatabase,
    [int]$RecoveryObjectiveSeconds = 1800
)

$ErrorActionPreference = 'Stop'
$source = az sql db show --resource-group $ResourceGroup --server $ServerName --name $DatabaseName --query id -o tsv
if (-not $source) { throw 'The exact source database could not be resolved.' }
$restoreName = "authcenter-drill-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
if (-not $Execute) { Write-Output "DRY_RUN source=$source restoredDatabase=$restoreName autoDelete=$(-not $KeepRestoredDatabase)"; return }
if (-not $PSCmdlet.ShouldProcess($source, "Create point-in-time restore $restoreName")) { return }

$started = [DateTimeOffset]::UtcNow
try {
    az sql db restore --resource-group $ResourceGroup --server $ServerName --name $DatabaseName --dest-name $restoreName --time ([DateTime]::UtcNow.AddMinutes(-5).ToString('o')) -o none
    if ($LASTEXITCODE -ne 0) { throw 'Point-in-time restore failed.' }
    $status = az sql db show --resource-group $ResourceGroup --server $ServerName --name $restoreName --query status -o tsv
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    if ($status -ne 'Online') { throw "Restored database status is $status." }
    if ($elapsed -gt $RecoveryObjectiveSeconds) { throw "Restore exceeded the $RecoveryObjectiveSeconds second RTO." }
    Write-Output "RESTORE_VERIFIED database=$restoreName seconds=$([math]::Round($elapsed, 1)) status=$status"
}
finally {
    if (-not $KeepRestoredDatabase) {
        $resolved = az sql db show --resource-group $ResourceGroup --server $ServerName --name $restoreName --query name -o tsv 2>$null
        if ($resolved -eq $restoreName -and $restoreName.StartsWith('authcenter-drill-', [StringComparison]::Ordinal)) {
            az sql db delete --resource-group $ResourceGroup --server $ServerName --name $restoreName --yes
            Write-Output "RESTORE_CLEANUP database=$restoreName"
        }
    }
}
