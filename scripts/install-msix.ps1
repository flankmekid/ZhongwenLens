<#
.SYNOPSIS
    Installs the signed MSIX, trusting its certificate first.
.DESCRIPTION
    Windows refuses to install an MSIX whose signing certificate it does not trust, and a
    self-signed certificate is untrusted by definition. Importing it into LocalMachine\TrustedPeople
    is what makes the package installable — that store is specifically for "I vouch for this
    publisher" decisions, and it is narrower than dropping the certificate into Trusted Root.

    Importing a certificate to LocalMachine requires elevation; the install itself does not.

    Run scripts\uninstall-msix.ps1 to remove both the app and the trust entry.
#>
[CmdletBinding()]
param(
    [string]$Package,
    [string]$Certificate
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDir = Join-Path $repoRoot 'artifacts'

if (-not $Package) {
    $Package = Get-ChildItem $artifactDir -Filter '*.msix' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Certificate) { $Certificate = Join-Path $artifactDir 'ZhongwenLens.cer' }

if (-not $Package -or -not (Test-Path $Package)) {
    throw "no .msix found in $artifactDir. Run scripts\package-msix.ps1 first."
}
if (-not (Test-Path $Certificate)) {
    throw "certificate not found at $Certificate. Run scripts\package-msix.ps1 first."
}

$elevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    throw "this must run as administrator: trusting the signing certificate writes to the " +
          "LocalMachine certificate store."
}

Write-Host "Trusting $Certificate..." -ForegroundColor White
Import-Certificate -FilePath $Certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

Write-Host "Installing $Package..." -ForegroundColor White
Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion

$installed = Get-AppxPackage -Name 'ZhongwenLens'
if (-not $installed) { throw "install reported success but the package is not registered" }

Write-Host ""
Write-Host ("installed  {0} {1}" -f $installed.Name, $installed.Version) -ForegroundColor Green
Write-Host ("location   {0}" -f $installed.InstallLocation) -ForegroundColor Green
Write-Host ""
Write-Host "Launch it from the Start menu, then press Ctrl+Alt+Z." -ForegroundColor White
Write-Host "Run at login can be enabled in Settings > Apps > Startup." -ForegroundColor White
