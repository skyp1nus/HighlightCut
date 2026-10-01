<#
.SYNOPSIS
    Measures the memory of this version of HighlightCut and of another one (master by default) with the same benchmark
    and the same videos. See README.md.

.EXAMPLE
    ./scripts/memory/compare.ps1 -Minutes 30 -Models $env:HIGHLIGHTCUT_MODELS_DIR
#>
param(
    # The version to compare with (a branch, tag or commit); empty measures only this one.
    [string]$Compare = 'origin/master',
    # Length of the long video.
    [double]$Minutes = 90,
    # A models folder with Parakeet installed; without one there is no transcription step.
    [string]$Models = $env:HIGHLIGHTCUT_MODELS_DIR,
    [string]$Out = 'artifacts/memory',
    [switch]$NoScenes
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$out = (New-Item -ItemType Directory -Force -Path ([IO.Path]::Combine($root, $Out))).FullName
$media = (New-Item -ItemType Directory -Force -Path (Join-Path $out 'media')).FullName
$common = @('--video', (Join-Path $media "long-$Minutes.mp4"), '--second', (Join-Path $media 'second.mp4'), '--minutes', "$Minutes")
if ($Models) { $common += @('--models', $Models) }
if ($NoScenes) { $common += '--no-scenes' }

function Measure-Version([string]$tree, [string]$name) {
    dotnet run --project (Join-Path $tree 'scripts/memory') -c Release -- @common --out (Join-Path $out "$name.md")
    if ($LASTEXITCODE) { throw "The benchmark failed on $name." }
}

if ($Compare) {
    # The other version in a worktree of its own, measured by this benchmark, with the native tools this one has.
    $base = Join-Path ([IO.Path]::GetTempPath()) 'highlightcut-memory-base'
    if (Test-Path $base) { git -C $root worktree remove --force $base }
    git -C $root worktree add --detach $base $Compare
    if ($LASTEXITCODE) { throw "Could not check out $Compare." }
    try {
        $bench = New-Item -ItemType Directory -Force -Path (Join-Path $base 'scripts/memory')
        Get-ChildItem -Path (Join-Path $root 'scripts/memory') -File | Copy-Item -Destination $bench
        if (Test-Path (Join-Path $root 'deps')) {
            $deps = New-Item -ItemType Directory -Force -Path (Join-Path $base 'deps')
            Get-ChildItem -Path (Join-Path $root 'deps') -Directory | Where-Object Name -ne '.cache' |
                Copy-Item -Destination $deps -Recurse -Force
        }
        Measure-Version $base 'before'
    }
    finally {
        git -C $root worktree remove --force $base
    }
}
Measure-Version $root 'after'
