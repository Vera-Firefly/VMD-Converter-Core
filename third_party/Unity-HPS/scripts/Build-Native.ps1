param([string]$OutputDirectory = 'native/win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ x64 tools are required to build the FBX reader.' }
Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
New-Item -ItemType Directory -Force $output | Out-Null
Push-Location $output
try {
    & cl.exe /nologo /O2 /LD /MT /std:c11 /D_CRT_SECURE_NO_WARNINGS (Join-Path $repo 'native/hps_fbx.c') (Join-Path $repo 'third_party/ufbx/ufbx.c') /link /OUT:UnityHps.Fbx.dll
    if ($LASTEXITCODE -ne 0) { throw 'Native FBX reader build failed.' }
} finally { Pop-Location }
