<#
.SYNOPSIS
    Do código ao ar, num comando: testa, builda, publica e sobe no servidor.

.DESCRIPTION
    Substitui a sequência decorada (buildar, empurrar, entrar por SSH, editar o .env,
    `pull`, `up -d`) por uma linha:

        ./deploy.ps1 v1.2.0

    O que ele faz, em ordem — e para no primeiro erro:

      1. sobe o APP_VER no .env (local);
      2. `dotnet clean && build && test` — a suíte inteira, com Postgres e Redis reais;
      3. `docker build` e `docker push`;
      4. envia o compose, o .env e a service account para o servidor (SFTP, com
         verificação de hash em cada arquivo);
      5. `docker compose up -d` no servidor.

    Publicar uma imagem que não passa nos testes é publicar um deploy que vai falhar no
    servidor, onde é caro descobrir. Por isso o passo 2 não é opcional (o -SkipTests
    existe, mas ele avisa).

.PARAMETER Version
    A versão desta release (ex.: v1.2.0). Grava no .env e vira a tag da imagem.
    Omita para reusar o APP_VER que já está lá.

.PARAMETER Environment
    Qual pasta de docker/<env>/apps/<app> usar. Padrão: prod.

.PARAMETER SkipTests
    Pula a suíte. Ela leva ~1min30 (sobe Postgres e Redis via Testcontainers) — e é ela
    que garante que a imagem publicada presta.

.PARAMETER NoDeploy
    Publica no Docker Hub e PARA. Não toca no servidor.

.PARAMETER Local
    Só builda a imagem, aqui. Não publica, não faz deploy.

.EXAMPLE
    ./deploy.ps1 v1.2.0             # testa, builda, publica e sobe no servidor
    ./deploy.ps1 v1.2.0 -NoDeploy   # ... e para no Docker Hub
    ./deploy.ps1 -Local             # só a imagem local, para conferir
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Version,

    [string] $Environment = 'prod',
    [switch] $SkipTests,
    [switch] $NoDeploy,
    [switch] $Local
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Die($msg)        { Write-Host "`nFALHOU: $msg" -ForegroundColor Red; exit 1 }

# --- 1. Descobrir a app e ler as configurações -------------------------------
$appsDir = Join-Path $root "docker/$Environment/apps"
if (-not (Test-Path $appsDir)) { Die "Não existe $appsDir." }

$appDirs = @(Get-ChildItem $appsDir -Directory)
if ($appDirs.Count -ne 1) {
    Die "Esperava exatamente 1 app em $appsDir, achei $($appDirs.Count)."
}
$appDir = $appDirs[0].FullName

$envFile = Join-Path $appDir '.env'
if (-not (Test-Path $envFile)) {
    Die "Não achei o .env em $appDir.`n       Copie o .env.example para .env e preencha."
}

function Read-KeyValueFile($path) {
    $result = @{}

    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*#' -or $line -notmatch '=') { continue }

        $key, $value = $line -split '=', 2
        $result[$key.Trim()] = $value.Trim()
    }

    return $result
}

$cfg = Read-KeyValueFile $envFile

# --- 2. A versão --------------------------------------------------------------
# Subir o APP_VER é o passo mais fácil de esquecer, e o esquecimento é silencioso: o
# servidor faz `pull` de uma tag que já tem em cache e sobe o binário ANTIGO, sem erro
# nenhum. Passar a versão como argumento faz o script gravá-la — e o .env local e o do
# servidor nunca mais divergem (é o MESMO arquivo: ele é enviado junto).
if ($Version) {
    if ($Version -notmatch '^v?\d+\.\d+\.\d+') {
        Die "Versão '$Version' não parece uma versão (esperado: v1.2.3)."
    }

    (Get-Content $envFile) -replace '^APP_VER=.*', "APP_VER=$Version" | Set-Content $envFile -Encoding UTF8
    $cfg['APP_VER'] = $Version

    Write-Ok "APP_VER=$Version gravado no .env"
}

foreach ($key in 'PROJECT_NAME', 'DOCKER_HUB_PROFILE', 'APP_VER') {
    if (-not $cfg[$key]) { Die "$key não está definido no .env." }
}

$image = "$($cfg['DOCKER_HUB_PROFILE'])/$($cfg['PROJECT_NAME']):$($cfg['APP_VER'])"

Write-Step "Deploy de $image"

