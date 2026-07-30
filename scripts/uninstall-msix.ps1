<#
.SYNOPSIS
    Removes the app and, optionally, the trust entry for its self-signed certificate.
.DESCRIPTION
    Uninstalling the package leaves the certificate in LocalMachine\TrustedPeople. That is
    usually what you want between rebuilds — otherwise every reinstall needs elevation again —
    so -RemoveCertificate is opt-in.

    Saved words and settings live in the package's own data folder and are removed by Windows
    along with the package. Export them first if you want to keep them.
#>
[CmdletBinding()]
param(
    [switch]$RemoveCertificate
)

$ErrorActionPreference = 'Stop'

$package = Get-AppxPackage -Name 'ZhongwenLens'
if ($package) {
    Write-Host ("Removing {0} {1}..." -f $package.Name, $package.Version) -ForegroundColor White
    Remove-AppxPackage -Package $package.PackageFullName
    Write-Host "removed" -ForegroundColor Green
} else {
    Write-Host "ZhongwenLens is not installed" -ForegroundColor DarkGray
}

if (-not $RemoveCertificate) { return }

$elevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    Write-Warning "not elevated; leaving the certificate in place"
    return
}

Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Subject -eq 'CN=ZhongwenLens' } |
    ForEach-Object {
        Write-Host ("Removing certificate {0}..." -f $_.Thumbprint) -ForegroundColor White
        Remove-Item $_.PSPath -Force
    }
