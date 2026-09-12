[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    docker compose up --detach --wait
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose terminou com código $LASTEXITCODE." }

    Write-Host ''
    Write-Host 'PostgreSQL 18 disponível em 127.0.0.1:55432' -ForegroundColor Green
    Write-Host 'Desenvolvimento: postgres_executor_dev'
    Write-Host 'Testes:          postgres_executor_test'
    Write-Host 'Usuário:         executor_dev'
finally {
    Pop-Location
}
