# Build self-contained Windows release and produce a zip artifact.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/make_installer.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$out = Join-Path $root "dist"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish OsmoActionViewer/OsmoActionViewer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$out/OsmoActionViewer"

Compress-Archive -Path "$out/OsmoActionViewer/*" -DestinationPath "$out/OsmoActionViewer-win-x64.zip" -Force

Write-Host "Built: $out/OsmoActionViewer-win-x64.zip"
