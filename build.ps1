# Build Vivint Desktop as a self-contained Windows app folder (no .NET install needed).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
# Output: dist\VivintDesktop.exe (runtime bundled inside) and libmpv-2.dll (video).
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src\VivintDesktop\VivintDesktop.csproj"
$dist = Join-Path $PSScriptRoot "dist"

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

Write-Host "Restoring and publishing..." -ForegroundColor Cyan
# Single-file: the .NET runtime is bundled (compressed) inside VivintDesktop.exe; WPF/WebView2
# native DLLs are extracted to a per-user cache on first run. libmpv-2.dll is excluded from the
# bundle (ExcludeFromSingleFile in the csproj) and stays next to the exe.
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $dist
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Get-ChildItem $dist -Filter *.xml | Remove-Item -Force
# createdump.exe is the runtime's crash-dump helper; the app doesn't need it.
Remove-Item (Join-Path $dist "createdump.exe") -Force -ErrorAction SilentlyContinue

$exe = Join-Path $dist "VivintDesktop.exe"
$mb = [math]::Round(((Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host "`nDone ($mb MB). Run:" -ForegroundColor Green
Write-Host "  $exe"
