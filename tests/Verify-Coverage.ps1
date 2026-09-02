param(
    [Parameter(Mandatory = $false)]
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot 'coverage-results'),

    [Parameter(Mandatory = $false)]
    [string] $ModuleName = 'VintageRTX',

    [Parameter(Mandatory = $false)]
    [double] $RequiredPercent = 100.0
)

$ErrorActionPreference = 'Stop'

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter 'coverage.opencover.xml')
if ($reports.Count -eq 0) {
    throw "No coverage.opencover.xml report was found below '$ResultsDirectory'."
}

$sequencePoints = @{}
$branchPoints = @{}
$methods = @{}

foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($module in @($document.CoverageSession.Modules.Module)) {
        $resolvedModuleName = [string] $module.ModuleName
        if ($resolvedModuleName -ne $ModuleName) {
            continue
        }

        $filesById = @{}
        foreach ($file in @($module.Files.File)) {
            $filesById[[string] $file.uid] = [string] $file.fullPath
        }

        foreach ($class in @($module.Classes.Class)) {
            foreach ($method in @($class.Methods.Method)) {
                $methodName = [string] $method.Name
                $methodFileRef = [string] $method.FileRef.uid
                $methodFile = if ($filesById.ContainsKey($methodFileRef)) {
                    $filesById[$methodFileRef]
                } else {
                    '<generated>'
                }
                $methodKey = "$methodFile|$methodName"
                if (-not $methods.ContainsKey($methodKey)) {
                    $methods[$methodKey] = $false
                }

                foreach ($point in @($method.SequencePoints.SequencePoint)) {
                    if ($null -eq $point) {
                        continue
                    }
                    $fileId = [string] $point.fileid
                    $filePath = if ($filesById.ContainsKey($fileId)) {
                        $filesById[$fileId]
                    } else {
                        $methodFile
                    }
                    $key = '{0}|{1}|{2}|{3}|{4}|{5}' -f `
                        $filePath, $methodName, $point.sl, $point.sc, $point.el, $point.ec
                    $visited = [long] $point.vc -gt 0
                    if (-not $sequencePoints.ContainsKey($key)) {
                        $sequencePoints[$key] = $visited
                    } elseif ($visited) {
                        $sequencePoints[$key] = $true
                    }
                    if ($visited) {
                        $methods[$methodKey] = $true
                    }
                }

                foreach ($point in @($method.BranchPoints.BranchPoint)) {
                    if ($null -eq $point) {
                        continue
                    }
                    $fileId = [string] $point.fileid
                    $filePath = if ($filesById.ContainsKey($fileId)) {
                        $filesById[$fileId]
                    } else {
                        $methodFile
                    }
                    $key = '{0}|{1}|{2}|{3}|{4}' -f `
                        $filePath, $methodName, $point.offset, $point.path, $point.sl
                    $visited = [long] $point.vc -gt 0
                    if (-not $branchPoints.ContainsKey($key)) {
                        $branchPoints[$key] = $visited
                    } elseif ($visited) {
                        $branchPoints[$key] = $true
                    }
                }
            }
        }
    }
}

if ($sequencePoints.Count -eq 0) {
    throw "Module '$ModuleName' has no sequence points in the supplied reports."
}

function Get-Percent([int] $Covered, [int] $Total) {
    if ($Total -eq 0) {
        return 100.0
    }
    return 100.0 * $Covered / $Total
}

$coveredLines = @($sequencePoints.Values | Where-Object { $_ }).Count
$coveredBranches = @($branchPoints.Values | Where-Object { $_ }).Count
$coveredMethods = @($methods.Values | Where-Object { $_ }).Count
$linePercent = Get-Percent $coveredLines $sequencePoints.Count
$branchPercent = Get-Percent $coveredBranches $branchPoints.Count
$methodPercent = Get-Percent $coveredMethods $methods.Count

Write-Host ('Coverage {0}: lines {1}/{2} ({3:N2}%), branches {4}/{5} ({6:N2}%), methods {7}/{8} ({9:N2}%).' -f `
    $ModuleName,
    $coveredLines,
    $sequencePoints.Count,
    $linePercent,
    $coveredBranches,
    $branchPoints.Count,
    $branchPercent,
    $coveredMethods,
    $methods.Count,
    $methodPercent)

$missesByFile = @{}
foreach ($entry in $sequencePoints.GetEnumerator()) {
    if ($entry.Value) {
        continue
    }
    $parts = $entry.Key.Split('|')
    $path = $parts[0]
    if (-not $missesByFile.ContainsKey($path)) {
        $missesByFile[$path] = 0
    }
    $missesByFile[$path]++
}

if ($missesByFile.Count -gt 0) {
    Write-Host 'Largest uncovered files:'
    $missesByFile.GetEnumerator() |
        Sort-Object Value -Descending |
        Select-Object -First 20 |
        ForEach-Object { Write-Host ('  {0,5}  {1}' -f $_.Value, $_.Key) }
}

$uncoveredBranches = @($branchPoints.GetEnumerator() | Where-Object { -not $_.Value })
if ($uncoveredBranches.Count -gt 0) {
    Write-Host 'Uncovered branches:'
    $uncoveredBranches |
        Sort-Object Key |
        Select-Object -First 50 |
        ForEach-Object { Write-Host ('  {0}' -f $_.Key) }
}

$failed = $linePercent -lt $RequiredPercent `
    -or $branchPercent -lt $RequiredPercent `
    -or $methodPercent -lt $RequiredPercent
if ($failed) {
    throw ('Coverage gate failed: required {0:N2}% for lines, branches and methods.' -f $RequiredPercent)
}

Write-Host ('PASS coverage gate: {0:N2}% lines, branches and methods.' -f $RequiredPercent)
