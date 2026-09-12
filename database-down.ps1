[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    docker compose down
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose terminou com código $LASTEXITCODE." }
} finally {
    Pop-Location
}
