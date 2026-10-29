<#
.SYNOPSIS
  Removes the KASPER program files, PATH entry and Start Menu shortcut.
  Your data in %LOCALAPPDATA%\KASPER is kept unless you pass -RemoveData.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\KASPER'),
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'

Get-Process Kasper.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ -and $_ -ne $InstallDir })
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'KASPER.lnk') -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'KASPER') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "KASPER removed." -ForegroundColor Yellow
