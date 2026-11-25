# ==============================================================================
# PROJECT R - WINDOWS AGENT NATIVE COMPILER (BUILD SCRIPT)
# Compiles ProjectRAgent.exe (Service/Daemon) & ProjectRManager.exe (GUI)
# Using Windows Built-in Microsoft Visual C# Compiler (csc.exe)
# ==============================================================================

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$binDir = Join-Path $scriptDir "bin"
$publicDownloadDir = Join-Path $repoRoot "public\downloads\agent"

if (-not (Test-Path $binDir)) {
    New-Item -Path $binDir -ItemType Directory -Force | Out-Null
}
if (-not (Test-Path $publicDownloadDir)) {
    New-Item -Path $publicDownloadDir -ItemType Directory -Force | Out-Null
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    Write-Host "[ERROR] .NET Framework C# compiler (csc.exe) tidak ditemukan!" -ForegroundColor Red
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "     PROJECT R - COMPILING NATIVE WINDOWS AGENT SUITE     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Compiler: $csc" -ForegroundColor Gray
Write-Host ""

$commonFiles = Get-ChildItem (Join-Path $scriptDir "src\Common\*.cs") | Select-Object -ExpandProperty FullName
$serviceFiles = @($commonFiles) + (Join-Path $scriptDir "src\Service\Program.cs")
$managerFiles = @($commonFiles) + (Join-Path $scriptDir "src\Manager\Program.cs")

$wpfDir = Join-Path (Split-Path -Parent $csc) "WPF"
if (-not (Test-Path $wpfDir)) {
    $wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
}

# Standard Reference Assemblies for Service
$serviceRefs = @(
    "System.dll",
    "System.Core.dll",
    "System.Data.dll",
    "System.Management.dll",
    "System.ServiceProcess.dll",
    "System.Web.Extensions.dll"
)
$serviceRefArgs = ($serviceRefs | ForEach-Object { "/r:$_" }) -join " "

# WPF Reference Assemblies for Manager GUI
$managerRefs = @($serviceRefs) + @(
    "System.Xaml.dll",
    "Microsoft.VisualBasic.dll",
    (Join-Path $wpfDir "WindowsBase.dll"),
    (Join-Path $wpfDir "PresentationCore.dll"),
    (Join-Path $wpfDir "PresentationFramework.dll")
)
$managerRefArgs = ($managerRefs | ForEach-Object { "/r:`"$_`"" }) -join " "

# Find Inno Setup Compiler (ISCC.exe)
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $cmdIscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmdIscc) { $iscc = $cmdIscc.Source }
}

# 1. Compile Binaries for x64 and x86
$archs = @("x64", "x86")

foreach ($arch in $archs) {
    Write-Host ""
    Write-Host "--- Mengompilasi Arsitektur: $arch ---" -ForegroundColor Cyan

    $iconArg = "/win32icon:`"$scriptDir\resources\app.ico`""

    $agentExe = Join-Path $binDir "ProjectRAgent-$arch.exe"
    $agentCmd = "& `"$csc`" /nologo /t:exe /platform:$arch /optimize+ $iconArg /out:`"$agentExe`" $serviceRefArgs " + ($serviceFiles | ForEach-Object { "`"$_`"" } | Out-String).Replace("`r`n", " ")
    Invoke-Expression $agentCmd

    $managerExe = Join-Path $binDir "ProjectRManager-$arch.exe"
    $managerCmd = "& `"$csc`" /nologo /t:winexe /platform:$arch /optimize+ $iconArg /out:`"$managerExe`" $managerRefArgs " + ($managerFiles | ForEach-Object { "`"$_`"" } | Out-String).Replace("`r`n", " ")
    Invoke-Expression $managerCmd

    if ((Test-Path $agentExe) -and (Test-Path $managerExe)) {
        Write-Host "  -> [OK] Biner $arch berhasil dikompilasi!" -ForegroundColor Green
    } else {
        Write-Host "  -> [ERROR] Gagal mengompilasi biner $arch" -ForegroundColor Red
        exit 1
    }
}

# Copy default AnyCPU / x64 as main ProjectRAgent.exe & ProjectRManager.exe
Copy-Item (Join-Path $binDir "ProjectRAgent-x64.exe") -Destination (Join-Path $binDir "ProjectRAgent.exe") -Force
Copy-Item (Join-Path $binDir "ProjectRManager-x64.exe") -Destination (Join-Path $binDir "ProjectRManager.exe") -Force

