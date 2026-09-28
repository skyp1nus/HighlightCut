#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the native binaries HighlightCut needs (ffmpeg, ffprobe, libmpv) into deps/<rid>/.

.DESCRIPTION
    Versions, download URLs and SHA-256 hashes are pinned in scripts/deps.json. Every download and
    every extracted file is verified before it is used. The build copies deps/<rid>/ next to the app.

    Downloads are cached in deps/.cache, so reruns work offline. A rerun skips components that are
    already installed and match the manifest, unless -Force is given.

    Works with Windows PowerShell 5.1 and PowerShell 7+.

.PARAMETER Rid
    Runtime identifier to fetch for. Only win-x64 is pinned for now.

.PARAMETER Component
    Components to fetch: ffmpeg, libmpv, vulkan. Default: all components for the RID.

.PARAMETER Force
    Reinstall even when the installed files already match the manifest.

.PARAMETER Check
    Only verify the installed files against the manifest. Downloads nothing; exits 1 on mismatch.

.PARAMETER Proxy
    Proxy URI for downloads. By default the system proxy settings are used.

.PARAMETER ProxyUseDefaultCredentials
    Authenticate to the proxy with the current user's credentials.

.EXAMPLE
    pwsh scripts/fetch-deps.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\fetch-deps.ps1 -Component ffmpeg -Force
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Rid = 'win-x64',
    [string[]]$Component,
    [string]$Destination,
    [string]$CacheDir,
    [switch]$Force,
    [switch]$Check,
    [uri]$Proxy,
    [switch]$ProxyUseDefaultCredentials
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# The progress bar makes Invoke-WebRequest very slow on Windows PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if (-not $Proxy -and $env:HTTPS_PROXY) { $Proxy = [uri]$env:HTTPS_PROXY }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'deps.json') | ConvertFrom-Json
if (-not $Destination) { $Destination = Join-Path (Join-Path $RepoRoot 'deps') $Rid }
if (-not $CacheDir) {
    if ($env:HIGHLIGHTCUT_DEPS_CACHE) { $CacheDir = $env:HIGHLIGHTCUT_DEPS_CACHE }
    else { $CacheDir = Join-Path (Join-Path $RepoRoot 'deps') '.cache' }
}
$ToolsDir = Join-Path (Join-Path $RepoRoot 'deps') '.tools'
$StampPath = Join-Path $Destination '.highlightcut-deps.json'
$UserAgent = 'HighlightCut-fetch-deps/1'   # SourceForge serves an HTML page to browser-like agents.

$RidEntry = $Manifest.rids.$Rid
if (-not $RidEntry) { throw "No pinned dependencies for RID '$Rid' in deps.json." }
$AllComponents = @($RidEntry.PSObject.Properties.Name)
if (-not $Component) { $Component = $AllComponents }
foreach ($c in $Component) {
    if ($AllComponents -notcontains $c) { throw "Unknown component '$c'. Known: $($AllComponents -join ', ')." }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-Sha256([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Sha256 $Path) -eq $Expected.ToLowerInvariant())
}

function Read-Stamp {
    if (Test-Path -LiteralPath $StampPath) {
        try { return Get-Content -Raw -LiteralPath $StampPath | ConvertFrom-Json } catch { }
    }
    return $null
}

function Write-Stamp($Stamp) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $Stamp | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StampPath -Encoding UTF8
}

function Test-Installed([string]$Name, $Entry) {
    foreach ($f in $Entry.files) {
        $path = Join-Path $Destination $f.to
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        if (($f.PSObject.Properties.Name -contains 'sha256') -and -not (Test-Sha256 $path $f.sha256)) { return $false }
    }
    $stamp = Read-Stamp
    if (-not $stamp -or -not ($stamp.PSObject.Properties.Name -contains $Name)) { return $false }
    return $stamp.$Name.archiveSha256 -eq $Entry.archive.sha256
}

function Invoke-Download([string[]]$Urls, [string]$Sha256, [string]$Target) {
    if (Test-Sha256 $Target $Sha256) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
    $partial = "$Target.partial"
    foreach ($url in $Urls) {
        if (-not $url.StartsWith('https://')) { throw "Refusing non-HTTPS URL: $url" }
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Write-Host "  downloading $url (attempt $attempt)"
                $request = @{ Uri = $url; OutFile = $partial; UserAgent = $UserAgent; UseBasicParsing = $true }
                if ($Proxy) { $request.Proxy = $Proxy }
                if ($ProxyUseDefaultCredentials) { $request.ProxyUseDefaultCredentials = $true }
                Invoke-WebRequest @request
                $actual = Get-Sha256 $partial
                if ($actual -ne $Sha256.ToLowerInvariant()) {
                    throw "SHA-256 mismatch for $url`n  expected $Sha256`n  actual   $actual"
                }
                Move-Item -Force -LiteralPath $partial -Destination $Target
                return
            }
            catch {
                Write-Warning "  $($_.Exception.Message)"
                Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $partial
                if ($attempt -lt 3) { Start-Sleep -Seconds ([math]::Pow(2, $attempt)) }
            }
        }
    }
    throw "Could not download $(Split-Path -Leaf $Target) from any mirror."
}

