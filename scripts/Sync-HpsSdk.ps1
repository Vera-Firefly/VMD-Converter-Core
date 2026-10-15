param([string]$SourceDirectory = (Join-Path $PSScriptRoot '../../Unity-HPS'))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = Join-Path $repo 'third_party/Unity-HPS'
foreach ($relative in @('src/UnityHps','profiles','native/hps_fbx.c','third_party/ufbx','scripts/Build-Native.ps1','Directory.Build.props','NuGet.Config','LICENSE','NOTICE')) {
    $item = Join-Path $source $relative
    if (-not (Test-Path -LiteralPath $item)) { throw "Missing HPS SDK source: $item" }
}
foreach ($relative in @('src/UnityHps','profiles','native/hps_fbx.c','third_party/ufbx','scripts/Build-Native.ps1','Directory.Build.props','NuGet.Config','LICENSE','NOTICE')) {
    $item = Join-Path $source $relative
    $dest = Join-Path $destination $relative
    if (Test-Path -LiteralPath $item -PathType Container) {
        Get-ChildItem -LiteralPath $item -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
            $file = Join-Path $dest ([IO.Path]::GetRelativePath($item,$_.FullName))
            New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $file -Force
        }
    } else {
        New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
        Copy-Item -LiteralPath $item -Destination $dest -Force
    }
}
Write-Host "SDK snapshot updated: $destination. Review git diff before publishing."
