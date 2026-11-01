<#
.SYNOPSIS
    Builds the HapticLink installer (HapticLink-win-Setup.exe) and its update packages.

.DESCRIPTION
    - Builds the service folder (drivers\build-service.ps1: service, helper, signed driver).
    - Publishes the tray app (single exe + "service" folder) with the given version.
    - Packs it with Velopack (vpk, pinned in .config/dotnet-tools.json) into artifacts\installer:
        HapticLink-win-Setup.exe     the installer (per user, no admin rights; installs the .NET 8 Desktop Runtime
                                     if it's missing). The app's first start runs the one-time setup (one admin prompt).
        *.nupkg, releases.win.json   update packages (installed copies check GitHub Releases for these)
    - With -Publish, uploads everything as a GitHub release.
    - The release notes are this version's section of CHANGELOG.md ("## x.y.z (date)" up to the next "## ");
      -Publish refuses to run without one.
    Releases sign the driver with the certificate in GitHub secrets (PAD_CERT_PFX + PAD_CERT_PASSWORD, set by the
    release workflow); local builds use a development certificate.

.PARAMETER Install
    Also install the built version over this PC's installed copy right away (owner's rule for releases).

.EXAMPLE
    ./build-installer.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [switch]$Publish,
    [switch]$Install
)
$appName = 'HapticLink'
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$repoUrl = 'https://github.com/hemanthvj12/haptic-link'
$publishDir = Join-Path $repo 'artifacts\installer-publish'
$outputDir = Join-Path $repo 'artifacts\installer'
$notesFile = Join-Path $repo 'artifacts\release-notes.md'

function Get-ReleaseNotes([string]$version) {
    $lines = Get-Content (Join-Path $repo 'CHANGELOG.md') -Encoding UTF8
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^## $([regex]::Escape($version))(\s|$)") { $start = $i + 1; break }
    }
    if ($start -lt 0) { return $null }
    $end = $start
    while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## ') { $end++ }
    if ($end -le $start) { return $null }
    $notes = ($lines[$start..($end - 1)] -join "`n").Trim()
    if ($notes) { $notes } else { $null }
}

