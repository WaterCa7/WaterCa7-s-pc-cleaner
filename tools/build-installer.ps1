# Builds publish/WaterCa7sPcCleaner-Setup.exe: a self-contained folder publish of the app, packed by Inno Setup.
# Used locally, by CI and by the release. Needs Inno Setup 6 (ISCC.exe): $env:ISCC, PATH, or the default install folder.
# -Version defaults to <Version> of src/PcCleaner/PcCleaner.csproj, so the setup and the program always carry the same number.
param(
    [string]$Version,
    [string]$OutDir = 'publish'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'src\PcCleaner\PcCleaner.csproj')
    $Version = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Bad version: $Version" }
$app = Join-Path $root "$OutDir\app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

dotnet publish (Join-Path $root 'src\PcCleaner\PcCleaner.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (-not (Test-Path (Join-Path $app 'WaterCa7sPcCleaner.exe'))) { throw 'The publish has no WaterCa7sPcCleaner.exe' }

$iscc = $env:ISCC
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 (ISCC.exe) is not installed: $iscc" }

& $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$(Join-Path $root $OutDir)" (Join-Path $root 'installer\WaterCa7sPcCleaner.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Join-Path $root "$OutDir\WaterCa7sPcCleaner-Setup.exe"
if (-not (Test-Path $setup)) { throw "No setup at $setup" }
"Setup: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
