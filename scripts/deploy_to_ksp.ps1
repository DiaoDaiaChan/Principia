param(
    [string]$KspDirectory = $env:KSP_DIRECTORY,
    [switch]$Wait
)

$srcDll = "Release\GameData\Principia\ksp_plugin_adapter.dll"
$srcPdb = "Release\GameData\Principia\ksp_plugin_adapter.pdb"

if (-not $KspDirectory) {
    $candidatePaths = @(
        $env:KSP_PATH,
        "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program_newmod",
        "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
        "C:\Program Files\Steam\steamapps\common\Kerbal Space Program",
        "D:\SteamLibrary\steamapps\common\Kerbal Space Program",
        "E:\SteamLibrary\steamapps\common\Kerbal Space Program"
    )
    foreach ($cand in $candidatePaths) {
        if ($cand -and (Test-Path (Join-Path $cand "GameData\Principia"))) {
            $KspDirectory = $cand
            break
        }
    }
    if (-not $KspDirectory) {
        foreach ($cand in $candidatePaths) {
            if ($cand -and (Test-Path $cand)) {
                $KspDirectory = $cand
                break
            }
        }
    }
}

if (-not $KspDirectory -or -not (Test-Path $KspDirectory)) {
    Write-Error "KSP installation directory not found. Please pass -KspDirectory or set the KSP_DIRECTORY environment variable."
    exit 1
}

$targetDir1 = Join-Path $KspDirectory "GameData\Principia"
$targetDir2 = Join-Path $KspDirectory "Principia"

if (-not (Test-Path $srcDll)) {
    Write-Error "Source DLL not found at $srcDll. Please build first."
    exit 1
}

$kspProcess = Get-Process -Name "KSP_x64" -ErrorAction SilentlyContinue

if ($kspProcess) {
    if ($Wait) {
        Write-Host "KSP_x64 (PID $($kspProcess.Id)) is currently running. Waiting for KSP to exit..." -ForegroundColor Yellow
        $kspProcess.WaitForExit()
        Write-Host "KSP exited. Continuing deployment." -ForegroundColor Green
    } else {
        Write-Warning "KSP_x64 (PID $($kspProcess.Id)) is currently running and has memory-mapped the DLL."
        Write-Warning "Please save and close KSP, then re-run this script to deploy."
        exit 2
    }
}

Write-Host "Deploying latest Principia binaries to KSP directories..." -ForegroundColor Cyan

Copy-Item -Path $srcDll -Destination "$targetDir1\ksp_plugin_adapter.dll" -Force
Copy-Item -Path $srcPdb -Destination "$targetDir1\ksp_plugin_adapter.pdb" -Force

if (Test-Path $targetDir2) {
    Copy-Item -Path $srcDll -Destination "$targetDir2\ksp_plugin_adapter.dll" -Force
    Copy-Item -Path $srcPdb -Destination "$targetDir2\ksp_plugin_adapter.pdb" -Force
}

$hashSrc = (Get-FileHash -Path $srcDll -Algorithm SHA256).Hash
$hashDst = (Get-FileHash -Path "$targetDir1\ksp_plugin_adapter.dll" -Algorithm SHA256).Hash

if ($hashSrc -eq $hashDst) {
    Write-Host "SUCCESS: Deployed and verified! SHA256: $hashDst" -ForegroundColor Green
} else {
    Write-Error "HASH MISMATCH: Deployment failed verification."
}
