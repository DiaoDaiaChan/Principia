param(
    [string]$KspDirectory = $env:KSP_DIRECTORY,
    [switch]$Wait
)

$srcDll = "Release\GameData\Principia\ksp_plugin_adapter.dll"
$srcPdb = "Release\GameData\Principia\ksp_plugin_adapter.pdb"
$srcNativeDll = "Release\GameData\Principia\Windows\x64\principia.dll"
$srcNativePdb = "Release\GameData\Principia\Windows\x64\principia.pdb"

if (-not (Test-Path $srcDll)) {
    Write-Error "Source DLL not found at $srcDll. Please build first."
    exit 1
}

$candidateDirs = @()
if ($KspDirectory) {
    $candidateDirs += $KspDirectory
}

# 1. If KSP_x64 is running, detect its exact active directory
$runningKsp = Get-Process -Name "KSP_x64" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($runningKsp) {
    try {
        $runningDir = Split-Path $runningKsp.MainModule.FileName -Parent
        if ($runningDir -and (Test-Path (Join-Path $runningDir "GameData\Principia"))) {
            $candidateDirs += $runningDir
            Write-Host "Detected actively running KSP instance (PID $($runningKsp.Id)): $runningDir" -ForegroundColor Cyan
        }
    } catch {
        # Ignore permission error
    }
}

# 2. Known standard paths
$standardPaths = @(
    $env:KSP_PATH,
    "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program_newmod",
    "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
    "C:\Program Files\Steam\steamapps\common\Kerbal Space Program_newmod",
    "C:\Program Files\Steam\steamapps\common\Kerbal Space Program",
    "D:\SteamLibrary\steamapps\common\Kerbal Space Program",
    "E:\SteamLibrary\steamapps\common\Kerbal Space Program"
)

foreach ($cand in $standardPaths) {
    if ($cand -and (Test-Path $cand) -and (Test-Path (Join-Path $cand "GameData\Principia"))) {
        if (-not ($candidateDirs -contains $cand)) {
            $candidateDirs += $cand
        }
    }
}

if ($candidateDirs.Count -eq 0) {
    Write-Error "No KSP installation directory with GameData\Principia found. Please pass -KspDirectory."
    exit 1
}

function Copy-FileAtomic([string]$source, [string]$destination) {
    try {
        Copy-Item -Path $source -Destination $destination -Force -ErrorAction Stop
    } catch {
        # If file is memory-mapped by a running process, NTFS permits renaming the file
        $oldDest = "$destination.old"
        if (Test-Path $oldDest) {
            Remove-Item $oldDest -Force -ErrorAction SilentlyContinue
        }
        Move-Item -Path $destination -Destination $oldDest -Force
        Copy-Item -Path $source -Destination $destination -Force
    }
}

foreach ($dir in $candidateDirs) {
    Write-Host "`nDeploying to KSP directory: $dir" -ForegroundColor Cyan
    $targetDir1 = Join-Path $dir "GameData\Principia"
    $targetDir2 = Join-Path $dir "Principia"

    $dstDll = Join-Path $targetDir1 "ksp_plugin_adapter.dll"
    $dstPdb = Join-Path $targetDir1 "ksp_plugin_adapter.pdb"

    Copy-FileAtomic $srcDll $dstDll
    if (Test-Path $srcPdb) {
        Copy-FileAtomic $srcPdb $dstPdb
    }

    if (Test-Path $srcNativeDll) {
        $platforms = @("x64", "x64_AVX_FMA")
        foreach ($plat in $platforms) {
            $nativeDstDir = Join-Path $targetDir1 "Windows\$plat"
            if (-not (Test-Path $nativeDstDir)) {
                New-Item -ItemType Directory -Path $nativeDstDir -Force | Out-Null
            }
            $dstNativeDll = Join-Path $nativeDstDir "principia.dll"
            Copy-FileAtomic $srcNativeDll $dstNativeDll
            Write-Host "Deployed native principia.dll to $dstNativeDll" -ForegroundColor Green
            if (Test-Path $srcNativePdb) {
                Copy-FileAtomic $srcNativePdb (Join-Path $nativeDstDir "principia.pdb")
            }
        }
    }

    $legacyShadersDst = Join-Path $targetDir1 "shaders"
    if (Test-Path $legacyShadersDst) {
        Remove-Item -Path $legacyShadersDst -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $targetDir2) {
        Copy-FileAtomic $srcDll (Join-Path $targetDir2 "ksp_plugin_adapter.dll")
        if (Test-Path $srcPdb) {
            Copy-FileAtomic $srcPdb (Join-Path $targetDir2 "ksp_plugin_adapter.pdb")
        }
    }

    $hashSrc = (Get-FileHash -Path $srcDll -Algorithm SHA256).Hash
    $hashDst = (Get-FileHash -Path $dstDll -Algorithm SHA256).Hash

    if ($hashSrc -eq $hashDst) {
        Write-Host "SUCCESS: Deployed and verified to $dir! SHA256: $hashDst" -ForegroundColor Green
    } else {
        Write-Error "HASH MISMATCH on $($dir): Deployment failed verification."
    }
}
