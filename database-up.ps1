[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    docker compose up --detach --wait
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose terminou com código $LASTEXITCODE." }

    # Também atualiza o banco fake e mostra os dados exatos para o painel.
    & .\test-fake-database.ps1 -SkipDockerStart
    if ($LASTEXITCODE -ne 0) { throw "A preparação da base fake terminou com código $LASTEXITCODE." }
}
finally {
    Pop-Location
}
