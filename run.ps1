[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$executable = Join-Path $projectRoot "bin\$Configuration\net48\PostgresCommandExecuter.exe"

if ($Build -or -not (Test-Path -LiteralPath $executable)) {
    & (Join-Path $projectRoot 'build.ps1') -Configuration $Configuration
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Executável não encontrado: $executable"
}

& $executable