# 1.5. Compile Project R Windows Remote Agent Daemon (Golang) for x64 (amd64) and x86 (386)
$goRemoteDir = Join-Path $scriptDir "remote-agent"
if (Test-Path (Join-Path $goRemoteDir "main.go")) {
    Write-Host ""
    Write-Host "--- Mengompilasi Project R Windows Remote Agent (Golang x64 & x86) ---" -ForegroundColor Cyan
    if (-not (Get-Command "go" -ErrorAction SilentlyContinue) -and (Test-Path "C:\Program Files\Go\bin\go.exe")) {
        $env:PATH = "C:\Program Files\Go\bin;" + $env:PATH
    }
    $origGoos = $env:GOOS
    $origGoarch = $env:GOARCH
    try {
        $env:GOOS = "windows"
        $goArchMap = @{ "x64" = "amd64"; "x86" = "386" }
        foreach ($arch in $archs) {
            $env:GOARCH = $goArchMap[$arch]
            $remoteOut = Join-Path $binDir "ProjectRRemoteAgent-$arch.exe"
            Push-Location $goRemoteDir
            & go build -ldflags="-s -w -H windowsgui" -o $remoteOut .
            $goExit = $LASTEXITCODE
            Pop-Location
            if ($goExit -ne 0 -or -not (Test-Path $remoteOut)) {
                Write-Host "  -> [ERROR] Gagal mengompilasi ProjectRRemoteAgent-$arch.exe" -ForegroundColor Red
                exit 1
            }
            Write-Host "  -> [OK] ProjectRRemoteAgent-$arch.exe ($($env:GOARCH)) berhasil dikompilasi!" -ForegroundColor Green
            Copy-Item $remoteOut -Destination (Join-Path $publicDownloadDir "ProjectRRemoteAgent-$arch.exe") -Force
        }
    } finally {
        $env:GOOS = $origGoos
        $env:GOARCH = $origGoarch
    }

    Copy-Item (Join-Path $binDir "ProjectRRemoteAgent-x64.exe") -Destination (Join-Path $binDir "ProjectRRemoteAgent.exe") -Force
    Copy-Item (Join-Path $binDir "ProjectRRemoteAgent.exe") -Destination (Join-Path $publicDownloadDir "ProjectRRemoteAgent.exe") -Force
}

# 2. Compile Professional Setup Wizard Installers (Inno Setup)
$issFile = Join-Path $scriptDir "installer\setup.iss"
if ((Test-Path $iscc) -and (Test-Path $issFile)) {
    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Yellow
    Write-Host " [INNO SETUP] MEMBANGUN ENTERPRISE INSTALLER WIZARD SETUP  " -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Yellow

    foreach ($arch in $archs) {
        Write-Host "[*] Membangun Setup Wizard ($arch)..." -ForegroundColor Yellow
        $setupCmd = "& `"$iscc`" /Q /DAppArch=$arch `"$issFile`""
        Invoke-Expression $setupCmd

        $setupExe = Join-Path $binDir "ProjectR-Agent-Setup-$arch.exe"
        if (Test-Path $setupExe) {
            $setupSize = (Get-Item $setupExe).Length / 1MB
            Write-Host "  -> [BERHASIL] $setupExe ($([Math]::Round($setupSize, 2)) MB)" -ForegroundColor Green
            Copy-Item $setupExe -Destination (Join-Path $publicDownloadDir "ProjectR-Agent-Setup-$arch.exe") -Force
        }
    }
} else {
    Write-Host "[!] Inno Setup ISCC.exe tidak ditemukan, melewati tahap pembuatan installer setup.exe" -ForegroundColor Yellow
}

# 3. Publish to web public download directory
Write-Host ""
Write-Host "[*] Menyalin biner ke web server: $publicDownloadDir ..." -ForegroundColor Gray
foreach ($arch in $archs) {
    Copy-Item (Join-Path $binDir "ProjectRAgent-$arch.exe") -Destination (Join-Path $publicDownloadDir "ProjectRAgent-$arch.exe") -Force
    Copy-Item (Join-Path $binDir "ProjectRManager-$arch.exe") -Destination (Join-Path $publicDownloadDir "ProjectRManager-$arch.exe") -Force
}
Copy-Item (Join-Path $binDir "ProjectRAgent.exe") -Destination (Join-Path $publicDownloadDir "ProjectRAgent.exe") -Force
Copy-Item (Join-Path $binDir "ProjectRManager.exe") -Destination (Join-Path $publicDownloadDir "ProjectRManager.exe") -Force
if (Test-Path (Join-Path $binDir "ProjectRRemoteAgent.exe")) {
    Copy-Item (Join-Path $binDir "ProjectRRemoteAgent.exe") -Destination (Join-Path $publicDownloadDir "ProjectRRemoteAgent.exe") -Force
}

