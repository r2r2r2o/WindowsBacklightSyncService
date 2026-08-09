<#
.SYNOPSIS
    Stops and removes the BacklightSyncService Windows service.
.EXAMPLE
    .\uninstall.ps1
#>
param(
    [string]$ServiceName = "BacklightSyncService"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Please run this script from an elevated PowerShell."
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
sc.exe delete $ServiceName

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service removal may be pending (marked for deletion)."
} else {
    Write-Host "Service '$ServiceName' removed."
}
