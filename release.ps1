[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'PostgresCommandExecuter.csproj'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$releasesRoot = Join-Path $artifactsRoot 'releases'
$workRoot = Join-Path $artifactsRoot '.work'

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
if (-not $Version) {
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Versão inválida: '$Version'. Use o formato 1.2.3 ou 1.2.3-beta.1."
}

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) { throw 'dotnet.exe não encontrado. Instale o .NET SDK para compilar.' }
    $dotnet = $dotnetCommand.Source
}

New-Item -ItemType Directory -Force -Path $releasesRoot, $workRoot | Out-Null
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $workRoot $runId
$buildRoot = Join-Path $runRoot 'build'
$packageRoot = Join-Path $runRoot "PostgresCommandExecuter-v$Version"
$zipName = "PostgresCommandExecuter-v$Version-win-net48.zip"
$zipPath = Join-Path $releasesRoot $zipName
$hashPath = "$zipPath.sha256"

New-Item -ItemType Directory -Force -Path $buildRoot, $packageRoot | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

try {
    if (-not $SkipRestore) {
        & $dotnet restore $projectFile --configfile $nugetConfig
        if ($LASTEXITCODE -ne 0) { throw "Restore falhou com código $LASTEXITCODE." }
    }

    & $dotnet build $projectFile --configuration Release --no-restore --output $buildRoot `
        -property:Version=$Version -property:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "Build falhou com código $LASTEXITCODE." }

    Get-ChildItem -LiteralPath $buildRoot -File |
        Where-Object { $_.Extension -notin '.pdb', '.xml' } |
        Copy-Item -Destination $packageRoot

    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md')
    @(
        "PostgreSQL Command Executer"
        "Versão: $Version"
        "Plataforma: Windows / .NET Framework 4.8"
        "Gerado em UTC: $([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    ) | Set-Content -LiteralPath (Join-Path $packageRoot 'VERSION.txt') -Encoding UTF8

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath $hashPath) {
        Remove-Item -LiteralPath $hashPath -Force
    }

    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $zipName" | Set-Content -LiteralPath $hashPath -Encoding ASCII

    Write-Host ''
    Write-Host "Release v$Version criada com sucesso." -ForegroundColor Green
    Write-Host "ZIP:    $zipPath"
    Write-Host "SHA256: $hash"
}
finally {
    $resolvedWork = [System.IO.Path]::GetFullPath($workRoot).TrimEnd('\') + '\'
    $resolvedRun = [System.IO.Path]::GetFullPath($runRoot)
    if ($resolvedRun.StartsWith($resolvedWork, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedRun)) {
        Remove-Item -LiteralPath $resolvedRun -Recurse -Force
    }
}
