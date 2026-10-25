param([ValidateSet('win-x64')][string]$Runtime = 'win-x64', [switch]$NoAot)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'Build-Native.ps1')
    dotnet restore src/UnityHps.Cli --configfile NuGet.Config -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build src/UnityHps.Cli -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & ./src/UnityHps.Cli/bin/Release/net10.0/UnityHps.exe self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed.' }
    $publish = Join-Path $repo "artifacts/publish/$Runtime"
    $aot = if ($NoAot) { 'false' } else { 'true' }
    $single = if ($NoAot) { 'true' } else { 'false' }
    dotnet publish src/UnityHps.Cli -c Release -r $Runtime --self-contained true --configfile NuGet.Config -p:NuGetAudit=false "-p:PublishAot=$aot" "-p:PublishSingleFile=$single" -p:IncludeNativeLibrariesForSelfExtract=true -p:StripSymbols=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. NativeAOT needs the Visual Studio C++ desktop build tools; alternatively use -NoAot.' }
    $bundle = Join-Path $repo "artifacts/release/Unity-HPS-$Runtime"
    New-Item -ItemType Directory -Force $bundle,(Join-Path $bundle 'managed'),(Join-Path $bundle 'unity/Editor'),(Join-Path $bundle 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'UnityHps.exe') -Destination $bundle -Force
    Copy-Item -LiteralPath (Join-Path $repo 'src/UnityHps/bin/Release/net10.0/UnityHps.Core.dll') -Destination (Join-Path $bundle 'managed') -Force
    Copy-Item -LiteralPath (Join-Path $publish 'UnityHps.Fbx.dll') -Destination $bundle -Force
    Copy-Item -LiteralPath (Join-Path $publish 'UnityHps.Fbx.dll') -Destination (Join-Path $bundle 'managed') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'third_party/ufbx/LICENSE') -Destination (Join-Path $bundle 'ufbx-LICENSE.txt') -Force
    foreach ($name in @('README.md', 'README.zh-CN.md', 'LICENSE', 'NOTICE')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $bundle -Force
    }
    # Remove documentation retained by an older local release build.
    Get-ChildItem -LiteralPath (Join-Path $bundle 'docs') -File -Filter '*.md' |
        Remove-Item -Force
    foreach ($name in @('OFFLINE.md', 'PROTOCOL.md')) {
        Copy-Item -LiteralPath (Join-Path $repo "docs/$name") -Destination (Join-Path $bundle 'docs') -Force
    }
    Get-ChildItem -LiteralPath (Join-Path $repo 'unity/Editor') -Filter '*.cs' | Copy-Item -Destination (Join-Path $bundle 'unity/Editor') -Force
    $manifest = @{}
    foreach ($relative in @('UnityHps.exe','UnityHps.Fbx.dll','managed/UnityHps.Core.dll','managed/UnityHps.Fbx.dll')) {
        $manifest[$relative] = (Get-FileHash -LiteralPath (Join-Path $bundle $relative) -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'build-manifest.json') -Encoding utf8
    Compress-Archive -LiteralPath $bundle -DestinationPath (Join-Path $repo "artifacts/Unity-HPS-$Runtime.zip") -Force
    Write-Host "Standalone executable: $publish/UnityHps.exe"
} finally { Pop-Location }
