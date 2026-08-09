<#
.SYNOPSIS
    Installs or updates BacklightSyncService as a Windows service (runs as LocalSystem).
.DESCRIPTION
    Copies the published binaries to $InstallDir and registers + starts the service.

    If the service is already installed and RUNNING, it is stopped first and the script
    waits until the service has fully stopped and its process has exited — otherwise the
    running exe would lock the files and the update would fail. Only then are the
    binaries replaced, the service reconfigured and started again.

    Run this script from an elevated PowerShell.
.EXAMPLE
    .\install.ps1
#>
param(
    [string]$ServiceName = "BacklightSyncService",
    [string]$InstallDir  = "$env:ProgramFiles\BacklightSyncService",
    [string]$ExeName     = "BacklightSyncService.exe"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Please run this script from an elevated PowerShell."
}

$source = Join-Path $PSScriptRoot "..\publish"
$sourceExe = Join-Path $source $ExeName
if (-not (Test-Path $sourceExe)) {
    throw "Published binaries not found in '$source'. Build first:`n  dotnet publish -c Release -r win-x64 --self-contained true -o publish"
}

# Stale-build guard: the publish output must match the source version, otherwise the
# update would silently install an old exe (seen repeatedly: publish\ still had v1.2.0
# while the source was v1.3.x).
$csprojPath = Join-Path $PSScriptRoot "..\BacklightSyncService.csproj"
$csprojVersion = (Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>' -ErrorAction SilentlyContinue | ForEach-Object { $_.Matches[0].Groups[1].Value })
if ($csprojVersion) {
    $exeFileVersion = (Get-Item $sourceExe).VersionInfo.FileVersion
    if ($exeFileVersion -and $exeFileVersion -notlike "$csprojVersion*") {
        throw "STALE BUILD DETECTED: publish exe is v$exeFileVersion but the source is v$csprojVersion. Republish first:`n  dotnet publish -c Release -r win-x64 --self-contained true -o publish"
    }
    Write-Host "Build version check: source v$csprojVersion == publish v$exeFileVersion (OK)."
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# ---------------------------------------------------------------------------
# Stop the service (if installed) and WAIT until SCM reports it as stopped.
# Stop-Service returns before the service's shutdown handler has necessarily
# finished, so a manual poll is required before touching the files.
# ---------------------------------------------------------------------------
function Stop-ServiceIfRunning {
    param([string]$Name, [int]$TimeoutSeconds = 60)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }

    $svc.Refresh()
    if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Service '$Name' is already stopped."
        return
    }

    Write-Host "Service '$Name' is '$($svc.Status)' - stopping it..."
    try {
        # If it is already StopPending (e.g. from a previous attempt), skip the call.
        if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
            Stop-Service -Name $Name -Force -ErrorAction Stop
        }
    } catch {
        $svc.Refresh()
        # It may have stopped between the check and the call - that is fine.
        if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            throw "Failed to stop service '$Name': $_"
        }
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $svc.Refresh()
        if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-Host "Service '$Name' stopped."
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "Service '$Name' did not reach 'Stopped' within $TimeoutSeconds seconds (state: $($svc.Status))."
}

# ---------------------------------------------------------------------------
# Wait until the process has actually exited so it no longer locks the files.
# ---------------------------------------------------------------------------
function Wait-ForProcessExit {
    param([string]$ProcessName, [int]$TimeoutSeconds = 30)

    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    Write-Host "Waiting for process '$ProcessName' (PID $($procs.Id -join ', ')) to exit..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) {
            Write-Host "Process exited."
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "Process '$ProcessName' is still running after $TimeoutSeconds seconds; cannot replace the binaries. Stop it manually and re-run."
}

# ---------------------------------------------------------------------------
# Main flow
# ---------------------------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' is already installed - updating in place."
    Stop-ServiceIfRunning -Name $ServiceName
    Wait-ForProcessExit -ProcessName ([System.IO.Path]::GetFileNameWithoutExtension($ExeName))
} else {
    Write-Host "Service '$ServiceName' is not installed yet - installing fresh."
}

# Replace the binaries. Retry a few times in case a file handle lingers briefly.
$binPath = Join-Path $InstallDir $ExeName
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Copy-Item -Path (Join-Path $source "*") -Destination $InstallDir -Recurse -Force
        break
    } catch {
        if ($attempt -lt 5) {
            Write-Warning "Copy attempt $attempt failed ($($_.Exception.Message)) - retrying in 2 s..."
            Start-Sleep -Seconds 2
        } else {
            throw "Failed to copy published files to '$InstallDir' after 5 attempts: $_"
        }
    }
}
Write-Host "Binaries updated in '$InstallDir'."

# Register / reconfigure the service.
if ($existing) {
    sc.exe config $ServiceName binPath= "`"$binPath`"" | Out-Null
    sc.exe config $ServiceName start= auto | Out-Null
    sc.exe description $ServiceName "Synchronizes the display backlight level across all Windows power plans." | Out-Null
    Write-Host "Service configuration updated."
} else {
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$binPath`"" `
        -DisplayName "Backlight Sync Service" `
        -Description "Synchronizes the display backlight level across all Windows power plans." `
        -StartupType Automatic | Out-Null
    Write-Host "Service '$ServiceName' created."
}

# Start and verify it actually came up.
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName
$svc.Refresh()
if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Write-Host "Service '$ServiceName' started successfully. Binary: $binPath"
} else {
    Write-Warning "Service '$ServiceName' did not reach 'Running' (state: $($svc.Status)). Check the Event Log and $env:ProgramData\BacklightSyncService\logs\backlight-sync.log"
}
