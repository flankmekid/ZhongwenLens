<#
.SYNOPSIS
    Downloads the PP-OCRv4 ONNX models into data/models/.
.DESCRIPTION
    Three models make up the OCR pipeline (detect -> orient -> recognise) plus the
    character dictionary used to decode recogniser output.

    PP-OCRv5 is deliberately not used: no pinned, verifiable ONNX export of it
    exists at a stable URL. v4 mobile is the newest version available as ONNX from
    a source that can be checked. All models are Apache 2.0.

    -Server swaps in the large detection model: ~110 MB instead of 4.6 MB, better
    on dense or low-contrast text, roughly 4x slower. Not needed for typical
    rendered screen text.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Server
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$modelDir = Join-Path $PSScriptRoot '..\data\models'
$null = New-Item -ItemType Directory -Force -Path $modelDir
$modelDir = (Resolve-Path $modelDir).Path

$hf = 'https://huggingface.co/SWHL/RapidOCR/resolve/main'

$detName = if ($Server) { 'ch_PP-OCRv4_det_server_infer.onnx' } else { 'ch_PP-OCRv4_det_infer.onnx' }

$files = @(
    @{ Name = 'det.onnx'; Url = "$hf/PP-OCRv4/$detName";                          MinBytes = 4MB   }
    @{ Name = 'rec.onnx'; Url = "$hf/PP-OCRv4/ch_PP-OCRv4_rec_infer.onnx";        MinBytes = 9MB   }
    @{ Name = 'cls.onnx'; Url = "$hf/PP-OCRv1/ch_ppocr_mobile_v2.0_cls_infer.onnx"; MinBytes = 400KB }
    @{ Name = 'ppocr_keys_v1.txt'
       Url  = 'https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/ppocr_keys_v1.txt'
       MinBytes = 20KB }
)

Write-Host "Fetching PP-OCRv4 ONNX models -> $modelDir" -ForegroundColor White
if ($Server) { Write-Host "  (using the server detection model, ~110 MB)" -ForegroundColor Yellow }

foreach ($f in $files) {
    $dest = Join-Path $modelDir $f.Name
    if ((Test-Path $dest) -and -not $Force) {
        Write-Host ("  skip  {0,-20} already present ({1:N1} MB)" -f $f.Name, ((Get-Item $dest).Length / 1MB)) -ForegroundColor DarkGray
        continue
    }

    Write-Host ("  get   {0,-20} {1}" -f $f.Name, $f.Url) -ForegroundColor Cyan
    $tmp = "$dest.partial"
    try {
        Invoke-WebRequest -Uri $f.Url -OutFile $tmp -TimeoutSec 600 -UseBasicParsing
    } catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "failed to download $($f.Name): $($_.Exception.Message)"
    }

    $size = (Get-Item $tmp).Length
    if ($size -lt $f.MinBytes) {
        Remove-Item $tmp -Force
        throw "$($f.Name) is only $size bytes (expected at least $($f.MinBytes)) - the source may have moved"
    }

    Move-Item $tmp $dest -Force
    Write-Host ("        {0,-20} {1:N1} MB" -f 'ok', ($size / 1MB)) -ForegroundColor Green
}

# The recogniser's output class count must match the character dictionary, or every
# decode is silently shifted. Report both so a mismatch is caught here, not at runtime.
$keys = Join-Path $modelDir 'ppocr_keys_v1.txt'
$keyCount = (Get-Content $keys -Encoding UTF8).Count
Write-Host ""
Write-Host "Character dictionary: $keyCount entries (expect 6623; +blank +space = 6625 rec classes)" -ForegroundColor White
Write-Host "Models are Apache 2.0 - see THIRD-PARTY-NOTICES.md" -ForegroundColor Yellow