# Guarda contra o erro mais caro: republicar uma tag que já está no ar. O servidor faria
# `pull` e receberia um binário DIFERENTE com o mesmo número de versão — e a partir daí
# nada mais bate com nada quando você for investigar um bug.
if (-not $Local) {
    docker manifest inspect $image 2>$null | Out-Null

    if ($LASTEXITCODE -eq 0) {
        Die "A tag $($cfg['APP_VER']) JÁ EXISTE no Docker Hub.`n       Suba a versão: ./deploy.ps1 v<nova>"
    }
}

# --- 3. Testes ---------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step 'Testes (Postgres e Redis reais, via Testcontainers)'

    # O `clean` não é cerimônia: um build incremental ESCONDE falhas de estado global —
    # foi assim que uma corrida entre containers de teste ficou latente por uma fase inteira.
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

# --- 4. Imagem ---------------------------------------------------------------
Write-Step 'Build da imagem'

docker build -t $image $root
if ($LASTEXITCODE -ne 0) { Die 'docker build falhou.' }

Write-Ok $image

if ($Local) {
    Write-Host "`nImagem local criada. Sem push, sem deploy (-Local)." -ForegroundColor Gray
    exit 0
}

# --- 5. Push -----------------------------------------------------------------
Write-Step 'Push para o Docker Hub'

docker push $image
if ($LASTEXITCODE -ne 0) { Die 'docker push falhou. Você fez `docker login`?' }

Write-Ok 'Publicada.'

if ($NoDeploy) {
    Write-Host "`nPublicada. Sem deploy (-NoDeploy)." -ForegroundColor Gray
    exit 0
}

# --- 6. Deploy no servidor ----------------------------------------------------
#
# Tudo vem do MESMO .env — não há um segundo arquivo de configuração para manter.
if (-not $cfg['DEPLOY_HOST']) {
    Write-Host @"

Imagem publicada, mas NÃO houve deploy: falta o DEPLOY_HOST no .env.

Acrescente as três linhas (veja o .env.example):

    DEPLOY_PATH=/opt/garius/customers/tcm/sfchortolandia
    DEPLOY_HOST=1.2.3.4
    DEPLOY_USER=root

Ou suba à mão, no servidor:
    docker compose -f docker-compose.app.yml pull
    docker compose -f docker-compose.app.yml up -d
"@ -ForegroundColor Yellow

    exit 0
}

foreach ($key in 'DEPLOY_PATH', 'DEPLOY_USER') {
    if (-not $cfg[$key]) { Die "$key não está definido no .env (o DEPLOY_HOST está)." }
}

$remoteFolder = $cfg['DEPLOY_PATH'].TrimEnd('/')

# A SENHA não está no .env, de propósito: esse arquivo é ENVIADO para o próprio servidor
# (o compose o lê lá). Uma senha de SSH viajando junto com o que ela protege — e ficando
# em disco, em claro, num servidor de produção — é uma péssima ideia.
#
# Ela é pedida UMA VEZ e guardada cifrada pela DPAPI do Windows, atrelada ao seu usuário:
# nem outro usuário da mesma máquina consegue decifrá-la.
$vault = Join-Path $env:USERPROFILE '.garius'
$passwordFile = Join-Path $vault "$($cfg['DEPLOY_USER'])@$($cfg['DEPLOY_HOST']).cred"

if (Test-Path $passwordFile) {
    $securePassword = Get-Content $passwordFile | ConvertTo-SecureString
} else {
    Write-Host ''
    $securePassword = Read-Host "  Senha de $($cfg['DEPLOY_USER'])@$($cfg['DEPLOY_HOST'])" -AsSecureString

    New-Item -ItemType Directory -Path $vault -Force | Out-Null
    $securePassword | ConvertFrom-SecureString | Set-Content $passwordFile

    Write-Ok "Senha guardada (cifrada) em $passwordFile — não vou pedir de novo."
}

Write-Step "Deploy em $($cfg['DEPLOY_HOST']):$remoteFolder"

# O `plink`/`pscp` não são garantidos no Windows; o OpenSSH é (vem com o sistema desde o
# Windows 10). Mas ele não aceita senha por argumento — de propósito. Então usamos o
# módulo Posh-SSH, que fala SSH e SFTP com senha, e é instalável sem admin.
if (-not (Get-Module -ListAvailable -Name Posh-SSH)) {
    Write-Host '  Instalando o módulo Posh-SSH (uma vez só)...' -ForegroundColor Gray

    Install-Module Posh-SSH -Scope CurrentUser -Force -AllowClobber
}

