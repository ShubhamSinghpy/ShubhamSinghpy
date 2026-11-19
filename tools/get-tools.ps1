# Downloads ffprobe.exe (part of FFmpeg) into this folder. Used only to READ video metadata.
# Also copies ffmpeg.exe, used only by the optional face recognition feature to read frames from videos.
# Source: gyan.dev FFmpeg "release essentials" build.
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tools = $PSScriptRoot
$url   = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$zip   = Join-Path $env:TEMP "ffmpeg-release-essentials.zip"
$tmp   = Join-Path $env:TEMP "ffmpeg-extract"

Write-Host "Downloading FFmpeg package (about 80-100 MB)..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $tmp -Force

$probe = Get-ChildItem -Path $tmp -Recurse -Filter "ffprobe.exe" | Select-Object -First 1
if (-not $probe) { throw "ffprobe.exe was not found in the downloaded package." }
Copy-Item $probe.FullName (Join-Path $tools "ffprobe.exe") -Force

# ffmpeg.exe is only needed by the optional face recognition feature, to read a few frames from videos.
$mpeg = Get-ChildItem -Path $tmp -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
if ($mpeg) { Copy-Item $mpeg.FullName (Join-Path $tools "ffmpeg.exe") -Force }

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "ffprobe.exe is ready in $tools" -ForegroundColor Green
