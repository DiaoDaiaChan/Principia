param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Project = "ksp_plugin\ksp_plugin.vcxproj",
    [switch]$Deploy,
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent

# Locate MSBuild
$msBuildCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
)

$msBuild = $null
foreach ($cand in $msBuildCandidates) {
    if (Test-Path $cand) {
        $msBuild = $cand
        break
    }
}

if (-not $msBuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsInstall = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
        if ($vsInstall) {
            $cand = Join-Path $vsInstall "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $cand) {
                $msBuild = $cand
            }
        }
    }
}

if (-not $msBuild) {
    Write-Error "Could not find MSBuild.exe."
    exit 1
}

Write-Host "Using MSBuild: $msBuild" -ForegroundColor DarkGray
Write-Host "Building $Project ($Configuration|$Platform)..." -ForegroundColor Cyan

$targetArg = if ($Rebuild) { "/t:Rebuild" } else { "/t:Build" }
$solutionDir = "$root\"

$sw = [System.Diagnostics.Stopwatch]::StartNew()

& $msBuild $Project `
    /p:Configuration=$Configuration `
    /p:Platform=$Platform `
    /p:SolutionDir="$solutionDir" `
    $targetArg `
    /v:m

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if ($Project -eq "ksp_plugin\ksp_plugin.vcxproj") {
    Write-Host "Building ksp_plugin_adapter\ksp_plugin_adapter.csproj ($Configuration)..." -ForegroundColor Cyan
    & $msBuild "ksp_plugin_adapter\ksp_plugin_adapter.csproj" `
        /p:Configuration=$Configuration `
        /p:SolutionDir="$solutionDir" `
        $targetArg `
        /v:m

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Adapter build failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

$sw.Stop()
Write-Host "Build finished in $($sw.Elapsed.TotalSeconds.ToString('F2'))s." -ForegroundColor Green

if ($Deploy) {
    Write-Host "Deploying to KSP..." -ForegroundColor Cyan
    & "$PSScriptRoot\deploy_to_ksp.ps1"
}