Import-Module Posh-SSH

$credential = New-Object System.Management.Automation.PSCredential($cfg['DEPLOY_USER'], $securePassword)

$ssh = New-SSHSession -ComputerName $cfg['DEPLOY_HOST'] -Credential $credential -AcceptKey -ErrorAction Stop
$sftp = New-SFTPSession -ComputerName $cfg['DEPLOY_HOST'] -Credential $credential -AcceptKey -ErrorAction Stop

try {
    function Invoke-Remote($command) {
        $result = Invoke-SSHCommand -SessionId $ssh.SessionId -Command $command

        if ($result.ExitStatus -ne 0) {
            Die "Falhou no servidor: $command`n       $($result.Error)"
        }

        return $result.Output
    }

    # Limpa o que vai ser reescrito. O `secrets/` some junto: um arquivo órfão ali (uma
    # service account de outra app, uma chave antiga) é exatamente o tipo de coisa que
    # ninguém percebe e que continua sendo montada no container.
    Invoke-Remote "mkdir -p '$remoteFolder' && rm -f '$remoteFolder/docker-compose.app.yml' '$remoteFolder/.env' && rm -rf '$remoteFolder/secrets'"

    # Envia o compose e o .env — o MESMO .env daqui, com a versão que acabamos de publicar.
    # É isto que garante que o servidor nunca fica numa versão diferente da que você buildou.
    foreach ($file in 'docker-compose.app.yml', '.env') {
        $localFile = Join-Path $appDir $file

        Set-SFTPItem -SessionId $sftp.SessionId -Path $localFile -Destination $remoteFolder -Force

        # Verifica o hash. Um upload truncado produz um compose que "quase" funciona — e um
        # erro de YAML às 2h da manhã não é como você quer descobrir isso.
        $localHash = (Get-FileHash $localFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $remoteHash = (Invoke-Remote "sha256sum '$remoteFolder/$file' | cut -d' ' -f1").Trim()

        if ($localHash -ne $remoteHash) {
            Die "O arquivo $file chegou corrompido ao servidor (hash não bate)."
        }

        Write-Ok "$file enviado"
    }

    # A service account: a credencial que destrava o Secret Manager.
    $secretsDir = Join-Path $appDir 'secrets'
    $serviceAccount = Get-ChildItem $secretsDir -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $serviceAccount) {
        Die "Não achei a service account em $secretsDir.`n       Sem ela a aplicação não sobe (é o que destrava o Secret Manager)."
    }

    Invoke-Remote "mkdir -p '$remoteFolder/secrets'"
    Set-SFTPItem -SessionId $sftp.SessionId -Path $serviceAccount.FullName -Destination "$remoteFolder/secrets" -Force

    # 600, não 777: é uma CREDENCIAL. Com 777, qualquer usuário do servidor lê a chave que
    # abre todos os segredos da aplicação — senha do banco, chaves de criptografia, JWT.
    Invoke-Remote "chmod 700 '$remoteFolder/secrets' && chmod 600 '$remoteFolder/secrets/'*.json"

    Write-Ok 'service account enviada (600)'

    # Sobe. O `pull` traz a imagem nova; o `up -d` recria o que mudou.
    #
    # As migrations rodam primeiro, sozinhas, e a API só sobe se elas passarem
    # (depends_on: service_completed_successfully). Se elas falharem, o deploy para aqui —
    # que é exatamente o que se quer.
    Write-Step 'Subindo (migrations, depois a API)'

    $up = Invoke-SSHCommand -SessionId $ssh.SessionId -Command "cd '$remoteFolder' && docker compose -f docker-compose.app.yml pull && docker compose -f docker-compose.app.yml up -d" -TimeOut 600

    Write-Host $up.Output

    if ($up.ExitStatus -ne 0) {
        Die "O compose falhou no servidor:`n$($up.Error)"
    }

    Write-Ok 'No ar.'

    Write-Host "`n  $image" -ForegroundColor Green
    Write-Host "  https://$($cfg['APP_HOST'])" -ForegroundColor Green
}
finally {
    if ($ssh)  { Remove-SSHSession  -SessionId $ssh.SessionId  | Out-Null }
    if ($sftp) { Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null }
}
