[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'PostgresCommandExecuter.csproj'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (Get-Command msbuild.exe -ErrorAction SilentlyContinue) {
    $msbuild = (Get-Command msbuild.exe).Source
}
elseif (Test-Path -LiteralPath $vswhere) {
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
}

if (-not $msbuild -or -not (Test-Path -LiteralPath $msbuild)) {
    throw 'MSBuild não encontrado. Instale o Visual Studio Build Tools com o workload Desenvolvimento para desktop com .NET.'
}

Write-Host "MSBuild: $msbuild"
& $msbuild $projectFile -restore -property:Configuration=$Configuration -property:RestoreConfigFile=$nugetConfig
if ($LASTEXITCODE -ne 0) {
    throw "A compilação terminou com código $LASTEXITCODE."
}

$executable = Join-Path $projectRoot "bin\$Configuration\net48\PostgresCommandExecuter.exe"
Write-Host "Compilado: $executable" -ForegroundColor Green
