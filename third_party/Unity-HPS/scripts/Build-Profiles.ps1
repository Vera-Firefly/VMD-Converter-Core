param([Parameter(Mandatory)][string]$Unity)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/UnityReferenceProject'
if (-not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt'))) {
    New-Item -ItemType Directory -Force (Join-Path $project 'Assets/Editor'),(Join-Path $project 'ProjectSettings'),(Join-Path $project 'Packages') | Out-Null
    [IO.File]::WriteAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'),"m_EditorVersion: 2022.3.22f1`n")
    [IO.File]::WriteAllText((Join-Path $project 'Packages/manifest.json'),'{"dependencies":{"com.unity.modules.animation":"1.0.0"}}')
}
Get-ChildItem -LiteralPath (Join-Path $repo 'unity/Editor') -Filter '*.cs' | Copy-Item -Destination (Join-Path $project 'Assets/Editor') -Force
Push-Location $repo
try {
    dotnet build src/UnityHps.Cli -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    $cli = Join-Path $repo 'src/UnityHps.Cli/bin/Release/net10.0/UnityHps.exe'
    foreach ($topology in @('53','55')) {
        foreach ($kind in @('standard','mass')) {
            $output = Join-Path $repo "artifacts/profile-$kind$topology"
            $log = "$output.log"
            $arguments = @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','UnityHps.Reference.UnityHpsCalibration.ExportSynthetic','-hpsOutput',('"'+$output+'"'),'-logFile',('"'+$log+'"'))
            if ($topology -eq '53') { $arguments += '-hpsTopology53' }
            if ($kind -eq 'mass') { $arguments += '-hpsMassProbe' }
            $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "Synthetic export failed: $log" }
            & $cli calibrate --rig "$output/rig.json" --input "$output/training.json" --output "$output/rig.calibrated.json"
            if ($LASTEXITCODE -ne 0) { throw 'Synthetic calibration failed.' }
        }
        & $cli profile --rig "artifacts/profile-standard$topology/rig.calibrated.json" --mass-rig "artifacts/profile-mass$topology/rig.calibrated.json" --output "profiles/standard$topology.json.gz"
        if ($LASTEXITCODE -ne 0) { throw 'Profile compression failed.' }
    }
} finally { Pop-Location }
