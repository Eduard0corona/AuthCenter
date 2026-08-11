[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$PrimaryResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$PrimaryServer,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$FailoverGroup,
    [switch]$Execute,
    [switch]$FailBack,
    [int]$RecoveryObjectiveSeconds = 300
)

$ErrorActionPreference = 'Stop'
$group = az sql failover-group show --resource-group $PrimaryResourceGroup --server $PrimaryServer --name $FailoverGroup -o json | ConvertFrom-Json
if (-not $group.id -or -not $group.partnerServers) { throw 'The exact failover group and partner could not be resolved.' }
$partnerId = $group.partnerServers[0].id
$segments = $partnerId.Trim('/').Split('/')
$partnerResourceGroup = $segments[[Array]::IndexOf($segments, 'resourceGroups') + 1]
$partnerServer = $segments[[Array]::IndexOf($segments, 'servers') + 1]
if (-not $Execute) { Write-Output "DRY_RUN group=$($group.id) partner=$partnerId failBack=$FailBack"; return }
if (-not $PSCmdlet.ShouldProcess($group.id, "Fail over to $partnerServer and measure recovery")) { return }

$started = [DateTimeOffset]::UtcNow
az sql failover-group set-primary --resource-group $partnerResourceGroup --server $partnerServer --name $FailoverGroup
if ($LASTEXITCODE -ne 0) { throw 'Failover did not complete.' }
$elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
if ($elapsed -gt $RecoveryObjectiveSeconds) { throw "Failover exceeded the $RecoveryObjectiveSeconds second RTO." }
Write-Output "FAILOVER_VERIFIED primary=$partnerServer seconds=$([math]::Round($elapsed, 1))"

if ($FailBack) {
    if (-not $PSCmdlet.ShouldProcess($group.id, "Fail back to $PrimaryServer")) { return }
    az sql failover-group set-primary --resource-group $PrimaryResourceGroup --server $PrimaryServer --name $FailoverGroup
    if ($LASTEXITCODE -ne 0) { throw 'Failback did not complete.' }
    Write-Output "FAILBACK_VERIFIED primary=$PrimaryServer"
}
