# Resource Analyzer for Windows - One-Click Build & Package Script
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building Resource Analyzer for Windows" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Publish Self-Contained Executable
Write-Host "`n[1/3] Publishing self-contained executable..." -ForegroundColor Yellow
if (Test-Path "./publish") {
    Remove-Item -Path "./publish/*" -Recurse -Force -ErrorAction SilentlyContinue
}
dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=false -o ./publish
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit $LASTEXITCODE
}

# 2. Copy native assets & Optimize size
Write-Host "`n[2/3] Copying native assets and optimizing package size..." -ForegroundColor Yellow
Copy-Item "nvdaControllerClient.dll" "./publish/" -Force
if (-not (Test-Path "./publish/Resources")) {
    New-Item -ItemType Directory -Path "./publish/Resources" -Force | Out-Null
}
Copy-Item "Resources/app.ico" "./publish/Resources/" -Force

# Remove any foreign language satellite resource folders (leaves English intact)
Get-ChildItem -Path "./publish" -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' } | Remove-Item -Recurse -Force
# Remove unused Windows Forms designer DLLs and optional debug/XPS components
Remove-Item -Path "./publish/System.Windows.Forms.Design*.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "./publish/mscordbi.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "./publish/Microsoft.DiaSymReader.Native.amd64.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "./publish/System.Windows.Controls.Ribbon.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "./publish/ReachFramework.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "./publish/PresentationUI.dll" -Force -ErrorAction SilentlyContinue

# 3. Compile Inno Setup Installer
Write-Host "`n[3/3] Compiling accessible setup installer..." -ForegroundColor Yellow
$isccPath = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
}

if (Test-Path $isccPath) {
    & $isccPath "Installer\setup.iss"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "`n==========================================" -ForegroundColor Green
        Write-Host " Build & Installer Successful!" -ForegroundColor Green
        Write-Host " Standalone EXE: ./publish/ResourceAnalyzer.exe" -ForegroundColor Green
        Write-Host " Setup Installer: ./Installer/Output/ResourceAnalyzer_Setup_v1.0.0.exe" -ForegroundColor Green
        Write-Host "==========================================" -ForegroundColor Green
    }
} else {
    Write-Warning "Inno Setup compiler not found. Standalone EXE is ready in ./publish/ResourceAnalyzer.exe"
}
