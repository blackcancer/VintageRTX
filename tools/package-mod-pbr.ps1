[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ModRoot,

    [Parameter(Mandatory = $true)]
    [string]$PackId,

    [string]$PackVersion = '1.0.0',

    [string]$PackName,

    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\generated\pbr-packs'),

    [ValidateSet('block', 'all')]
    [string]$Scope = 'block',

    [ValidateSet('auto', 'generic', 'stone', 'brick', 'wood', 'metal', 'anvil', 'polished', 'polished-metal', 'cloth', 'glass')]
    [string]$Profile = 'auto',

    [switch]$FlipGreen
)

$projectPath = Join-Path $PSScriptRoot 'PbrTextureGenerator\PbrTextureGenerator.csproj'
$arguments = @(
    'run', '--project', $projectPath, '--configuration', 'Release', '--no-restore', '--',
    'pack', '--mod-root', $ModRoot, '--output', $OutputRoot, '--pack-id', $PackId,
    '--pack-version', $PackVersion, '--scope', $Scope, '--profile', $Profile
)

if ($PackName) {
    $arguments += @('--pack-name', $PackName)
}

if ($FlipGreen) {
    $arguments += '--flip-green'
}

& dotnet @arguments
exit $LASTEXITCODE
