<#
.SYNOPSIS
  Builds the "service" folder the HapticLink app ships and its setup installs:
    artifacts\service\HapticLink.Service.exe   the service (also setup/removal), framework-dependent single file
    artifacts\service\HapticLinkPadHost.dll    its SwDeviceCreate helper (C)
    artifacts\service\driver\HapticLinkPad.*   the signed virtual DualSense driver package (+ its .cer)

.DESCRIPTION
  Signing: -PfxPath/-PfxPassword (release builds: the certificate from GitHub secrets, see make-release-cert.ps1),
  else the development certificate build-driver.ps1 makes. -Timestamp adds a timestamp so signatures outlive the
  certificate (needs internet).
  The service has its own <Version> (in its csproj), separate from the app: the app re-runs setup (one admin prompt)
  only when the bundled service is newer than the installed one, so bump it when the service, driver or helper change.
#>
param(
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$Timestamp
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'artifacts\service'

$signArgs = @{}
if ($PfxPath) { $signArgs = @{ PfxPath = $PfxPath; PfxPassword = $PfxPassword } }
if ($Timestamp) { $signArgs.Timestamp = $true }
& (Join-Path $PSScriptRoot 'HapticLink.Pad\build-driver.ps1') @signArgs
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Driver build failed ($LASTEXITCODE)." }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
& $msbuild (Join-Path $PSScriptRoot 'HapticLink.PadHost\HapticLinkPadHost.vcxproj') -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "PadHost build failed ($LASTEXITCODE)." }

$publish = Join-Path $root 'artifacts\service-publish'
dotnet publish (Join-Path $root 'src\HapticLink.Service\HapticLink.Service.csproj') -c Release -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($LASTEXITCODE)." }

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $out 'driver') | Out-Null
Copy-Item (Join-Path $publish 'HapticLink.Service.exe') $out
Copy-Item (Join-Path $root 'artifacts\padhost\bin\HapticLinkPadHost.dll') $out
Copy-Item (Join-Path $root 'artifacts\driver\package\*') (Join-Path $out 'driver')
Write-Host "Service folder ready: $out"
Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($out.Length + 1) }
