param(
    [switch]$SkipTests,
    [switch]$SkipTools
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "=== Professional Media Organizer - build ===" -ForegroundColor Cyan

# 1. .NET SDK check
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK is not installed." -ForegroundColor Red
    Write-Host "Install it once from https://dotnet.microsoft.com/download/dotnet/8.0 (choose 'SDK', Windows x64), then run this script again."
    exit 1
}
$sdk = (dotnet --version)
Write-Host "Using .NET SDK $sdk"
if ([int]($sdk.Split('.')[0]) -lt 8) {
    Write-Host "Please install .NET SDK 8 or newer." -ForegroundColor Red
    exit 1
}

# 2. ffprobe (needed for video GPS / dates). Downloaded once into tools\
if (-not $SkipTools -and -not (Test-Path "$root\tools\ffprobe.exe")) {
    Write-Host "Downloading ffprobe (one time)..."
    try { & "$root\tools\get-tools.ps1" }
    catch { Write-Host "Could not download ffprobe: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "The app still works; videos will just have limited metadata (see docs\USER_GUIDE.md)." -ForegroundColor Yellow }
}

# 2b. Face models for the OPTIONAL face recognition feature (about 40 MB, one time)
if (-not $SkipTools -and -not ((Test-Path "$root\models\face_detection_yunet_2023mar.onnx") -and (Test-Path "$root\models\face_recognition_sface_2021dec.onnx"))) {
    Write-Host "Downloading face models (one time)..."
    try { & "$root\tools\get-models.ps1" }
    catch { Write-Host "Could not download the face models: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "The app still works; the optional face recognition feature will ask for them (run tools\get-models.ps1)." -ForegroundColor Yellow }
}

# 3. Tests
if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test "$root\tests\MediaOrganizer.Tests\MediaOrganizer.Tests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Run 'build.bat -SkipTests' to build anyway, and please report the failing test names." }
}

# 4. Publish standalone single-file exe (no .NET install needed to RUN it)
$out = Join-Path $root "dist\MediaOrganizer"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host "Publishing standalone executable..." -ForegroundColor Cyan
dotnet publish "$root\src\MediaOrganizer.App\MediaOrganizer.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# Native-package leftovers that are not needed to run (import libraries, debug symbols).
Get-ChildItem $out -File | Where-Object { $_.Extension -in ".lib", ".pdb" } | Remove-Item -Force

# 5. Put ffprobe (and ffmpeg, for optional face recognition in videos) next to the exe
New-Item -ItemType Directory -Force "$out\tools" | Out-Null
foreach ($t in "ffprobe.exe", "ffmpeg.exe") {
    if (Test-Path "$root\tools\$t") { Copy-Item "$root\tools\$t" "$out\tools\$t" -Force }
}
# Face models are copied beside the exe by the project file ("models" folder).
if (-not (Test-Path "$out\models\face_recognition_sface_2021dec.onnx")) {
    Write-Host "Note: face models are missing from the output; run tools\get-models.ps1 and build again to include them." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "SUCCESS: $out\MediaOrganizer.exe" -ForegroundColor Green
