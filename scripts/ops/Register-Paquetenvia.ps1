#Requires -Version 7.2
<#
.SYNOPSIS
    Registers Paquetenvia in AuthCenter (OPS-16): its application, its confidential OAuth client and
    the client secret in Paquetenvia's Key Vault.

.DESCRIPTION
    The values are those of Paquetenvia's docs/development/auth-001-authcenter-bff.md, sections 10
    and 10.1. The script signs in as an AuthCenter administrator, reads what already exists, prints
    the plan and applies it after confirmation. Run again, it changes only what drifted.

    The client secret goes from AuthCenter's response straight into Key Vault: it is never printed,
    logged or written anywhere else. A vault that denies public traffic, as Paquetenvia's pilot vault
    does, gets a temporary rule for this machine's IP that is removed before the script ends, also
    under -WhatIf. Last, the script checks that the stored secret authenticates the client.

    Requires the Azure CLI signed in (az login) with Key Vault Secrets Officer on the vault, and
    permission to change its network rules when the vault denies public traffic.

.EXAMPLE
    ./scripts/ops/Register-Paquetenvia.ps1 -AdminEmail admin@example.com -GrantAdminAccess

.EXAMPLE
    ./scripts/ops/Register-Paquetenvia.ps1 -AdminEmail admin@example.com -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Prompt')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prompt')][string]$AdminEmail,
    [Parameter(Mandatory, ParameterSetName = 'Credential')][pscredential]$Credential,
    # A current authenticator code. Without it the script asks when AuthCenter requires a second factor.
    [ValidatePattern('^\d{6}$')][string]$MfaCode,
    [ValidateSet('Production', 'Dev')][string]$Environment = 'Production',
    [ValidatePattern('^(https://[^/?#@\s]+|http://(localhost|127\.0\.0\.1)(:\d+)?)/?$')]
    [string]$AuthCenterUrl = 'https://authcenter.info',
    # Paquetenvia's Key Vault. Without it, the only vault in -ResourceGroup.
    [ValidatePattern('^[a-zA-Z0-9-]{3,24}$')][string]$KeyVaultName,
    [ValidatePattern('^[-\w.()]{1,90}$')][string]$ResourceGroup = 'rg-pv-pilot',
    [string]$Subscription,
    [ValidatePattern('^[a-zA-Z0-9-]{1,127}$')][string]$SecretName = 'authcenter-paquetenvia-client-secret',
    # Also give the signed-in administrator access to Paquetenvia: open registration only covers new accounts.
    [switch]$GrantAdminAccess,
    # Issue a new client secret even when Key Vault already has one.
    [switch]$RotateSecret
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$applicationCode = 'PAQUETENVIA'
$applicationName = 'Paquetenvia'
$authCenter = $AuthCenterUrl.TrimEnd('/')
$target = @{
    Production = @{ Origin = 'https://paquetenvia.com'; ClientId = 'paquetenvia-web-prod'; Name = 'Paquetenvia web (production)' }
    Dev        = @{ Origin = 'https://dev.paquetenvia.com'; ClientId = 'paquetenvia-web-dev'; Name = 'Paquetenvia web (dev)' }
}[$Environment]
$origin = $target.Origin
$clientId = $target.ClientId

$desiredSettings = [ordered]@{
    registrationMode         = 'Open'
    allowPasswordLogin       = $true
    allowMagicLink           = $true
    allowGoogleLogin         = $false
    allowMicrosoftLogin      = $false
    allowGitHubLogin         = $false
    allowAppleLogin          = $false
    # Paquetenvia attaches pending memberships to the email of a verified ID token: AuthCenter must
    # never assert email_verified for an address nobody confirmed.
    requireEmailConfirmation = $true
    # Privileged Paquetenvia roles step up with acr_values (section 14.3); nobody else needs MFA.
    requireMfa               = $false
    allowedEmailDomains      = $null
    defaultRoleId            = $null
}
$desiredClient = [ordered]@{
    displayName                      = $target.Name
    redirectUris                     = @("$origin/signin-authcenter")
    allowedScopes                    = @('openid', 'profile', 'email', 'offline_access')
    grantTypes                       = @('authorization_code', 'refresh_token')
    loginUrl                         = "$authCenter/login"
    postLogoutRedirectUris           = @("$origin/login")
    # The browser never calls AuthCenter's token endpoints: the Paquetenvia API does (BFF).
    allowedCorsOrigins               = @()
    backchannelLogoutUri             = "$origin/auth/backchannel-logout"
    backchannelLogoutSessionRequired = $true
    accessTokenLifetimeSeconds       = 900
    requirePkce                      = $true
    autoConsent                      = $true
}

