<#
.SYNOPSIS
    Downloads the raw dictionary source data into data/raw/.
.DESCRIPTION
    Run once before ZhongwenLens.DataBuild. None of these files are committed:
    CC-CEDICT is CC BY-SA 4.0 and jieba's table is large, so both are fetched
    from source and turned into dictionary.db locally.

    Sources:
      cedict_ts.u8    CC-CEDICT, ~120k entries          CC BY-SA 4.0
      jieba_dict.txt  jieba unigram frequency table     MIT
      hsk.json        complete-hsk-vocabulary (3.0)     MIT
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$rawDir = Join-Path $PSScriptRoot '..\data\raw'
$null = New-Item -ItemType Directory -Force -Path $rawDir
$rawDir = (Resolve-Path $rawDir).Path

function Get-Source {
    param([string]$Name, [string]$Url, [int]$MinBytes)

    $dest = Join-Path $rawDir $Name
    if ((Test-Path $dest) -and -not $Force) {
        $have = (Get-Item $dest).Length
        Write-Host ("  skip  {0,-16} already present ({1:N1} MB)" -f $Name, ($have / 1MB)) -ForegroundColor DarkGray
        return $dest
    }

    Write-Host ("  get   {0,-16} {1}" -f $Name, $Url) -ForegroundColor Cyan
    $tmp = "$dest.partial"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -TimeoutSec 300 -UseBasicParsing
    } catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "failed to download $Name from $Url : $($_.Exception.Message)"
    }

    # Guard against a truncated transfer or an HTML error page saved as data.
    $size = (Get-Item $tmp).Length
    if ($size -lt $MinBytes) {
        Remove-Item $tmp -Force
        throw "$Name is only $size bytes (expected at least $MinBytes) - the source may have moved"
    }

    Move-Item $tmp $dest -Force
    Write-Host ("        {0,-16} {1:N1} MB" -f 'ok', ($size / 1MB)) -ForegroundColor Green
    return $dest
}

Write-Host "Fetching dictionary source data -> $rawDir" -ForegroundColor White

# --- CC-CEDICT (zipped; the archive holds a single .u8 file) --------------------
$zip = Get-Source -Name 'cedict.zip' `
    -Url 'https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.zip' `
    -MinBytes 2MB

$cedict = Join-Path $rawDir 'cedict_ts.u8'
if ((-not (Test-Path $cedict)) -or $Force) {
    Write-Host "  unzip cedict_ts.u8" -ForegroundColor Cyan
    Expand-Archive -Path $zip -DestinationPath $rawDir -Force
    $extracted = Get-ChildItem $rawDir -Filter '*.u8' | Select-Object -First 1
    if (-not $extracted) { throw "no .u8 file inside cedict.zip" }
    if ($extracted.FullName -ne $cedict) { Move-Item $extracted.FullName $cedict -Force }
}

# --- jieba unigram frequencies (word / count / POS, space separated) -----------
Get-Source -Name 'jieba_dict.txt' `
    -Url 'https://raw.githubusercontent.com/fxsjy/jieba/master/jieba/dict.txt' `
    -MinBytes 3MB | Out-Null

# --- HSK 3.0 word lists -------------------------------------------------------
Get-Source -Name 'hsk.json' `
    -Url 'https://raw.githubusercontent.com/drkameleon/complete-hsk-vocabulary/main/complete.json' `
    -MinBytes 5MB | Out-Null

Write-Host ""
Write-Host "Done. Next: dotnet run --project src\ZhongwenLens.DataBuild" -ForegroundColor White
Write-Host "CC-CEDICT is CC BY-SA 4.0 - see THIRD-PARTY-NOTICES.md before distributing." -ForegroundColor Yellow
