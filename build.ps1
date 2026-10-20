# Resource Analyzer for Windows - One-Click Build & Package Script
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building Resource Analyzer for Windows" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Publish Self-Contained Executable
Write-Host "`n[1/3] Publishing self-contained executable..." -ForegroundColor Yellow
Stop-Process -Name ResourceAnalyzer -Force -ErrorAction SilentlyContinue
if (Test-Path "./publish") {
    Remove-Item -Path "./publish/*" -Recurse -Force -ErrorAction SilentlyContinue
}
dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o ./publish
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

# Remove unused Windows Desktop runtime assemblies to optimize installer size (~12 MB savings)
# CRITICAL: NEVER remove System.Windows.Controls.Ribbon.dll as WPF AncestorType XAML resolution strictly requires it!
# CRITICAL: NEVER remove System.Configuration.ConfigurationManager.dll as WPF BaseCompatibilityPreferences strictly requires it!
$unusedAssemblies = @(
    "System.Windows.Forms.Design*.dll",
    "mscordbi.dll",
    "mscordaccore*.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    # CRITICAL: NEVER remove System.Reflection.Metadata.dll as system snapshot reflection strictly requires it!
    "ReachFramework.dll",
    "PresentationUI.dll",
    "System.Printing.dll",
    "System.Data.Common.dll",
    "System.Data.dll",
    "System.Data.DataSetExtensions.dll",
    "PresentationFramework-SystemData.dll",
    "System.Private.DataContractSerialization.dll",
    "System.ServiceModel.Web.dll",
    "System.Net.WebSockets*.dll",
    "System.Security.Cryptography.Pkcs.dll",
    "System.Security.Cryptography.Xml.dll",
    "System.Threading.Tasks.Dataflow.dll",
    "System.Security.Permissions.dll",
    "PresentationFramework.Luna.dll",
    "PresentationFramework.Royale.dll",
    "PresentationFramework.Classic.dll",
    "PresentationFramework.Aero.dll",
    "PresentationFramework.AeroLite.dll"
)
foreach ($pattern in $unusedAssemblies) {
    Remove-Item -Path "./publish/$pattern" -Force -ErrorAction SilentlyContinue
}

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
        $setupFile = (Get-ChildItem "./Installer/Output/*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        Write-Host " Setup Installer: ./Installer/Output/$($setupFile.Name)" -ForegroundColor Green
        $targetDir = "C:\Users\Aakansh\Music\Output"
        if (Test-Path $targetDir) {
            $destFile = Join-Path $targetDir $setupFile.Name
            Copy-Item $setupFile.FullName -Destination $targetDir -Force
            Write-Host " Output Folder: $destFile" -ForegroundColor Green

            # Compute SHA-256 and update WinGet Manifest automatically
            Write-Host "`nComputing SHA-256 hash..." -ForegroundColor Yellow
            $hashResult = Get-FileHash -Path $destFile -Algorithm SHA256
            $sha256 = $hashResult.Hash.ToUpper()
            Write-Host " SHA-256: $sha256" -ForegroundColor Green

            $hashFile = "$destFile.sha256"
            Set-Content -Path $hashFile -Value "$sha256  $($setupFile.Name)"
            Write-Host " Saved Hash File: $hashFile" -ForegroundColor Cyan

            $manifestPath = "./winget-manifest/AccessibleTools.ResourceAnalyzer.installer.yaml"
            if (Test-Path $manifestPath) {
                $today = Get-Date -Format "yyyy-MM-dd"
                $content = Get-Content -Path $manifestPath -Raw
                $content = [System.Text.RegularExpressions.Regex]::Replace($content, "ReleaseDate:\s*\d{4}-\d{2}-\d{2}", "ReleaseDate: $today")
                $content = [System.Text.RegularExpressions.Regex]::Replace($content, "InstallerSha256:\s*[A-Fa-f0-9]{64}", "InstallerSha256: $sha256")
                Set-Content -Path $manifestPath -Value $content -NoNewline
                Write-Host " WinGet Manifest Updated: ReleaseDate=$today, InstallerSha256=$sha256" -ForegroundColor Green
            }
        }
        Write-Host "==========================================" -ForegroundColor Green
    }
} else {
    Write-Warning "Inno Setup compiler not found. Standalone EXE is ready in ./publish/ResourceAnalyzer.exe"
}
