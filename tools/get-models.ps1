# Downloads the two small ONNX models used by the OPTIONAL face recognition feature into ..\models.
# Both come from the OpenCV Zoo (https://github.com/opencv/opencv_zoo):
#   - YuNet face detector   (MIT licence,        about 0.2 MB)
#   - SFace face recogniser (Apache-2.0 licence, about 37 MB)
# They are only read when the user turns face recognition on. Nothing is ever uploaded.
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$models = Join-Path (Split-Path $PSScriptRoot -Parent) "models"
New-Item -ItemType Directory -Force $models | Out-Null

$files = @(
    @{ Name = "face_detection_yunet_2023mar.onnx";   MinBytes = 100000;   Url = "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx" },
    @{ Name = "face_recognition_sface_2021dec.onnx"; MinBytes = 30000000; Url = "https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx" }
)

foreach ($f in $files) {
    $dest = Join-Path $models $f.Name
    if ((Test-Path $dest) -and (Get-Item $dest).Length -ge $f.MinBytes) { Write-Host "Already present: $($f.Name)"; continue }
    Write-Host "Downloading $($f.Name)..."
    Invoke-WebRequest -Uri $f.Url -OutFile $dest -UseBasicParsing
    if ((Get-Item $dest).Length -lt $f.MinBytes) { Remove-Item $dest -Force; throw "$($f.Name) looks incomplete. Please try again." }
}
Write-Host "Face models are ready in $models" -ForegroundColor Green
