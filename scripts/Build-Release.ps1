param(
    [string]$OutputDirectory = 'artifacts/win-x64',
    [switch]$WithoutUnityHps
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'VmdConverterCore.csproj'
$publish = Join-Path $repo 'bin/Release/net10.0/win-x64/publish'
$destination = [System.IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$packageSource = Join-Path $repo 'unity-package/com.verafirefly.vmdcc'
$packageDestination = Join-Path $destination 'com.verafirefly.vmdcc'
$binaryNames = @('VmdConverterCore.exe', 'VmdConverterCore.Shared.dll', 'VmdConverterCore.RootMotion.dll', 'VmdConverterCore.Muscles.dll')

if (Test-Path -LiteralPath $destination) {
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -gt 0) {
        throw "Release directory is not empty: $destination. Select a new -OutputDirectory."
    }
}

Push-Location $repo
try {
    if (-not $WithoutUnityHps) {
        & (Join-Path $repo 'third_party/Unity-HPS/scripts/Build-Native.ps1')
        & dotnet build src/Modules/Hps/VmdConverterCore.Hps.csproj -c Release --warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'HPS adapter build failed.' }
        $binaryNames += @('VmdConverterCore.Hps.dll', 'UnityHps.Core.dll', 'UnityHps.Fbx.dll')
    }
    & dotnet restore $project -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    & dotnet build $project -c Release -r win-x64 --no-restore --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    if (-not $WithoutUnityHps) {
        foreach ($name in @('VmdConverterCore.Hps.dll','UnityHps.Core.dll','UnityHps.Fbx.dll')) {
            Copy-Item -LiteralPath (Join-Path $repo "src/Modules/Hps/bin/Release/net10.0/$name") -Destination (Join-Path $publish $name) -Force
        }
    }
    foreach ($name in $binaryNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name))) {
            throw "Missing published binary: $name"
        }
    }
    foreach ($relative in @('Editor/VmdTargetRigExporter.cs', 'Editor/VmdConverterCoreBridge.cs', 'Editor/VmdExternalPoseWorker.cs', 'Editor.Nlu/VmdNluBatchPipeline.cs', 'Runtime/VmdTargetCurveData.cs', 'package.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageSource $relative))) {
            throw "Missing Unity integration source: $relative"
        }
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath $packageSource -Destination $packageDestination -Recurse
    if (-not $WithoutUnityHps) {
        Copy-Item -LiteralPath (Join-Path $repo 'third_party/Unity-HPS/third_party/ufbx/LICENSE') -Destination (Join-Path $destination 'ufbx-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $repo 'third_party/Unity-HPS/third_party/ufbx/LICENSE') -Destination (Join-Path $packageDestination 'Tools/ufbx-LICENSE.txt')
        foreach ($name in @('LICENSE', 'NOTICE')) {
            $source = Join-Path $repo "third_party/Unity-HPS/$name"
            Copy-Item -LiteralPath $source -Destination (Join-Path $destination "Unity-HPS-$name.txt")
            Copy-Item -LiteralPath $source -Destination (Join-Path $packageDestination "Tools/Unity-HPS-$name.txt") -Force
        }
    }
    Copy-Item -LiteralPath (Join-Path $repo 'examples/unity') -Destination (Join-Path $destination 'examples-unity') -Recurse
    foreach ($name in @('README.md', 'README.zh-CN.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination (Join-Path $destination 'docs') -Recurse
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $destination 'examples-unity') -Filter '*.md') {
        $text = [IO.File]::ReadAllText($file.FullName).Replace('../../docs/','../docs/').Replace('../../unity-package/com.verafirefly.vmdcc/','../com.verafirefly.vmdcc/')
        [IO.File]::WriteAllText($file.FullName,$text)
    }
    foreach ($name in @('README.md', 'README.zh-CN.md')) {
        $readme = Join-Path $destination $name
        [IO.File]::WriteAllText($readme,[IO.File]::ReadAllText($readme).Replace('examples/unity','examples-unity').Replace('unity-package/com.verafirefly.vmdcc','com.verafirefly.vmdcc'))
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $destination 'docs') -Filter '*.md') {
        [IO.File]::WriteAllText($file.FullName,[IO.File]::ReadAllText($file.FullName).Replace('../examples/unity/','../examples-unity/'))
    }
    foreach ($name in $binaryNames) {
        Copy-Item -LiteralPath (Join-Path $publish $name) -Destination (Join-Path $destination $name)
        Copy-Item -LiteralPath (Join-Path $publish $name) -Destination (Join-Path $packageDestination 'Tools')
        $meta = Join-Path $packageDestination ('Tools/' + $name + '.meta')
        if (-not (Test-Path -LiteralPath $meta) -or
            -not (Select-String -LiteralPath $meta -Pattern '^guid:\s*[0-9a-fA-F]{32}$' -Quiet)) {
            throw "Missing or invalid Unity metadata for $name"
        }
    }

    foreach ($relative in @('README.md', 'README.zh-CN.md', 'LICENSE', 'com.verafirefly.vmdcc/README.md', 'com.verafirefly.vmdcc/README.zh-CN.md', 'com.verafirefly.vmdcc/Editor/VmdConverterCoreBridge.cs', 'com.verafirefly.vmdcc/Editor/VmdTargetRigExporter.cs', 'com.verafirefly.vmdcc/Editor.Nlu/VmdNluBatchPipeline.cs', 'com.verafirefly.vmdcc/Runtime/VmdTargetCurveData.cs', 'com.verafirefly.vmdcc/Tools.meta', 'com.verafirefly.vmdcc/Tools/VmdConverterCore.exe', 'examples-unity/VmdccTargetFkExample.cs', 'examples-unity/README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $relative))) {
            throw "Incomplete release: $relative"
        }
    }
    & (Join-Path $destination 'VmdConverterCore.exe') --help
    if ($LASTEXITCODE -ne 0) { throw 'CLI smoke test failed.' }
    Write-Host "Release ready: $destination"
}
finally {
    Pop-Location
}
