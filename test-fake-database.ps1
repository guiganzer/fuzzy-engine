[CmdletBinding()]
param(
    [switch]$SkipDockerStart
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-ContainerPsql {
    param(
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U executor_dev -d $Database @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "psql falhou no banco '$Database' (código $LASTEXITCODE)."
    }
}

Push-Location $projectRoot
try {
    if (-not $SkipDockerStart) {
        # Docker Compose aguarda o healthcheck do PostgreSQL 18 antes de continuar.
        docker compose up --detach --wait
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose terminou com código $LASTEXITCODE." }
    }

    # Reaplica os arquivos de demonstração de modo idempotente. Isso também
    # atualiza ambientes que já tinham o volume criado antes destes arquivos.
    Invoke-ContainerPsql -Database 'postgres_executor_dev' -Arguments @('-f', '/docker-entrypoint-initdb.d/01-create-test-database.sql')
    Invoke-ContainerPsql -Database 'postgres_executor_dev' -Arguments @('-f', '/docker-entrypoint-initdb.d/02-create-fake-types-table.sql')
    Invoke-ContainerPsql -Database 'postgres_executor_dev' -Arguments @('-f', '/docker-entrypoint-initdb.d/03-seed-fake-types-data.sql')

    $devReady = (& docker compose exec -T postgres psql -tA -v ON_ERROR_STOP=1 -U executor_dev -d postgres_executor_dev -c 'SELECT 1') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or $devReady.Trim() -ne '1') { throw 'O banco de desenvolvimento não respondeu ao teste de disponibilidade.' }

    $rowCount = (& docker compose exec -T postgres psql -tA -v ON_ERROR_STOP=1 -U executor_dev -d postgres_executor_test -c 'SELECT count(*) FROM public.postgresql_type_showcase') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or [int]$rowCount.Trim() -lt 3) { throw 'A tabela fake não possui a carga de demonstração esperada.' }

    $columnCount = (& docker compose exec -T postgres psql -tA -v ON_ERROR_STOP=1 -U executor_dev -d postgres_executor_test -c "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'postgresql_type_showcase'") | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or [int]$columnCount.Trim() -lt 50) { throw 'A tabela fake não possui a cobertura de tipos esperada.' }

    $jsonLength = (& docker compose exec -T postgres psql -tA -v ON_ERROR_STOP=1 -U executor_dev -d postgres_executor_test -c 'SELECT max(octet_length(large_payload_jsonb::text)) FROM public.postgresql_type_showcase') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or [int]$jsonLength.Trim() -lt 600) { throw 'O JSON longo de demonstração não foi carregado.' }

    Write-Host ''
    Write-Host 'Banco fake pronto e validado.' -ForegroundColor Green
    Write-Host 'Preencha o painel com:' -ForegroundColor Cyan
    Write-Host '  Servidor: 127.0.0.1'
    Write-Host '  Porta:    55432'
    Write-Host '  Banco:    postgres_executor_test'
    Write-Host '  Usuário:  executor_dev'
    Write-Host '  Senha:    local_dev_password'
    Write-Host ''
    Write-Host "A tabela public.postgresql_type_showcase contém $($rowCount.Trim()) registros e $($columnCount.Trim()) colunas." -ForegroundColor Green
    Write-Host "Maior JSONB: $($jsonLength.Trim()) bytes." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Copie e execute esta consulta no painel:' -ForegroundColor Cyan
    $showQuery = $false
    foreach ($line in Get-Content (Join-Path $projectRoot 'docker\demo\select-showcase.sql')) {
        if ($line.TrimStart().StartsWith('SELECT', [System.StringComparison]::OrdinalIgnoreCase)) { $showQuery = $true }
        if ($showQuery) { Write-Host $line }
        if ($showQuery -and $line.TrimEnd().EndsWith(';')) { break }
    }
    Write-Host 'Para parar o ambiente depois do teste: .\database-down.ps1' -ForegroundColor Yellow
}
finally {
    Pop-Location
}
