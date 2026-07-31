<#
.SYNOPSIS
    Builds the Zhongwen Lens MSI installer.
.DESCRIPTION
    Publishes the app fully standalone (.NET and the Windows App SDK both self-contained),
    stages it with the dictionary and OCR models, then builds an MSI with WiX.

    No signing, and therefore no certificate for anyone to import. An unsigned MSI raises a
    SmartScreen warning that the user can dismiss; an unsigned or self-signed MSIX is refused
    outright, which is why this replaced the MSIX packaging.

    Requires the WiX build tool:
        dotnet tool install --global wix
        wix extension add --global WixToolset.UI.wixext
        wix extension add --global WixToolset.Util.wixext
.PARAMETER Version
    Product version, three or four parts. MSI only compares the first three for upgrade
    decisions, so the revision field is ignored when deciding whether to replace an install.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $repoRoot 'src\ZhongwenLens.App\ZhongwenLens.App.csproj'
$dataDir = Join-Path $repoRoot 'data'
$artifactDir = Join-Path $repoRoot 'artifacts'
$stageDir = Join-Path $artifactDir 'msi-stage'
$publishDir = Join-Path $artifactDir 'publish'
$wxs = Join-Path $repoRoot 'installer\ZhongwenLens.wxs'
$msiPath = Join-Path $artifactDir "ZhongwenLens-$Version-$Platform.msi"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw @"
WiX is not installed. Install it with:

    dotnet tool install --global wix
    wix extension add --global WixToolset.UI.wixext
    wix extension add --global WixToolset.Util.wixext
"@
}

# --- 1. Verify the data is built -----------------------------------------------------------
foreach ($required in 'dictionary.db', 'models\det.onnx', 'models\rec.onnx', 'models\cls.onnx',
                      'models\ppocr_keys_v1.txt') {
    if (-not (Test-Path (Join-Path $dataDir $required))) {
        throw "missing data\$required. Run scripts\fetch-data.ps1, scripts\fetch-models.ps1 and " +
              "dotnet run --project src\ZhongwenLens.DataBuild first."
    }
}

# --- 2. Publish ----------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "Publishing $Configuration|$Platform (fully self-contained)..." -ForegroundColor White

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # The same version goes into the assembly as into the package, so the installed .exe can
    # never report a different version from the installer that placed it there.
    & dotnet publish $appProject `
        -c $Configuration `
        -p:Platform=$Platform `
        -r "win-$($Platform.ToLower())" `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -p:Version=$Version `
        -p:FileVersion=$Version `
        -p:AssemblyVersion=$Version `
        -o $publishDir | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $publishDir 'ZhongwenLens.App.exe'))) {
    throw "publish output has no ZhongwenLens.App.exe"
}

# --- 3. Stage ------------------------------------------------------------------------------
Write-Host "Staging installer payload..." -ForegroundColor White

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
$null = New-Item -ItemType Directory -Force -Path $stageDir

Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $stageDir -Force

# The app looks for its data next to the executable.
$stagedData = Join-Path $stageDir 'data'
$null = New-Item -ItemType Directory -Force -Path $stagedData
Copy-Item (Join-Path $dataDir 'dictionary.db') $stagedData -Force
Copy-Item (Join-Path $dataDir 'models') $stagedData -Recurse -Force

Get-ChildItem $stageDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

# --- 4. Licence page -----------------------------------------------------------------------
# WixUI_Minimal shows the licence as RTF, so the plain-text LICENSE is wrapped into one.
$licenseSource = Join-Path $repoRoot 'LICENSE'
$licenseRtf = Join-Path $stageDir 'LICENSE.rtf'

if (Test-Path $licenseSource) {
    $body = (Get-Content $licenseSource -Raw) -replace '\\', '\\\\' -replace '([{}])', '\$1'
    $body = ($body -split "`r?`n") -join '\par' + '\par'
} else {
    $body = 'Zhongwen Lens\par'
}

# Minimal RTF: a header, one font, then the text. Nothing here needs a real RTF writer.
Set-Content -Path $licenseRtf -Encoding ASCII -Value @"
{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}
\fs18 $body}
"@

$sizeMb = [math]::Round(((Get-ChildItem $stageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("  staged  {0} MB across {1:N0} files" -f $sizeMb,
    (Get-ChildItem $stageDir -Recurse -File).Count) -ForegroundColor DarkGray

# --- 5. Build the MSI ----------------------------------------------------------------------
Write-Host ""
Write-Host "Building MSI (this compresses ~$sizeMb MB, so it takes a minute)..." -ForegroundColor White

if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

# -sw1077 suppresses the "[INSTALLFOLDER] looks like a property reference" warning on
# WixShellExecTarget. It is a string literal there by design, which is the case WiX's own
# warning text says to ignore; see the comment in ZhongwenLens.wxs.
& wix build $wxs `
    -arch $Platform `
    -d "StageDir=$stageDir" `
    -d "Version=$Version" `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -sw1077 `
    -o $msiPath

if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

$msiMb = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)

Write-Host ""
Write-Host "installer  $msiPath ($msiMb MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Install it by double-clicking, or:" -ForegroundColor White
Write-Host "  msiexec /i `"$msiPath`"" -ForegroundColor DarkGray
Write-Host "No elevation needed - it installs per-user under LocalAppData." -ForegroundColor DarkGray
