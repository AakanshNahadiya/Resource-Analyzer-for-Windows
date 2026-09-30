# Resource Analyzer for Windows - PowerShell Uninstaller
$ErrorActionPreference = "SilentlyContinue"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Resource Analyzer for Windows - Uninstaller" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Closing any running instances..." -ForegroundColor Yellow
Get-Process -Name ResourceAnalyzer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$targetDir = Join-Path $env:LOCALAPPDATA 'Programs\Resource Analyzer for Windows'
if (Test-Path $targetDir) {
    Write-Host "Removing application files from: $targetDir" -ForegroundColor Yellow
    Remove-Item -Path $targetDir -Recurse -Force
}

# Remove Start Menu Shortcut
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Resource Analyzer for Windows.lnk'
if (Test-Path $startMenuShortcut) {
    Remove-Item -Path $startMenuShortcut -Force
}
# Remove Desktop Shortcut
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Resource Analyzer for Windows.lnk'
if (Test-Path $desktopShortcut) {
    Remove-Item -Path $desktopShortcut -Force
}

# Settings cleanup prompt
$settingsDir = Join-Path $env:APPDATA 'ResourceAnalyzer'
if (Test-Path $settingsDir) {
    $ans = Read-Host "Do you want to delete your saved preferences and settings in Roaming AppData? (Y/N)"
    if ($ans -eq 'Y' -or $ans -eq 'y') {
        Remove-Item -Path $settingsDir -Recurse -Force
        Write-Host "Preferences deleted." -ForegroundColor Yellow
    } else {
        Write-Host "Preferences preserved." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Resource Analyzer for Windows uninstalled successfully." -ForegroundColor Green
