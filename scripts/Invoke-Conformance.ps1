[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
dotnet test tests/AuthCenter.IntegrationTests/AuthCenter.IntegrationTests.csproj `
    --configuration $Configuration `
    --filter 'Category=Conformance' `
    --verbosity normal
if ($LASTEXITCODE -ne 0) { throw 'OIDC/SCIM conformance profile failed.' }

Push-Location sdk/typescript
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript SDK restore failed.' }
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript SDK tests failed.' }
}
finally { Pop-Location }
