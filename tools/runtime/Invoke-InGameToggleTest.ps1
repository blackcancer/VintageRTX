#requires -Version 5.1
<#
.SYNOPSIS
Builds the rewrite, starts the actual installed Vintage Story client and waits for a world A/B/A run.
The user loads a TEST WORLD in the normal menu. No login data is copied and no world is edited by
this script or the test harness. The running game may of course save that test world normally.
A returned 0 means the narrow world-toggle smoke test passed, not complete RTX acceptance.
#>
[CmdletBinding()]
param(
    [string]$VintageStoryPath = $env:VINTAGE_STORY,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateRange(90,1800)][int]$TimeoutSeconds = 300,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($VintageStoryPath)) { throw 'Set VINTAGE_STORY or pass -VintageStoryPath with the installation root.' }
$installation = [IO.Path]::GetFullPath($VintageStoryPath)
if (!(Test-Path (Join-Path $installation 'VintagestoryAPI.dll'))) { $installation = Split-Path $installation -Parent }
$client = Join-Path $installation 'Vintagestory.exe'
if (!(Test-Path -LiteralPath $client)) { throw "Full installed Windows client missing: $client. Reference DLLs alone cannot execute an in-game test." }
$modPath = Join-Path $repo "src\VintageRTX.Client\bin\$Configuration\Mods"
$dll = Join-Path $modPath 'vintagertx\VintageRTX.dll'
if (!$NoBuild) {
    & dotnet build (Join-Path $repo 'src\VintageRTX.Client\VintageRTX.Client.csproj') -c $Configuration "-p:VintageStoryPath=$installation"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. The game was not started.' }
}
if (!(Test-Path -LiteralPath $dll)) { throw "Built mod missing: $dll" }
$expectedDll = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$run = Join-Path $repo ('tests\artifacts\runtime-game\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$oldAuto = $env:VINTAGERTX_RUNTIME_AUTOTEST
$oldOutput = $env:VINTAGERTX_RUNTIME_OUTPUT
try {
    $env:VINTAGERTX_RUNTIME_AUTOTEST = 'toggle'
    $env:VINTAGERTX_RUNTIME_OUTPUT = $run
    # Only our explicitly built mod search path is added; no clientsettings/auth files are read.
    $process = Start-Process -FilePath $client -WorkingDirectory $installation `
        -ArgumentList @('--tracelog','--addModPath', ('"' + $modPath + '"')) -PassThru
}
finally {
    $env:VINTAGERTX_RUNTIME_AUTOTEST = $oldAuto
    $env:VINTAGERTX_RUNTIME_OUTPUT = $oldOutput
}
Write-Host 'Load a TEST WORLD normally and face a stationary illuminated surface. Do not load a valuable world for a test campaign.'
Write-Host 'Do not install another copy of VintageRTX. This process uses the local build above.'
Write-Host "Artifacts: $run"
$watch = [Diagnostics.Stopwatch]::StartNew()
while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $reports = @(Get-ChildItem -LiteralPath $run -Filter 'runtime-result.json' -Recurse -File)
    foreach ($file in $reports) {
        try { $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
        catch { continue } # Atomic publication can race directory enumeration; bounded by timeout.
        if ($report.status -eq 'RUNNING') { continue }
        if ($report.schemaVersion -ne 1 -or $report.test -ne 'runtime-toggle-smoke') { throw 'Unexpected report contract.' }
        $inputsPath = Join-Path $file.DirectoryName 'runtime-inputs.json'
        $inputs = Get-Content -LiteralPath $inputsPath -Raw | ConvertFrom-Json
        if ($inputs.inputs.'VintageRTX.dll' -ne $expectedDll) { throw 'Report was produced by a different mod binary. Check duplicate mods.' }
        if ($report.status -ne 'PASS') {
            Write-Error -ErrorAction Continue "$($report.status): $($report.reason) Report: $($file.FullName)"
            exit 1
        }
        $required = @('native-before','enabled','coverage','native-after')
        if (@($report.samples).Count -ne 4) { throw 'Incomplete A/B/A capture set.' }
        foreach ($name in $required) {
            $sample = @($report.samples | Where-Object { $_.name -eq $name })
            if ($sample.Count -ne 1 -or $sample[0].screenshot -ne "$name.png") { throw "Missing/ambiguous capture: $name" }
            $capture = Join-Path $file.DirectoryName "$name.png"
            if (!(Test-Path -LiteralPath $capture) -or (Get-FileHash -LiteralPath $capture -Algorithm SHA256).Hash -ne $sample[0].sha256) {
                throw "Changed or missing screenshot: $name"
            }
        }
        Write-Host "PASS (world-toggle smoke only): $($file.FullName)"
        Write-Host 'The game remains open. Save/close the test world normally; no forced process kill is used.'
        exit 0
    }
    $process.Refresh()
    if ($process.HasExited) { throw "Client exited before a completed world test (OS code $($process.ExitCode)). Artifacts: $run" }
    Start-Sleep -Milliseconds 250
}
throw "NO_RUNTIME_RESULT within $TimeoutSeconds seconds. The game is not killed. Login, load a test world and inspect the normal game logs; this is not a render PASS. Artifacts: $run"
