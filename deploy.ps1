<#
.SYNOPSIS
    Build, testa e publica a imagem da API no Docker Hub.

.DESCRIPTION
    Um comando em vez de uma sequência decorada. O script:

      1. lê PROJECT_NAME / DOCKER_HUB_PROFILE / APP_VER do .env do ambiente;
      2. roda `dotnet clean && build && test` ANTES de empacotar;
      3. builda a imagem e a publica com a tag do APP_VER.

    O passo (2) não é opcional e não é cerimônia: publicar uma imagem que não
    passa nos testes é publicar um deploy que vai falhar no servidor, onde é caro
    descobrir. Use -SkipTests só quando souber exatamente por quê.

    A imagem é UMA só: o mesmo binário roda a API e as migrations (quem decide é
    a env var MIGRATE_ONLY). Não há tag `-migration`.

.PARAMETER Environment
    Qual pasta de docker/<env>/apps/<app> usar. Padrão: prod.

.PARAMETER SkipTests
    Pula a suíte. Os testes sobem Postgres e Redis via Testcontainers e levam
    ~1min30 — mas são eles que garantem que a imagem publicada presta.

.PARAMETER Push
    Publica no Docker Hub. Sem isto, a imagem fica só na máquina local.

.EXAMPLE
    ./deploy.ps1                    # build + testes + imagem local
    ./deploy.ps1 -Push              # ... e publica no Docker Hub
    ./deploy.ps1 -Push -SkipTests   # publica sem testar (você por sua conta)
#>
[CmdletBinding()]
param(
    [string] $Environment = 'prod',
    [switch] $SkipTests,
    [switch] $Push
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Die($msg)        { Write-Host "`nFALHOU: $msg" -ForegroundColor Red; exit 1 }

# --- 1. Descobrir a app e ler o .env ----------------------------------------
$appsDir = Join-Path $root "docker/$Environment/apps"
if (-not (Test-Path $appsDir)) { Die "Não existe $appsDir." }

$appDirs = @(Get-ChildItem $appsDir -Directory)
if ($appDirs.Count -ne 1) {
    Die "Esperava exatamente 1 app em $appsDir, achei $($appDirs.Count). Ajuste o script se você mantém mais de uma."
}
$appDir = $appDirs[0].FullName

$envFile = Join-Path $appDir '.env'
if (-not (Test-Path $envFile)) {
    Die "Não achei o .env em $appDir.`n       Copie o .env.example para .env e preencha."
}

# Lê o .env em um hashtable (ignora comentários e linhas vazias).
$cfg = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
    $k, $v = $line -split '=', 2
    $cfg[$k.Trim()] = $v.Trim()
}

foreach ($key in 'PROJECT_NAME', 'DOCKER_HUB_PROFILE', 'APP_VER') {
    if (-not $cfg[$key]) { Die "$key não está definido no .env." }
}

$image = "$($cfg['DOCKER_HUB_PROFILE'])/$($cfg['PROJECT_NAME']):$($cfg['APP_VER'])"
Write-Step "Publicando $image"

# Guarda contra o deploy mais fácil de errar: republicar por cima de uma tag que
# já está no ar. O servidor faz `pull` e recebe um binário DIFERENTE com o mesmo
# número de versão — e aí nada bate com nada.
if ($Push) {
    $exists = docker manifest inspect $image 2>$null
    if ($LASTEXITCODE -eq 0) {
        Die "A tag $($cfg['APP_VER']) JÁ EXISTE no Docker Hub.`n       Suba o APP_VER no .env. Republicar por cima faz o servidor rodar um binário diferente com o mesmo número."
    }
}

# --- 2. Testes ---------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step 'Testes (Postgres e Redis reais, via Testcontainers)'

    # O `clean` não é decoração: um build incremental ESCONDE falhas de estado
    # global — foi assim que uma corrida entre containers de teste ficou latente
    # por uma fase inteira.
    dotnet clean --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Die 'dotnet clean falhou.' }

    dotnet build --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Die 'O build falhou. (Warnings são erros aqui, de propósito.)' }

    dotnet test --no-build --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Die 'A suíte falhou. Descubra o porquê ANTES de publicar.' }

    Write-Ok 'Suíte verde.'
} else {
    Write-Host "`n  AVISO: testes PULADOS (-SkipTests)." -ForegroundColor Yellow
}

# --- 3. Imagem ---------------------------------------------------------------
Write-Step 'Build da imagem'
docker build -t $image $root
if ($LASTEXITCODE -ne 0) { Die 'docker build falhou.' }
Write-Ok $image

# --- 4. Push -----------------------------------------------------------------
if ($Push) {
    Write-Step 'Push para o Docker Hub'
    docker push $image
    if ($LASTEXITCODE -ne 0) { Die 'docker push falhou. Você fez `docker login`?' }
    Write-Ok 'Publicada.'

    Write-Host @"

No servidor, para subir esta versão:

    cd docker/$Environment/apps/$(Split-Path $appDir -Leaf)
    # ajuste APP_VER=$($cfg['APP_VER']) no .env de lá
    docker compose -f docker-compose.app.yml pull
    docker compose -f docker-compose.app.yml up -d

As migrations rodam sozinhas, antes da API. Se elas falharem, a API NÃO sobe.
"@ -ForegroundColor Gray
} else {
    Write-Host "`nImagem local criada. Use -Push para publicar." -ForegroundColor Gray
}