function Get-7Zip {
    # Prefer a 7-Zip that is already installed; otherwise bootstrap the pinned 7zr.exe on Windows.
    foreach ($name in '7z', '7za', '7zr') {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    foreach ($dir in $env:ProgramFiles, ${env:ProgramFiles(x86)}) {
        if ($dir) {
            $candidate = Join-Path $dir '7-Zip\7z.exe'
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    $isWindowsOs = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    if (-not $isWindowsOs) { throw 'Extracting .7z archives needs 7-Zip (7z) on PATH.' }
    $tool = $Manifest.tools.'7zr'
    $exe = Join-Path $ToolsDir '7zr.exe'
    Invoke-Download -Urls $tool.urls -Sha256 $tool.sha256 -Target $exe
    return $exe
}

function Expand-Entries($Entry, [string]$Archive, [string]$Staging) {
    New-Item -ItemType Directory -Force -Path $Staging | Out-Null
    $format = $Entry.archive.format
    if ($format -eq 'zip') {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            foreach ($f in $Entry.files) {
                $item = $zip.GetEntry($f.from)
                if (-not $item) { throw "Archive $($Entry.archive.name) has no entry '$($f.from)'." }
                $out = Join-Path $Staging $f.to
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($item, $out, $true)
            }
        }
        finally { $zip.Dispose() }
    }
    elseif ($format -eq '7z') {
        $sevenZip = Get-7Zip
        $raw = Join-Path $Staging '.raw'
        $names = @($Entry.files | ForEach-Object { $_.from })
        & $sevenZip x $Archive "-o$raw" -y -bso0 -bsp0 @names
        if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE on $Archive." }
        foreach ($f in $Entry.files) {
            $src = Join-Path $raw $f.from
            if (-not (Test-Path -LiteralPath $src)) { throw "Archive $($Entry.archive.name) has no entry '$($f.from)'." }
            $out = Join-Path $Staging $f.to
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
            Move-Item -Force -LiteralPath $src -Destination $out
        }
        Remove-Item -Recurse -Force -LiteralPath $raw
    }
    else { throw "Unsupported archive format '$format'." }

    foreach ($f in $Entry.files) {
        if (($f.PSObject.Properties.Name -contains 'sha256') -and -not (Test-Sha256 (Join-Path $Staging $f.to) $f.sha256)) {
            throw "Extracted file '$($f.to)' does not match its pinned SHA-256."
        }
    }
}

function Install-Component([string]$Name, $Entry) {
    $archive = Join-Path (Join-Path $CacheDir $Entry.archive.sha256) $Entry.archive.name
    Invoke-Download -Urls $Entry.archive.urls -Sha256 $Entry.archive.sha256 -Target $archive

    $staging = Join-Path (Join-Path (Join-Path $RepoRoot 'deps') '.staging') $Name
    if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
    try {
        Expand-Entries -Entry $Entry -Archive $archive -Staging $staging
        foreach ($f in $Entry.files) {
            $out = Join-Path $Destination $f.to
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
            Move-Item -Force -LiteralPath (Join-Path $staging $f.to) -Destination $out
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
    }

    $stamp = Read-Stamp
    if (-not $stamp) { $stamp = [pscustomobject]@{} }
    $record = [pscustomobject]@{ version = $Entry.version; archiveSha256 = $Entry.archive.sha256; license = $Entry.license }
    $stamp | Add-Member -Force -NotePropertyName $Name -NotePropertyValue $record
    Write-Stamp $stamp
}

$failed = @()
foreach ($name in $Component) {
    $entry = $RidEntry.$name
    Write-Host "$name - $($entry.version)"
    if ($Check) {
        if (Test-Installed $name $entry) { Write-Host '  ok' }
        else { Write-Host '  missing or out of date'; $failed += $name }
        continue
    }
    if (-not $Force -and (Test-Installed $name $entry)) { Write-Host '  already installed'; continue }
    try {
        Install-Component $name $entry
        Write-Host '  installed'
    }
    catch {
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
        $failed += $name
    }
}

if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Native dependencies for $Rid are in $Destination"