# 4. Generate install.bat helper wrapper (auto-selects x86 vs x64 for legacy ZIP installs)
$installBat = Join-Path $publicDownloadDir "install.bat"
$installBatContent = @'
@echo off
setlocal
title Project R - Agent Launcher
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Meminta hak Administrator...
    powershell -Command "Start-Process '%~0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
set "ARCH=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="x86" (
    if not defined PROCESSOR_ARCHITEW6432 set "ARCH=x86"
)
if exist "ProjectRAgent-%ARCH%.exe" copy /y "ProjectRAgent-%ARCH%.exe" "ProjectRAgent.exe" >nul
if exist "ProjectRManager-%ARCH%.exe" copy /y "ProjectRManager-%ARCH%.exe" "ProjectRManager.exe" >nul
if exist "ProjectRRemoteAgent-%ARCH%.exe" copy /y "ProjectRRemoteAgent-%ARCH%.exe" "ProjectRRemoteAgent.exe" >nul
start "" "ProjectRManager.exe"
'@
Set-Content -Path $installBat -Value $installBatContent -Encoding ASCII -Force
Copy-Item $installBat -Destination (Join-Path $binDir "install.bat") -Force

# 5. Package ZIP bundle for easy deployment
Start-Sleep -Milliseconds 1000
$agentVersion = "2.2.6"
$configCs = Join-Path $scriptDir "src\Common\Config.cs"
if (Test-Path $configCs) {
    $match = Select-String -Path $configCs -Pattern 'CurrentVersion\s*=\s*"([^"]+)"'
    if ($match.Matches.Count -gt 0) {
        $agentVersion = $match.Matches[0].Groups[1].Value
    }
}
$zipFile = Join-Path $publicDownloadDir "ProjectR-Agent-Windows-v$agentVersion.zip"
if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
Compress-Archive -Path "$binDir\*" -DestinationPath $zipFile -Force
Write-Host "  -> [OK] ZIP Bundle dibuat: $zipFile" -ForegroundColor Green

# 6. Generate Cryptographic Release Manifest & SHA-256 Checksums (PRD Phase 0 Item 5)
Write-Host ""
Write-Host "[*] Menghitung checksum SHA-256 dan membangun release manifest..." -ForegroundColor Gray
$artifacts = Get-ChildItem -Path $publicDownloadDir -File | Where-Object { $_.Name -ne "checksums.txt" -and $_.Name -ne "manifest.json" }
$checksumLines = @()
$manifestFiles = @()

foreach ($file in $artifacts) {
    $sha256 = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
    $checksumLines += "$sha256 *$($file.Name)"
    $manifestFiles += [PSCustomObject]@{
        name = $file.Name
        size = $file.Length
        sha256 = $sha256
    }
}

$checksumsPath = Join-Path $publicDownloadDir "checksums.txt"
Set-Content -Path $checksumsPath -Value ($checksumLines -join "`r`n") -Encoding ASCII -Force
Copy-Item $checksumsPath -Destination (Join-Path $binDir "checksums.txt") -Force

$manifestObj = [PSCustomObject]@{
    version = $agentVersion
    channel = "stable"
    platform = "windows"
    build_time = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    publisher = "Project R Open Source MDM"
    files = $manifestFiles
}
$manifestJson = $manifestObj | ConvertTo-Json -Depth 5
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$manifestPath = Join-Path $publicDownloadDir "manifest.json"
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)
Copy-Item $manifestPath -Destination (Join-Path $binDir "manifest.json") -Force
Write-Host "  -> [OK] SHA-256 Checksums & manifest.json berhasil dibuat (Clean UTF-8)!" -ForegroundColor Green

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " [BERHASIL] SELURUH BINER APLIKASI TELAH SIAP!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Output Folder : $binDir"
Write-Host " Web Download  : $publicDownloadDir"
Write-Host " ZIP Bundle    : ProjectR-Agent-Windows-v$agentVersion.zip"
Write-Host ""
