<#
.SYNOPSIS
  Development only: installs (or removes) the locally built virtual DualSense driver.

.DESCRIPTION
  Run from an administrator PowerShell after build-driver.ps1:
    install:   .\dev-install.ps1
    remove:    .\dev-install.ps1 -Uninstall

  Install trusts the package's signing certificate (LocalMachine Root + TrustedPublisher) and
  adds the driver to the driver store. It creates no device: HapticLink's pad service (or the
  test tool) does that. Uninstall removes every HapticLinkPad driver package and the
  certificate again.
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an administrator PowerShell.'
}

$package = Resolve-Path (Join-Path $PSScriptRoot '..\..\artifacts\driver\package')
$cerPath = Join-Path $package 'HapticLinkPad.cer'
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2($cerPath)

if ($Uninstall) {
    $published = pnputil /enum-drivers | Out-String
    $names = [regex]::Matches($published, '(?ms)Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+peripheralhubpad\.inf') |
        ForEach-Object { $_.Groups[1].Value }
    foreach ($name in $names) {
        pnputil /delete-driver $name /uninstall /force
    }
    foreach ($store in 'Root', 'TrustedPublisher') {
        certutil -delstore $store $cert.Thumbprint | Out-Null
    }
    Write-Host "Removed $($names.Count) driver package(s) and the certificate $($cert.Thumbprint)."
    return
}

foreach ($store in 'Root', 'TrustedPublisher') {
    certutil -addstore -f $store $cerPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't add the certificate to $store ($LASTEXITCODE)." }
}
pnputil /add-driver (Join-Path $package 'HapticLinkPad.inf') /install
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 259) { throw "pnputil failed ($LASTEXITCODE)." }
Write-Host "Installed. Certificate $($cert.Thumbprint) is trusted; the driver is in the driver store."
