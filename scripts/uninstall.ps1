<#
.SYNOPSIS
    Stops and removes the WindowsBacklightSyncService Windows service.
.EXAMPLE
    .\uninstall.ps1
#>
param(
    [string]$ServiceName = "WindowsBacklightSyncService"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Please run this script from an elevated PowerShell."
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

# Wait for the service to actually stop (Stop-Service can return while StopPending).
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
} while ($svc -and $svc.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)

sc.exe delete $ServiceName

# sc delete can stay pending while open handles close — poll until SCM reports it gone,
# so an immediate reinstall does not hit "service marked for deletion".
$deadline = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Milliseconds 500
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
} while ($svc -and (Get-Date) -lt $deadline)

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Warning "Service removal is still pending (marked for deletion). Re-run the script or reboot."
} else {
    Write-Host "Service '$ServiceName' removed."
}
