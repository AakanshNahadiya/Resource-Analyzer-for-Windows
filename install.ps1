# Resource Analyzer for Windows - PowerShell Installer
$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Resource Analyzer for Windows - 1-Click Installer" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# Close any running instances
Write-Host "Closing any running instances..." -ForegroundColor Yellow
Get-Process -Name ResourceAnalyzer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name AccessibleTaskManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$targetDir = Join-Path $env:LOCALAPPDATA 'Programs\Resource Analyzer for Windows'
Write-Host "Installing to: $targetDir" -ForegroundColor Yellow

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

$sourceDir = Join-Path $PSScriptRoot 'publish'
if (-not (Test-Path $sourceDir)) {
    $sourceDir = $PSScriptRoot
}

Write-Host "Copying files..." -ForegroundColor Yellow
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force

# Create Start Menu Shortcut
$wsh = New-Object -ComObject WScript.Shell
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDir 'Resource Analyzer for Windows.lnk'
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $targetDir 'ResourceAnalyzer.exe'
$shortcut.WorkingDirectory = $targetDir
$shortcut.Description = 'Resource Analyzer for Windows for Screen Readers'
$shortcut.IconLocation = Join-Path $targetDir 'Resources\app.ico'
$shortcut.Save()

# Create Desktop Shortcut
$desktopDir = [Environment]::GetFolderPath('Desktop')
$desktopShortcutPath = Join-Path $desktopDir 'Resource Analyzer for Windows.lnk'
$desktopShortcut = $wsh.CreateShortcut($desktopShortcutPath)
$desktopShortcut.TargetPath = Join-Path $targetDir 'ResourceAnalyzer.exe'
$desktopShortcut.WorkingDirectory = $targetDir
$desktopShortcut.Description = 'Resource Analyzer for Windows for Screen Readers'
$desktopShortcut.IconLocation = Join-Path $targetDir 'Resources\app.ico'
$desktopShortcut.Save()

# Clean up legacy shortcuts if they exist
$legacyStart = Join-Path $startMenuDir 'Accessible Task Manager.lnk'
if (Test-Path $legacyStart) { Remove-Item -Path $legacyStart -Force -ErrorAction SilentlyContinue }
$legacyDesktop = Join-Path $desktopDir 'Accessible Task Manager.lnk'
if (Test-Path $legacyDesktop) { Remove-Item -Path $legacyDesktop -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Installation successful!" -ForegroundColor Green
Write-Host "Shortcuts created on Desktop and in Start Menu." -ForegroundColor Green
Write-Host "Launching Resource Analyzer for Windows..." -ForegroundColor Green

Start-Process -FilePath (Join-Path $targetDir 'ResourceAnalyzer.exe')
