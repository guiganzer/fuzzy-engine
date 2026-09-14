[CmdletBinding()]
param(
    [switch]$SkipDockerStart
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if (-not $SkipDockerStart) {
    & (Join-Path $projectRoot 'test-fake-database.ps1')
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'dotnet SDK não encontrado. Instale o .NET SDK compatível antes de executar os testes.' }

& $dotnet run --project (Join-Path $projectRoot 'tests\PostgresCommandExecuter.ColorizationTests\PostgresCommandExecuter.ColorizationTests.csproj') --configuration Debug -- --integration
if ($LASTEXITCODE -ne 0) { throw "Os testes de integração de colorização falharam (código $LASTEXITCODE)." }