$script:accessToken = $null

function Get-Field([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $Object -is [string]) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Join-Body([System.Collections.IDictionary[]]$Parts) {
    $body = @{}
    foreach ($part in $Parts) { foreach ($key in $part.Keys) { $body[$key] = $part[$key] } }
    return $body
}

function Invoke-AuthCenter {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [hashtable]$Headers = @{},
        # Statuses returned to the caller instead of failing, such as 404 for a lookup.
        [int[]]$Allow = @()
    )
    $requestHeaders = @{} + $Headers
    if ($script:accessToken) { $requestHeaders.Authorization = "Bearer $($script:accessToken)" }
    $request = @{
        Method             = $Method
        Uri                = "$authCenter$Path"
        Headers            = $requestHeaders
        SkipHttpErrorCheck = $true
        StatusCodeVariable = 'status'
        TimeoutSec         = 30
    }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 5 -Compress
    }
    $response = Invoke-RestMethod @request
    if (($status -ge 200 -and $status -lt 300) -or $status -in $Allow) {
        return [pscustomobject]@{ Status = $status; Data = Get-Field $response 'data' }
    }
    $code = Get-Field $response 'errorCode'
    $message = if ($status -eq 403) { 'the administrator lacks the permission this step needs.' } else { Get-Field $response 'message' }
    if (-not $code -and $response -is [string]) { $message = ($response -replace '<[^>]+>', ' ' -replace '\s+', ' ').Trim() }
    throw "AuthCenter answered $status to $Method $($Path -replace '\?.*$', ''): $code $message".TrimEnd()
}

function Invoke-AzureCli {
    $arguments = @($args)
    if ($Subscription) { $arguments += @('--subscription', $Subscription) }
    $output = & az @arguments
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed (exit code $LASTEXITCODE): az $($args[0..2] -join ' ')" }
    return $output
}

function Get-Drift([object]$Current, [System.Collections.IDictionary]$Desired) {
    foreach ($name in $Desired.Keys) {
        $want = $Desired[$name]
        $have = Get-Field $Current $name
        if ($want -is [array]) {
            $same = (@($have | Sort-Object) -join "`n") -ceq (@($want | Sort-Object) -join "`n")
            $have = @($have) -join ', '
            $want = @($want) -join ', '
        }
        else {
            $same = "$have" -ceq "$want"
        }
        if (-not $same) { "$name '$have' -> '$want'" }
    }
}

function Save-ClientSecretInVault([string]$Secret) {
    # The CLI reads the value from a file so that it never appears in a command line.
    $file = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($file, $Secret, [System.Text.UTF8Encoding]::new($false))
        for ($attempt = 1; ; $attempt++) {
            try {
                return Invoke-AzureCli keyvault secret set --vault-name $KeyVaultName --name $SecretName `
                    --file $file --encoding utf-8 --content-type 'text/plain' `
                    --tags "authcenter-client=$clientId" --query id --output tsv
            }
            catch {
                if ($attempt -ge 3) { throw }
                Start-Sleep -Seconds 10
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    }
}

function Test-StoredSecret {
    # A made-up token is simply inactive for an authenticated client; a wrong secret answers 401
    # invalid_client. The value is read into memory only for this request.
    $stored = "$(Invoke-AzureCli keyvault secret show --vault-name $KeyVaultName --name $SecretName --query value --output tsv)"
    $status = 0
    try {
        $basic = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(
                [Uri]::EscapeDataString($clientId) + ':' + [Uri]::EscapeDataString($stored.Trim())))
        $answer = Invoke-RestMethod -Method Post -Uri "$authCenter/oauth/introspect" -Headers @{ Authorization = "Basic $basic" } `
            -ContentType 'application/x-www-form-urlencoded' -Body @{ token = [guid]::NewGuid().ToString('N') } `
            -SkipHttpErrorCheck -StatusCodeVariable 'status' -TimeoutSec 30
    }
    finally {
        $stored = $null
        $basic = $null
    }
    return $status -eq 200 -and (Get-Field $answer 'active') -eq $false
}