Push-Location $repo
try {
    $notes = Get-ReleaseNotes $Version
    if (-not $notes) {
        if ($Publish) { throw "CHANGELOG.md has no section for $Version (a heading like '## $Version (date)'). Add one before releasing." }
        Write-Warning "CHANGELOG.md has no section for $Version; the installer is built without release notes."
    }
    New-Item -ItemType Directory -Force (Split-Path $notesFile) | Out-Null
    if ($notes) { [IO.File]::WriteAllText($notesFile, $notes + "`n", [Text.UTF8Encoding]::new($false)) }

    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

    foreach ($dir in $publishDir, $outputDir) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }

    Write-Host 'Building the service folder (driver, helper, service)...'
    # The release certificate: from GitHub's secrets in the release workflow, else the owner's copy on this PC (made by
    # drivers\make-release-cert.ps1; its password is DPAPI-encrypted for this Windows user), so local release installs
    # sign the driver exactly like GitHub does.
    # D:\Vibe Projects\Signing\HapticLink: the owner's folder of signing certificates, next to the repository, outside it.
    $signing = Join-Path (Split-Path $repo -Parent) 'Signing\HapticLink'
    $localPfx = Join-Path $signing 'HapticLinkPad-release.pfx'
    $localPassword = Join-Path $signing 'HapticLinkPad-release.password'
    $serviceArgs = @{}
    if ($env:PAD_CERT_PFX) { $serviceArgs = @{ PfxPath = $env:PAD_CERT_PFX; PfxPassword = $env:PAD_CERT_PASSWORD; Timestamp = $true } }
    elseif ((Test-Path $localPfx) -and (Test-Path $localPassword)) {
        $password = [Net.NetworkCredential]::new('', ((Get-Content $localPassword -Raw).Trim() | ConvertTo-SecureString)).Password
        $serviceArgs = @{ PfxPath = $localPfx; PfxPassword = $password; Timestamp = $true }
        Write-Host "Signing with the release certificate in $signing."
    }
    elseif ($Publish -or $Install) {
        throw "Releases must sign the driver with the release certificate: set PAD_CERT_PFX and PAD_CERT_PASSWORD, or keep it in $signing (drivers\make-release-cert.ps1)."
    }
    & (Join-Path $repo 'drivers\build-service.ps1') @serviceArgs

    Write-Host "Publishing version $Version..."
    dotnet publish 'src\HapticLink.App\HapticLink.App.csproj' -c Release -o $publishDir -p:Version=$Version --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    Get-ChildItem $publishDir -File | Where-Object Name -ne 'HapticLink.exe' | Remove-Item -Force
    if (-not (Test-Path (Join-Path $publishDir 'service\HapticLink.Service.exe'))) { throw 'No service folder was published.' }

    if ($Publish) {
        $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { (& 'C:\Program Files\GitHub CLI\gh.exe' auth token).Trim() }
        if (-not $token) { throw 'No GitHub token: set GITHUB_TOKEN or log in with gh.' }
        dotnet vpk download github --repoUrl $repoUrl --token $token -o $outputDir
    }

    Write-Host 'Packing installer...'
    $notesArgs = if ($notes) { @('--releaseNotes', $notesFile) } else { @() }
    dotnet vpk pack @notesArgs `
        --packId HapticLink `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe HapticLink.exe `
        --packTitle 'HapticLink for DualSense' `
        --packAuthors 'hemanthvj12' `
        --icon 'src\HapticLink.App\Assets\app.ico' `
        --runtime win-x64 `
        --framework net8-x64-desktop `
        --shortcuts StartMenuRoot `
        --noPortable `
        --outputDir $outputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

    if ($Publish) {
        dotnet vpk upload github --repoUrl $repoUrl --token $token -o $outputDir --publish `
            --releaseName "HapticLink $Version" --tag "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
        Write-Host "Published release v$Version."
    }

    Write-Host "Installer: $(Join-Path $outputDir 'HapticLink-win-Setup.exe')"
    if ($Install) {
        # Owner, 2026-11-02: install each release here at once instead of waiting for GitHub to publish it and the
        # installed copy to update itself. Velopack's own updater applies the package and restarts the app; it's started
        # through Explorer so it runs outside the Claude app's sandbox (which would redirect its writes to AppData).
        $updateExe = Join-Path $env:LOCALAPPDATA "$appName\Update.exe"
        if (-not (Test-Path $updateExe)) { throw "$appName isn't installed here (no $updateExe); run its Setup first." }
        $nupkg = Join-Path $outputDir "$appName-$Version-full.nupkg"
        $cmd = Join-Path $repo 'artifacts\apply-update.cmd'
        $log = Join-Path $repo 'artifacts\apply-update.log'
        Remove-Item $log -ErrorAction SilentlyContinue
        @(
            '@echo off'
            "taskkill /IM $appName.exe /F >nul 2>&1"
            'timeout /t 2 /nobreak >nul'
            "`"%LOCALAPPDATA%\$appName\Update.exe`" apply --silent --package `"$nupkg`" > `"$log`" 2>&1"
            "echo exit %errorlevel% >> `"$log`""
        ) | Set-Content -Path $cmd -Encoding ascii
        Write-Host "Installing $Version over the installed copy..."
        Start-Process explorer.exe -ArgumentList "`"$cmd`""
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline -and -not ((Test-Path $log) -and (Select-String -Path $log -Pattern '^exit ' -Quiet))) { Start-Sleep 1 }
        if (-not (Test-Path $log)) { throw "The updater didn't start (no $log)." }
        if (-not (Select-String -Path $log -Pattern "Package version $([regex]::Escape($Version)) applied successfully" -Quiet)) {
            Get-Content $log -Tail 15 | Write-Host
            throw "Installing $Version failed; see $log."
        }
        Write-Host "Installed $Version; $appName restarted."
    }
}
finally { Pop-Location }
