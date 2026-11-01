<#
.SYNOPSIS
  One-time: makes the certificate that signs the virtual DualSense driver in releases, for GitHub secrets.

.DESCRIPTION
  Windows loads HapticLink's user-mode driver when its certificate is trusted on the PC: the app's one-time setup
  ("Full DualSense features over Bluetooth") adds it to Trusted Root and Trusted Publishers. Every release must be
  signed with the same certificate, so it lives in two GitHub secrets the release workflow reads:
    PAD_CERT_PFX_B64     the .pfx (certificate + private key), base64
    PAD_CERT_PASSWORD    its password

  This writes the .pfx and a text file with the base64 to -OutDir (outside the repository!) and prints what to do.
  It also keeps the password next to the .pfx as HapticLinkPad-release.password, encrypted for this Windows user (DPAPI),
  so local release builds (build-installer.ps1 -Install) sign the driver exactly like GitHub does; a copy of that file
  is useless to anyone else or on another PC.
  Keep the .pfx somewhere safe and private: anyone with it could sign code that PCs which ran setup trust.

.EXAMPLE
  ./make-release-cert.ps1 -OutDir "D:\Vibe Projects\Signing\HapticLink"   # outside the repository, where build-installer.ps1 looks
#>
param(
    [Parameter(Mandatory)] [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutDir)) { throw 'Give -OutDir as a full path outside the repository.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$full = [IO.Path]::GetFullPath($OutDir)
# Compare whole folder names, so a sibling such as ..\HapticLink-signing isn't mistaken for a folder inside the repository.
if (($full.TrimEnd('\') + '\').StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the certificate outside the repository.' }
New-Item -ItemType Directory -Force $full | Out-Null

$bytes = New-Object byte[] 24
$rng = [Security.Cryptography.RandomNumberGenerator]::Create() # Fill() isn't in Windows PowerShell 5.1's .NET
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$password = [Convert]::ToBase64String($bytes)

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=HapticLink Driver' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -KeySpec Signature `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
$pfx = Join-Path $full 'HapticLinkPad-release.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
# The password for local release builds, readable only by this Windows user.
ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content (Join-Path $full 'HapticLinkPad-release.password') -Encoding ascii

$b64 = Join-Path $full 'PAD_CERT_PFX_B64.txt'
[IO.File]::WriteAllText($b64, [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
$pw = Join-Path $full 'PAD_CERT_PASSWORD.txt'
[IO.File]::WriteAllText($pw, $password)

Write-Host ""
Write-Host "Made $pfx (thumbprint $($cert.Thumbprint), valid 10 years)."
Write-Host "Add two repository secrets (GitHub > Settings > Secrets and variables > Actions > New repository secret):"
Write-Host "  PAD_CERT_PFX_B64   = the contents of $b64"
Write-Host "  PAD_CERT_PASSWORD  = the contents of $pw"
Write-Host "Then delete the two .txt files. Keep the .pfx and HapticLinkPad-release.password where they are: local release"
Write-Host "builds sign with them (back the .pfx up privately; the password file only opens for this Windows user)."
