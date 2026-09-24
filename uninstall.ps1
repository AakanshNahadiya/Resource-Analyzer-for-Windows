# Resource Analyzer for Windows - PowerShell Uninstaller
$ErrorActionPreference = "SilentlyContinue"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Resource Analyzer for Windows - Uninstaller" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Closing any running instances..." -ForegroundColor Yellow
Get-Process -Name ResourceAnalyzer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name AccessibleTaskManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$targetDir = Join-Path $env:LOCALAPPDATA 'Programs\Resource Analyzer for Windows'
if (Test-Path $targetDir) {
    Write-Host "Removing application files from: $targetDir" -ForegroundColor Yellow
    Remove-Item -Path $targetDir -Recurse -Force
}

$legacyTargetDir = Join-Path $env:LOCALAPPDATA 'Programs\Accessible Task Manager'
if (Test-Path $legacyTargetDir) {
    Remove-Item -Path $legacyTargetDir -Recurse -Force
}

# Remove Start Menu Shortcut
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Resource Analyzer for Windows.lnk'
if (Test-Path $startMenuShortcut) {
    Remove-Item -Path $startMenuShortcut -Force
}
$legacyStart = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Accessible Task Manager.lnk'
if (Test-Path $legacyStart) {
    Remove-Item -Path $legacyStart -Force
}

# Remove Desktop Shortcut
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Resource Analyzer for Windows.lnk'
if (Test-Path $desktopShortcut) {
    Remove-Item -Path $desktopShortcut -Force
}
$legacyDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Accessible Task Manager.lnk'
if (Test-Path $legacyDesktop) {
    Remove-Item -Path $legacyDesktop -Force
}

# Settings cleanup prompt
$settingsDir = Join-Path $env:APPDATA 'ResourceAnalyzer'
$legacySettingsDir = Join-Path $env:APPDATA 'AccessibleTaskManager'
if ((Test-Path $settingsDir) -or (Test-Path $legacySettingsDir)) {
    $ans = Read-Host "Do you want to delete your saved preferences and settings in Roaming AppData? (Y/N)"
    if ($ans -eq 'Y' -or $ans -eq 'y') {
        if (Test-Path $settingsDir) { Remove-Item -Path $settingsDir -Recurse -Force }
        if (Test-Path $legacySettingsDir) { Remove-Item -Path $legacySettingsDir -Recurse -Force }
        Write-Host "Preferences deleted." -ForegroundColor Yellow
    } else {
        Write-Host "Preferences preserved." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Resource Analyzer for Windows uninstalled successfully." -ForegroundColor Green
