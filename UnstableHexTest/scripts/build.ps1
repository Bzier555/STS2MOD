[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'doctor.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$solution = Get-ChildItem -LiteralPath $repoRoot -Filter '*.sln' -File | Select-Object -First 1

Push-Location $repoRoot
try {
    & dotnet restore $solution.FullName
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet build $solution.FullName --configuration $Configuration --no-restore
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
