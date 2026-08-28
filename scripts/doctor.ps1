[CmdletBinding()]
param(
    [switch]$RequireGodot,
    [switch]$RequireBaseLib
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = 0

function Write-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail,
        [bool]$Required = $true
    )

    $status = if ($Passed) { 'PASS' } elseif ($Required) { 'FAIL' } else { 'WARN' }
    Write-Host ('[{0}] {1}: {2}' -f $status, $Name, $Detail)
    if (-not $Passed -and $Required) {
        $script:failures++
    }
}

function Get-LocalProperty {
    param(
        [xml]$Document,
        [string]$Name
    )

    foreach ($group in @($Document.Project.PropertyGroup)) {
        $value = $group.$Name
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            return [string]$value
        }
    }

    return $null
}

$gitCommand = Get-Command git -ErrorAction SilentlyContinue
Write-Check 'Git' ($null -ne $gitCommand) $(if ($gitCommand) { $gitCommand.Source } else { 'not found on PATH' })

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetVersion = $null
if ($dotnetCommand) {
    $dotnetVersion = (& dotnet --version 2>$null | Select-Object -First 1)
}
$dotnetOk = $false
if ($dotnetVersion) {
    $parsedVersion = $null
    $dotnetOk = [version]::TryParse($dotnetVersion, [ref]$parsedVersion) -and $parsedVersion.Major -ge 9
}
Write-Check '.NET SDK' $dotnetOk $(if ($dotnetVersion) { $dotnetVersion } else { 'SDK 9 or newer not found' })

$solutions = @(Get-ChildItem -LiteralPath $repoRoot -Filter '*.sln' -File -ErrorAction SilentlyContinue)
$projects = @(Get-ChildItem -LiteralPath $repoRoot -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
Write-Check 'Solution' ($solutions.Count -eq 1) $(if ($solutions.Count -eq 1) { $solutions[0].Name } else { "expected one root .sln; found $($solutions.Count)" })
Write-Check 'Project' ($projects.Count -eq 1) $(if ($projects.Count -eq 1) { $projects[0].Name } else { "expected one root .csproj; found $($projects.Count)" })

$localPropsPath = Join-Path $repoRoot 'Directory.Build.local.props'
$localProps = $null
if (Test-Path -LiteralPath $localPropsPath) {
    try {
        [xml]$localProps = Get-Content -Raw -LiteralPath $localPropsPath
        Write-Check 'Local configuration' $true 'Directory.Build.local.props loaded' $false
    }
    catch {
        Write-Check 'Local configuration' $false "invalid XML: $($_.Exception.Message)"
    }
}
else {
    Write-Check 'Local configuration' $false 'not present; template discovery and environment variables will be used' $false
}

$sts2Path = $env:STS2_PATH
if (-not $sts2Path -and $localProps) {
    $sts2Path = Get-LocalProperty $localProps 'Sts2Path'
}
if (-not $sts2Path) {
    $sts2Path = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
}
$sts2Assembly = Join-Path $sts2Path 'data_sts2_windows_x86_64\sts2.dll'
Write-Check 'Slay the Spire 2' (Test-Path -LiteralPath $sts2Assembly) $(if (Test-Path -LiteralPath $sts2Assembly) { $sts2Path } else { "sts2.dll not found beneath $sts2Path" })

$godotPath = $env:GODOT_PATH
if (-not $godotPath -and $localProps) {
    $godotPath = Get-LocalProperty $localProps 'GodotPath'
}
$godotFound = $godotPath -and (Test-Path -LiteralPath $godotPath)
Write-Check 'MegaDot/Godot' $godotFound $(if ($godotFound) { $godotPath } else { 'set GodotPath in local props or GODOT_PATH' }) $RequireGodot

$baseLibCandidates = @(
    (Join-Path $sts2Path 'mods\BaseLib')
)
$commonPath = Split-Path -Parent $sts2Path
$steamAppsPath = Split-Path -Parent $commonPath
$baseLibCandidates += Join-Path $steamAppsPath 'workshop\content\2868840\3737335127\BaseLib'
$baseLibPath = $baseLibCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
Write-Check 'BaseLib' ($null -ne $baseLibPath) $(if ($baseLibPath) { $baseLibPath } else { 'not found in the local mods or expected Workshop folder' }) $RequireBaseLib

if ($failures -gt 0) {
    Write-Host "Doctor found $failures required check failure(s)."
    exit 1
}

Write-Host 'Doctor completed without required failures.'

