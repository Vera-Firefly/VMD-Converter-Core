param([string]$OutputDirectory = 'artifacts/win-x64')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count -gt 0) { throw "Release directory must be empty: $destination" }
$publish = Join-Path $repo 'bin/Release/net10.0/win-x64/publish'
$binaries = @('VmdConverterCore.exe','VmdConverterCore.Shared.dll','VmdConverterCore.Hps.dll','UnityHps.Core.dll','UnityHps.Fbx.dll')
Push-Location $repo
try {
    & dotnet run --project tests/CurveReduction/CurveReduction.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Quaternion curve checks failed.' }
    & (Join-Path $repo 'third_party/Unity-HPS/scripts/Build-Native.ps1')
    & dotnet build src/Modules/Hps/VmdConverterCore.Hps.csproj -c Release --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Mandatory HPS adapter build failed.' }
    & dotnet publish VmdConverterCore.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    foreach ($name in @('VmdConverterCore.Hps.dll','UnityHps.Core.dll','UnityHps.Fbx.dll')) {
        Copy-Item -LiteralPath (Join-Path $repo "src/Modules/Hps/bin/Release/net10.0/$name") -Destination (Join-Path $publish $name) -Force
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'unity-package/com.verafirefly.vmdcc') -Destination (Join-Path $destination 'com.verafirefly.vmdcc') -Recurse
    $tools = Join-Path $destination 'com.verafirefly.vmdcc/Tools'
    foreach ($name in $binaries) {
        $source = Join-Path $publish $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing binary: $name" }
        Copy-Item -LiteralPath $source -Destination $destination -Force
        Copy-Item -LiteralPath $source -Destination $tools -Force
        if (-not (Test-Path -LiteralPath (Join-Path $tools "$name.meta"))) { throw "Missing plugin metadata: $name" }
    }
    foreach ($name in @('README.md','README.zh-CN.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $destination }
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $destination -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'examples/unity') -Destination (Join-Path $destination 'examples-unity') -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '*.md' -Recurse) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($file.DirectoryName -eq $destination) { $text = $text.Replace('examples/unity/','examples-unity/').Replace('unity-package/com.verafirefly.vmdcc','com.verafirefly.vmdcc') }
        elseif ($file.DirectoryName -eq (Join-Path $destination 'examples-unity')) { $text = $text.Replace('../../unity-package/','../').Replace('../../README','../README') }
        elseif ($file.DirectoryName -eq (Join-Path $destination 'com.verafirefly.vmdcc')) { $text = $text.Replace('../../README','../README').Replace('../../examples/unity/','../examples-unity/') }
        [IO.File]::WriteAllText($file.FullName,$text)
    }
    foreach ($name in @('LICENSE','NOTICE')) {
        Copy-Item -LiteralPath (Join-Path $repo "third_party/Unity-HPS/$name") -Destination (Join-Path $destination "Unity-HPS-$name.txt")
        Copy-Item -LiteralPath (Join-Path $repo "third_party/Unity-HPS/$name") -Destination (Join-Path $tools "Unity-HPS-$name.txt") -Force
    }
    Copy-Item -LiteralPath (Join-Path $repo 'third_party/Unity-HPS/third_party/ufbx/LICENSE') -Destination (Join-Path $destination 'ufbx-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $destination 'ufbx-LICENSE.txt') -Destination $tools -Force
    $manifest = @{}
    foreach ($name in $binaries) { $manifest[$name] = (Get-FileHash -LiteralPath (Join-Path $destination $name) -Algorithm SHA256).Hash }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'build-manifest.json') -Encoding utf8
    & (Join-Path $destination 'VmdConverterCore.exe') --help
    if ($LASTEXITCODE -ne 0) { throw 'CLI help check failed.' }
    Write-Host "Release ready: $destination"
}
finally { Pop-Location }
