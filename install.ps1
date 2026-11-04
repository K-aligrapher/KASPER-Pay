<#
.SYNOPSIS
  Builds KASPER and installs it for the current user so `kasper` works in any terminal.

.DESCRIPTION
  - Builds the React UI and publishes the desktop app + CLI (Release) to
    %LOCALAPPDATA%\Programs\KASPER
  - Adds that folder to your *user* PATH (no admin needed)
  - Creates a Start Menu shortcut "KASPER"
  Your data (database, settings, logs) stays in %LOCALAPPDATA%\KASPER and is not touched.
  Run uninstall.ps1 to remove it.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\KASPER'),
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

Write-Host "KASPER installer" -ForegroundColor Yellow

# 1. Stop a running copy so files can be replaced.
Get-Process Kasper.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. Build the UI.
Write-Host "Building UI..."
Push-Location (Join-Path $repo 'ui')
try {
    if (-not (Test-Path node_modules)) { npm install --silent; if ($LASTEXITCODE) { throw "npm install failed" } }
    npm run build --silent; if ($LASTEXITCODE) { throw "UI build failed" }
} finally { Pop-Location }

# 3. Publish desktop app and CLI side by side.
Write-Host "Publishing to $InstallDir ..."
New-Item -ItemType Directory -Force $InstallDir | Out-Null
# ReadyToRun precompiles the app so startup skips most JIT work.
$fast = @('-r', 'win-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true')
dotnet publish (Join-Path $repo 'src\Kasper.Desktop') -c Release -o $InstallDir @fast --nologo -v q
if ($LASTEXITCODE) { throw "Desktop publish failed" }
dotnet publish (Join-Path $repo 'src\Kasper.Cli') -c Release -o $InstallDir @fast --nologo -v q
if ($LASTEXITCODE) { throw "CLI publish failed" }

# 4. Add to user PATH (once).
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $InstallDir) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $InstallDir) -join ';'), 'User')
    Write-Host "Added $InstallDir to your user PATH."
}

# 5. Start Menu shortcut.
if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'KASPER.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $InstallDir 'Kasper.Desktop.exe'
    $s.WorkingDirectory = $InstallDir
    $s.IconLocation = Join-Path $InstallDir 'kasper.ico'
    $s.Description = 'KASPER - your desktop coworker'
    $s.Save()
}

# 6. Explorer "Send to -> KASPER" (FR-104): opens the files in the Documents screen.
if (-not $NoShortcut) {
    $sendTo = Join-Path ([Environment]::GetFolderPath('SendTo')) 'KASPER.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($sendTo)
    $s.TargetPath = Join-Path $InstallDir 'kasper.exe'
    $s.Arguments = 'drop'
    $s.WindowStyle = 7  # minimized: the console closes as soon as the files are handed over
    $s.IconLocation = Join-Path $InstallDir 'kasper.ico'
    $s.Save()
}

Write-Host ""
Write-Host "Done! Open a NEW cmd or PowerShell window and type:  kasper" -ForegroundColor Yellow
Write-Host "Or press Ctrl+Space after starting KASPER from the Start menu."
