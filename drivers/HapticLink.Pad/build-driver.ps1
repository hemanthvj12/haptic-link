<#
.SYNOPSIS
  Builds, catalogs and signs HapticLink's virtual DualSense driver package.

.DESCRIPTION
  1. Unpacks the parts of Microsoft's WDK NuGet package the build needs into artifacts\wdk
     (UMDF headers + stub library, kernel HID header, Inf2Cat), downloading it once.
  2. Builds HapticLinkPad.vcxproj with MSBuild (ordinary MSVC toolset).
  3. Stages HapticLinkPad.inf + .dll in artifacts\driver\package, makes the catalog with
     Inf2Cat and signs the DLL and catalog.

  Signing uses -PfxPath/-PfxPassword when given (release builds: the certificate kept in GitHub
  secrets), else a development certificate made on first use in artifacts\driver\cert. Either
  way the matching .cer is copied next to the package; installing the driver means trusting that
  certificate (LocalMachine Root + TrustedPublisher), which needs administrator rights.
#>
param(
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$Timestamp
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifacts = Join-Path $root 'artifacts'
$wdk = Join-Path $artifacts 'wdk\c'
$wdkVersion = '10.0.26100.6584'

# --- 1. driver kit parts -------------------------------------------------------------------
if (-not (Test-Path (Join-Path $wdk 'Include\wdf\umdf\2.15\wdf.h'))) {
    $nupkg = Join-Path $artifacts "wdk\microsoft.windows.wdk.x64.$wdkVersion.nupkg"
    if (-not (Test-Path $nupkg)) {
        New-Item -ItemType Directory -Force (Split-Path $nupkg) | Out-Null
        $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.wdk.x64/$wdkVersion/microsoft.windows.wdk.x64.$wdkVersion.nupkg"
        Write-Host "Downloading the WDK package ($wdkVersion)..."
        Invoke-WebRequest $url -OutFile $nupkg
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $wanted = @('c/Include/wdf/umdf/2.15/', 'c/Include/10.0.26100.0/km/', 'c/Lib/wdf/umdf/x64/2.15/', 'c/bin/10.0.26100.0/x86/')
        foreach ($entry in $zip.Entries) {
            $name = [Uri]::UnescapeDataString($entry.FullName)
            if ($entry.Name -eq '' -or -not ($wanted | Where-Object { $name.StartsWith($_) })) { continue }
            $target = Join-Path (Join-Path $artifacts 'wdk') $name
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $zip.Dispose() }
}

# --- 2. build -------------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild (Visual Studio with C++ tools) not found.' }
& $msbuild (Join-Path $PSScriptRoot 'HapticLinkPad.vcxproj') -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Driver build failed ($LASTEXITCODE)." }

# --- 3. package, catalog, sign --------------------------------------------------------------
$package = Join-Path $artifacts 'driver\package'
Remove-Item -Recurse -Force $package -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $package | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'HapticLinkPad.inf') $package
Copy-Item (Join-Path $artifacts 'driver\bin\HapticLinkPad.dll') $package

if (-not $PfxPath) {
    $certDir = Join-Path $artifacts 'driver\cert'
    $PfxPath = Join-Path $certDir 'HapticLinkPad-dev.pfx'
    $PfxPassword = 'peripheralhub-dev'
    if (-not (Test-Path $PfxPath)) {
        New-Item -ItemType Directory -Force $certDir | Out-Null
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=HapticLink Driver (development)' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -KeySpec Signature `
            -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
        $secure = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $secure | Out-Null
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
        Write-Host "Made a development signing certificate: $PfxPath"
    }
}
$pfx = New-Object Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword)
[IO.File]::WriteAllBytes((Join-Path $package 'HapticLinkPad.cer'), $pfx.Export('Cert'))

$inf2cat = Join-Path $artifacts 'wdk\c\bin\10.0.26100.0\x86\Inf2Cat.exe'
& $inf2cat "/driver:$package" /os:10_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed ($LASTEXITCODE)." }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$timestampArgs = if ($Timestamp) { @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256') } else { @() }
foreach ($file in 'HapticLinkPad.dll', 'HapticLinkPad.cat') {
    & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword @timestampArgs (Join-Path $package $file)
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed ($LASTEXITCODE)." }
}
Write-Host "Driver package ready: $package"
