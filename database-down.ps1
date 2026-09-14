[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    docker compose down
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose terminou com código $LASTEXITCODE." }
    Write-Host 'Ambiente Docker parado. Os dados fake foram preservados no volume local.' -ForegroundColor Yellow
    Write-Host 'Para subir e validar novamente: .\database-up.ps1'
} finally {
    Pop-Location
}
