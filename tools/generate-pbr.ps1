[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$InputPath,

    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\generated\pbr'),

    [ValidateSet('auto', 'generic', 'stone', 'brick', 'wood', 'metal', 'anvil', 'polished', 'polished-metal', 'cloth', 'glass')]
    [string]$Profile = 'auto',

    [string]$AssetsRoot,

    [switch]$FlipGreen,

    [switch]$SkipExisting
)

$projectPath = Join-Path $PSScriptRoot 'PbrTextureGenerator\PbrTextureGenerator.csproj'
$arguments = @('run', '--project', $projectPath, '--configuration', 'Release', '--no-restore', '--', 'generate')

foreach ($path in $InputPath) {
    $arguments += @('--input', $path)
}

$arguments += @('--output', $OutputRoot, '--profile', $Profile)
if ($AssetsRoot) {
    $arguments += @('--assets-root', $AssetsRoot)
}

if ($FlipGreen) {
    $arguments += '--flip-green'
}

if ($SkipExisting) {
    $arguments += '--skip-existing'
}

& dotnet @arguments
exit $LASTEXITCODE
