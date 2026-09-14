[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'dotnet SDK não encontrado. Instale o .NET SDK compatível antes de executar os testes.' }

& $dotnet run --project (Join-Path $projectRoot 'tests\PostgresCommandExecuter.ColorizationTests\PostgresCommandExecuter.ColorizationTests.csproj') --configuration Debug
if ($LASTEXITCODE -ne 0) { throw "Os testes de contrato da colorização falharam." }
