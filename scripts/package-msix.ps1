<#
.SYNOPSIS
    Builds a signed MSIX installer for Zhongwen Lens.
.DESCRIPTION
    Publishes the app fully standalone (.NET and Windows App SDK both self-contained), stages it
    with the dictionary and OCR models, then packs and signs it.

    The app project itself stays unpackaged. Converting it to single-project MSIX would make
    every F5 in Visual Studio deploy a package and require a certificate to debug; keeping the
    packaging in a script leaves the development loop alone and makes the installer reproducible
    from a clean checkout.

    Signing uses a self-signed certificate because there is no code-signing certificate for this
    project. Windows will not install an MSIX signed by an untrusted issuer, so the certificate
    must be imported into Trusted People first — install-msix.ps1 does both steps.
.PARAMETER Version
    Package version, must be four parts. The revision should stay 0; the Store reserves it.
.PARAMETER Publisher
    Certificate subject. Must match the manifest Identity/Publisher exactly or install fails.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$Publisher = 'CN=ZhongwenLens',
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
$stageDir = Join-Path $artifactDir 'msix-stage'
$publishDir = Join-Path $artifactDir 'publish'
$msixPath = Join-Path $artifactDir "ZhongwenLens-$Version-$Platform.msix"
$certPath = Join-Path $artifactDir 'ZhongwenLens.cer'
$pfxPath = Join-Path $artifactDir 'ZhongwenLens.pfx'

function Find-SdkTool {
    param([string]$Name)

    $roots = @(
        'C:\Program Files (x86)\Windows Kits\10\bin',
        'C:\Program Files\Windows Kits\10\bin')

    $tool = $roots |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem $_ -Recurse -Filter $Name -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $tool) { throw "$Name not found. Install the Windows 10/11 SDK." }
    return $tool.FullName
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'

Write-Host "makeappx  $makeappx" -ForegroundColor DarkGray
Write-Host "signtool  $signtool" -ForegroundColor DarkGray

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
    Write-Host ""
    Write-Host "Publishing $Configuration|$Platform (fully self-contained)..." -ForegroundColor White

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Self-contained on both counts, so the installed app needs neither the .NET runtime nor the
    # Windows App SDK runtime present on the target machine.
    & dotnet publish $appProject `
        -c $Configuration `
        -p:Platform=$Platform `
        -r "win-$($Platform.ToLower())" `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -o $publishDir | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $publishDir 'ZhongwenLens.App.exe'))) {
    throw "publish output has no ZhongwenLens.App.exe"
}

# --- 3. Stage ------------------------------------------------------------------------------
Write-Host ""
Write-Host "Staging package contents..." -ForegroundColor White

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
$null = New-Item -ItemType Directory -Force -Path $stageDir

Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force

# The manifest and images live in the project; the app finds its data next to the executable.
Copy-Item (Join-Path $repoRoot 'src\ZhongwenLens.App\Images') $stageDir -Recurse -Force
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $stageDir -Force

$stagedData = Join-Path $stageDir 'data'
$null = New-Item -ItemType Directory -Force -Path $stagedData
Copy-Item (Join-Path $dataDir 'dictionary.db') $stagedData -Force
Copy-Item (Join-Path $dataDir 'models') $stagedData -Recurse -Force

# A publish leaves the unpackaged manifest behind; inside a package it is ignored but confusing.
Get-ChildItem $stageDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

# --- 4. Manifest ---------------------------------------------------------------------------
$manifestSource = Join-Path $repoRoot 'src\ZhongwenLens.App\Package.appxmanifest'
$manifestTarget = Join-Path $stageDir 'AppxManifest.xml'

[xml]$manifest = Get-Content $manifestSource -Raw -Encoding UTF8
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Identity.ProcessorArchitecture = $Platform.ToLower()
$manifest.Save($manifestTarget)

Write-Host ("  identity  {0} {1} {2}" -f $manifest.Package.Identity.Name, $Version, $Publisher) -ForegroundColor DarkGray

$sizeMb = [math]::Round(((Get-ChildItem $stageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("  staged    {0} MB" -f $sizeMb) -ForegroundColor DarkGray

# --- 5. Certificate ------------------------------------------------------------------------
# Reused if it already exists, so a rebuilt package keeps the same identity and upgrades in
# place instead of installing alongside the previous version.
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if (-not $certificate) {
    Write-Host ""
    Write-Host "Creating a self-signed code-signing certificate for $Publisher..." -ForegroundColor White

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Zhongwen Lens (self-signed)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(5)
}

Write-Host ("  thumbprint {0}" -f $certificate.Thumbprint) -ForegroundColor DarkGray

$null = New-Item -ItemType Directory -Force -Path $artifactDir
Export-Certificate -Cert $certificate -FilePath $certPath -Force | Out-Null

$password = ConvertTo-SecureString -String 'zhongwenlens' -Force -AsPlainText
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password -Force | Out-Null

# --- 6. Pack and sign ----------------------------------------------------------------------
Write-Host ""
Write-Host "Packing..." -ForegroundColor White

if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

& $makeappx pack /d $stageDir /p $msixPath /o | Where-Object { $_ -match 'error|warning|Package creation' }
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

Write-Host "Signing..." -ForegroundColor White
& $signtool sign /fd SHA256 /a /f $pfxPath /p 'zhongwenlens' $msixPath | Where-Object { $_ -match 'error|Successfully' }
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

$packageMb = [math]::Round((Get-Item $msixPath).Length / 1MB, 1)

Write-Host ""
Write-Host "package  $msixPath ($packageMb MB)" -ForegroundColor Green
Write-Host "cert     $certPath" -ForegroundColor Green
Write-Host ""
Write-Host "To install, run as administrator:  scripts\install-msix.ps1" -ForegroundColor White
