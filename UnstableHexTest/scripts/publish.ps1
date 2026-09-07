[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'doctor.ps1') -RequireGodot -RequireBaseLib
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$project = Get-ChildItem -LiteralPath $repoRoot -Filter '*.csproj' -File | Select-Object -First 1

Push-Location $repoRoot
try {
    & dotnet publish $project.FullName --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host 'Publish completed. Check the mod-specific folder beneath the Slay the Spire 2 mods directory.'
    exit 0
}
finally {
    Pop-Location
}