function Format-Plan([object]$Current, [string[]]$Changes) {
    if (-not $Current) { return 'create' }
    if ($Changes) { return 'update ' + ($Changes -join '; ') }
    return 'up to date'
}

# --- Discovery: the issuer that Paquetenvia compares ordinally (AuthCenter__Issuer).
$discovery = Invoke-RestMethod -Uri "$authCenter/.well-known/openid-configuration" -TimeoutSec 30
$issuer = Get-Field $discovery 'issuer'
if ($issuer -cne $authCenter) {
    throw "AuthCenter's discovery names the issuer '$issuer', not $authCenter. Fix Jwt:Issuer and Oidc:PublicOrigin first."
}

# --- Key Vault: reachable before anything changes, so that a new secret always has a place to go.
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Install the Azure CLI and sign in with az login.' }
if (-not $KeyVaultName) {
    $vaults = @(Invoke-AzureCli keyvault list --resource-group $ResourceGroup --query '[].name' --output tsv)
    if ($vaults.Count -ne 1) { throw "Resource group $ResourceGroup has $($vaults.Count) Key Vaults; pass -KeyVaultName." }
    $KeyVaultName = "$($vaults[0])".Trim()
}
$temporaryRule = $null
$refreshToken = $null
try {
    $defaultAction = "$(Invoke-AzureCli keyvault show --name $KeyVaultName --query 'properties.networkAcls.defaultAction' --output tsv)".Trim()
    if ($defaultAction -eq 'Deny') {
        $ip = "$(Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15)".Trim()
        if ($ip -notmatch '^(\d{1,3}\.){3}\d{1,3}$') { throw "Cannot determine this machine's public IPv4 address." }
        Write-Host "Key Vault $KeyVaultName denies public traffic: adding a temporary rule for $ip."
        Invoke-AzureCli keyvault network-rule add --name $KeyVaultName --ip-address "$ip/32" --output none | Out-Null
        $temporaryRule = "$ip/32"
    }
    # Names only: listing never returns secret values. Firewall changes take a while to apply.
    $secretNames = $null
    for ($attempt = 1; $null -eq $secretNames; $attempt++) {
        try { $secretNames = @(Invoke-AzureCli keyvault secret list --vault-name $KeyVaultName --query '[].name' --output tsv 2>$null) }
        catch {
            if ($attempt -ge 20) { throw "Key Vault $KeyVaultName does not answer; check your role on its secrets and its firewall." }
            Start-Sleep -Seconds 15
        }
    }
    $secretExists = @($secretNames | ForEach-Object { "$_".Trim() }) -ccontains $SecretName

    # --- Sign in as an AuthCenter administrator.
    if (-not $Credential) {
        $Credential = [pscredential]::new($AdminEmail, (Read-Host -Prompt "AuthCenter password for $AdminEmail" -AsSecureString))
    }
    $signIn = (Invoke-AuthCenter POST '/api/auth/login' @{
            email           = $Credential.UserName
            password        = $Credential.GetNetworkCredential().Password
            applicationCode = 'AUTHCENTER'
        }).Data
    if (Get-Field $signIn 'forcedChangePendingToken') {
        throw 'AuthCenter requires a new password for this account: change it at /login first.'
    }
    $pendingToken = Get-Field $signIn 'mfaPendingToken'
    if ($pendingToken) {
        $code = if ($MfaCode) { $MfaCode } else {
            [System.Net.NetworkCredential]::new('', (Read-Host -Prompt 'Authenticator code (or a backup code)' -AsSecureString)).Password.Trim()
        }
        $verification = @{ mfaPendingToken = $pendingToken; trustDevice = $false }
        if ($code -match '^\d{6}$') { $verification.totpCode = $code } else { $verification.backupCode = $code }
        $signIn = (Invoke-AuthCenter POST '/api/auth/mfa/verify' $verification).Data
    }
    $script:accessToken = Get-Field $signIn 'accessToken'
    $refreshToken = Get-Field $signIn 'refreshToken'
    $administrator = Get-Field $signIn 'user'
    if (-not $script:accessToken -or -not $administrator) { throw 'AuthCenter did not complete the sign-in.' }

    # --- Current state.
    $application = $null
    for ($page = 1; ; $page++) {
        $list = (Invoke-AuthCenter GET "/api/applications?page=$page&pageSize=100").Data
        $match = @($list.items | Where-Object { $_.code -ceq $applicationCode })
        if ($match) { $application = (Invoke-AuthCenter GET "/api/applications/$($match[0].id)").Data; break }
        if ($page * 100 -ge $list.totalCount) { break }
    }
    $lookup = Invoke-AuthCenter GET "/api/oauth/clients/$clientId" -Allow 404
    $client = if ($lookup.Status -eq 404) { $null } else { $lookup.Data }
    if ($client -and (-not $application -or $client.applicationSystemId -ne $application.id)) {
        throw "OAuth client $clientId already belongs to application $($client.applicationCode); fix it in the console."
    }
    if ($application -and -not $application.isActive) {
        throw "Application $applicationCode is deactivated. Reactivating it is a deliberate decision: use the console."
    }
    if ($client -and -not $client.isActive) {
        throw "OAuth client $clientId is deactivated. Reactivating it is a deliberate decision: use the console."
    }

    $applicationChanges = @()
    if ($application) {
        if ($application.name -cne $applicationName) { $applicationChanges += "name '$($application.name)' -> '$applicationName'" }
        $applicationChanges += @(Get-Drift $application.registrationSettings $desiredSettings)
    }
    $clientChanges = if ($client) { @(Get-Drift $client $desiredClient) } else { @() }
    $secretAction = if (-not $client) { 'store the new client''s secret' }
    elseif ($RotateSecret) { 'rotate (requested)' }
    elseif (-not $secretExists) { 'rotate (missing in Key Vault)' }
    elseif (-not (Test-StoredSecret)) { 'rotate (the stored secret does not authenticate the client)' }
    else { $null }
    $grantAccess = $false
    if ($GrantAdminAccess) {
        $account = (Invoke-AuthCenter GET "/api/users/$($administrator.id)").Data
        $grantAccess = -not @($account.applicationAccesses | Where-Object { $_.applicationCode -ceq $applicationCode -and $_.isActive })
    }

    Write-Host ''
    Write-Host "AuthCenter     $authCenter (issuer $issuer), signed in as $($administrator.email)"
    Write-Host "Application    ${applicationCode}: $(Format-Plan $application $applicationChanges)"
    Write-Host "OAuth client   ${clientId}: $(Format-Plan $client $clientChanges)"
    Write-Host "Client secret  $KeyVaultName/${SecretName}: $(if ($secretAction) { $secretAction } else { 'present, unchanged' })"
    Write-Host "Admin access   $(if ($grantAccess) { 'grant' } elseif ($GrantAdminAccess) { 'already granted' } else { 'not requested' })"
    Write-Host ''

    $pending = (-not $application) -or $applicationChanges -or (-not $client) -or $clientChanges -or $secretAction -or $grantAccess
    if ($pending -and -not $PSCmdlet.ShouldProcess("$authCenter and Key Vault $KeyVaultName", 'Apply the Paquetenvia registration plan')) {
        return
    }

    # --- Apply.
    if (-not $application) {
        $body = Join-Body @(@{ code = $applicationCode; name = $applicationName; description = 'Paquetenvia web platform.' }, $desiredSettings)
        $application = (Invoke-AuthCenter POST '/api/applications' $body -Headers @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }).Data
        Write-Host "Created application $applicationCode."
    }
    elseif ($applicationChanges) {
        $body = Join-Body @(@{ version = $application.version; name = $applicationName; description = $application.description }, $desiredSettings)
        $application = (Invoke-AuthCenter PUT "/api/applications/$($application.id)" $body).Data
        Write-Host "Updated application $applicationCode."
    }

    $secret = $null
    if (-not $client) {
        # The same key makes a retried request return the first response, secret included, instead
        # of failing on the client that an attempt whose answer was lost already created.
        $key = [guid]::NewGuid().ToString()
        $body = Join-Body @(@{ applicationSystemId = $application.id; clientId = $clientId; clientType = 0 }, $desiredClient)
        for ($attempt = 1; ; $attempt++) {
            try { $created = (Invoke-AuthCenter POST '/api/oauth/clients' $body -Headers @{ 'Idempotency-Key' = $key }).Data; break }
            catch {
                if ($attempt -ge 3 -or "$_" -match 'answered 4\d\d') { throw }
                Start-Sleep -Seconds 5
            }
        }
        $secret = $created.clientSecret
        Write-Host "Created OAuth client $clientId."
    }
    else {
        if ($clientChanges) {
            $body = Join-Body @(@{ version = $client.version; isActive = $true }, $desiredClient)
            $client = (Invoke-AuthCenter PUT "/api/oauth/clients/$clientId" $body).Data
            Write-Host "Updated OAuth client $clientId."
        }
        if ($secretAction) {
            $purpose = 'admin.oauth-client.rotate-secret'
            $proof = (Invoke-AuthCenter POST '/api/auth/reauth/password' @{
                    purpose  = $purpose
                    password = $Credential.GetNetworkCredential().Password
                }).Data.proofToken
            $secret = (Invoke-AuthCenter POST "/api/oauth/clients/$clientId/rotate-secret" -Headers @{
                    'X-AuthCenter-Reauthentication' = $proof
                    'Idempotency-Key'               = [guid]::NewGuid().ToString()
                }).Data.clientSecret
            Write-Host "Rotated the secret of $clientId."
        }
    }
    if ($secret) {
        try { $secretId = Save-ClientSecretInVault $secret }
        catch { throw "The client secret could not be stored in $KeyVaultName ($_). Run the script again: it issues a new one." }
        finally { $secret = $null }
        Write-Host "Stored the client secret in Key Vault: $secretId"
    }

    if ($grantAccess) {
        Invoke-AuthCenter POST "/api/users/$($administrator.id)/applications/$($application.id)" | Out-Null
        Write-Host "Granted $($administrator.email) access to $applicationCode."
    }

    # --- Check: what the Paquetenvia API will read from Key Vault authenticates the client.
    if (-not (Test-StoredSecret)) {
        throw "The secret in Key Vault does not authenticate $clientId. Run the script again: it rotates the secret."
    }

    Write-Host ''
    Write-Host "Done: the secret in $KeyVaultName/$SecretName authenticates $clientId."
    Write-Host 'Paquetenvia API settings (deploy/azure/pilot/apps.bicep sets them):'
    Write-Host "  AuthCenter__Authority    = $authCenter"
    Write-Host "  AuthCenter__Issuer       = $issuer"
    Write-Host "  AuthCenter__ClientId     = $clientId"
    Write-Host "  AuthCenter__PublicOrigin = $origin"
    if ($secretAction) { Write-Host 'Restart the Paquetenvia API (its active Container Apps revision) so that it reads the new secret.' }
}
finally {
    if ($refreshToken) {
        try { Invoke-AuthCenter POST '/api/auth/logout' @{ refreshToken = $refreshToken } | Out-Null }
        catch { Write-Warning 'Could not end the AuthCenter session; it expires on its own.' }
    }
    if ($temporaryRule) {
        try {
            Invoke-AzureCli keyvault network-rule remove --name $KeyVaultName --ip-address $temporaryRule --output none | Out-Null
            Write-Host "Removed the temporary Key Vault rule for $temporaryRule."
        }
        catch { Write-Warning "Remove the temporary rule $temporaryRule from Key Vault $KeyVaultName yourself." }
    }
}
