[CmdletBinding()]
param(
    [switch]$Confirm
)

if (-not $Confirm) {
    throw 'Esta operação apaga os bancos locais. Execute novamente com: .\database-reset.ps1 -Confirm'
}

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    docker compose down --volumes --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível remover o ambiente (código $LASTEXITCODE)." }

    docker compose up --detach --wait
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível recriar o ambiente (código $LASTEXITCODE)." }
    Write-Host 'Bancos locais recriados.' -ForegroundColor Green
} finally {
    Pop-Location
}
