<#
.SYNOPSIS
    Do código ao ar, num comando: testa, builda, publica e sobe no servidor.

.DESCRIPTION
    Substitui a sequência decorada (buildar, empurrar, entrar por SSH, editar o .env,
    `pull`, `up -d`) por uma linha:

        ./deploy.ps1 v1.2.0

    O que ele faz, em ordem — e para no primeiro erro:

      1. grava a versão no APP_VER do .env (a versão tem UMA fonte: o argumento);
      2. `dotnet clean && build && test` — a suíte inteira, com Postgres e Redis reais;
      3. `docker build` e `docker push`;
      4. envia o compose, o .env e a service account para o servidor (SFTP, com
         verificação de hash em cada arquivo);
      5. `docker compose up -d --force-recreate` no servidor.

    Publicar uma imagem que não passa nos testes é publicar um deploy que vai falhar no
    servidor, onde é caro descobrir. Por isso o passo 2 não é opcional (o -SkipTests
    existe, mas ele avisa).

.PARAMETER Version
    A versão desta release (ex.: v1.2.0). OBRIGATÓRIA.

    Ela é a ÚNICA fonte: o script a grava no APP_VER do .env, e esse mesmo .env é enviado
    ao servidor. Você não a edita à mão em lugar nenhum.

    Republicar uma tag que já existe é PERMITIDO (só avisa). O `--force-recreate` no
    servidor garante que o container é recriado mesmo com o nome da imagem igual.

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
    .\deploy.ps1 v1.2.0             # testa, builda, publica e sobe no servidor
    .\deploy.ps1 v1.2.0 -NoDeploy   # ... e para no Docker Hub
    .\deploy.ps1 -Local             # só a imagem local, para conferir

.NOTES
    ⚠️ ESTE ARQUIVO PRECISA SER SALVO EM UTF-8 **COM BOM**.

    O PowerShell 5.1 (o que vem no Windows) assume ANSI num .ps1 sem BOM: os acentos viram
    bytes lixo, e o parser morre com um erro que não tem NADA a ver com a causa —
    "Missing closing '}'", apontando para uma linha aleatória. O PowerShell 7 lê UTF-8 por
    padrão, então o problema é invisível para quem só testa nele.

    Há um teste que trava isso: DeployScriptTests.
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
#
# A versão tem UMA fonte: este argumento. O APP_VER do .env é DERIVADO dele — o script o
# grava, e o mesmo .env é enviado ao servidor.
#
# Antes, a versão era digitada em dois lugares (aqui e no .env), e "dois lugares" sempre
# vira "um deles desatualizado": o servidor puxaria uma tag que já tem em cache e subiria o
# binário ANTIGO, sem erro nenhum.
if (-not $Version) {
    Die @"
Falta a versão.

    .\deploy.ps1 v1.2.0

(Ela é gravada no .env e vira a tag da imagem — você não a edita à mão em lugar nenhum.)
"@
}

if ($Version -notmatch '^v?\d+\.\d+\.\d+') {
    Die "Versão '$Version' não parece uma versão (esperado: v1.2.3)."
}

(Get-Content $envFile) -replace '^APP_VER=.*', "APP_VER=$Version" | Set-Content $envFile -Encoding UTF8
$cfg['APP_VER'] = $Version

Write-Ok "APP_VER=$Version"

foreach ($key in 'PROJECT_NAME', 'DOCKER_HUB_PROFILE') {
    if (-not $cfg[$key]) { Die "$key não está definido no .env." }
}

$image = "$($cfg['DOCKER_HUB_PROFILE'])/$($cfg['PROJECT_NAME']):$Version"

Write-Step "Deploy de $image"

# Republicar uma tag que já existe é PERMITIDO — é rotina durante o desenvolvimento de uma
# release. Só avisa, para você não fazer isso sem perceber: quem já tiver puxado essa tag
# está com um binário diferente do que está sendo publicado agora.
if (-not $Local) {
    docker manifest inspect $image 2>$null | Out-Null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  A tag $Version já existe no Docker Hub — vai ser SOBRESCRITA." -ForegroundColor Yellow
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
    #
    # ⚠️ O NOME é RENOMEADO no envio, e isso não é frescura.
    #
    # O arquivo que o GCP te dá tem um nome gerado (garius-tcm-7d83fa2633d3.json), mas o
    # compose monta um caminho FIXO (./secrets/gcp-service-account.json). Sem a renomeação,
    # os dois nunca batem — e o compose morre no servidor com "no such file", depois de já
    # ter buildado, publicado e enviado tudo.
    #
    # Renomear aqui é melhor do que exigir que VOCÊ renomeie: é a próxima coisa a esquecer,
    # e o erro só aparece no fim do deploy.
    $secretsDir = Join-Path $appDir 'secrets'
    $serviceAccount = Get-ChildItem $secretsDir -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $serviceAccount) {
        Die @"
Não achei a service account (nenhum .json) em:
    $secretsDir

É a credencial que destrava o Secret Manager — sem ela a aplicação não sobe.
Ponha o JSON que o GCP te deu nessa pasta. O nome não importa: o deploy o renomeia.
"@
    }

    Invoke-Remote "mkdir -p '$remoteFolder/secrets'"

    # O destino leva o nome que o COMPOSE espera, seja qual for o nome de origem.
    Set-SFTPItem `
        -SessionId $sftp.SessionId `
        -Path $serviceAccount.FullName `
        -Destination "$remoteFolder/secrets" `
        -Force

    if ($serviceAccount.Name -ne 'gcp-service-account.json') {
        Invoke-Remote "mv '$remoteFolder/secrets/$($serviceAccount.Name)' '$remoteFolder/secrets/gcp-service-account.json'"
    }

    # A PASTA é 700 — é ela que protege. O ARQUIVO é 644, e isso não é um relaxamento:
    #
    # O container roda como NÃO-ROOT (`app`, uid 1654) e o Docker monta o secret como um bind
    # mount comum — preservando o dono do host (o usuário do deploy). Com 600, o processo fica
    # trancado do lado de fora da própria credencial:
    #
    #     Access to the path '/run/secrets/gcp_service_account' is denied.
    #
    # (E `uid:` no compose NÃO resolve: só funciona no Docker Swarm; num compose normal o
    # Docker o ignora, e ainda avisa que está ignorando.)
    #
    # O 644 no arquivo não expõe nada A MAIS no host: para chegar até ele é preciso ENTRAR na
    # pasta, e ela é 700 — só o dono passa. Quem quer ler o arquivo já teria de ser o dono, ou
    # root (que lê tudo de qualquer jeito).
    Invoke-Remote "chmod 700 '$remoteFolder/secrets' && chmod 644 '$remoteFolder/secrets/gcp-service-account.json'"

    Write-Ok "service account enviada ($($serviceAccount.Name) -> gcp-service-account.json, 600)"

    # Sobe. O `pull` traz a imagem nova; o `up -d` recria o que mudou.
    #
    # As migrations rodam primeiro, sozinhas, e a API só sobe se elas passarem
    # (depends_on: service_completed_successfully). Se elas falharem, o deploy para aqui —
    # que é exatamente o que se quer.
    Write-Step 'Subindo (migrations, depois a API)'

    # --force-recreate é OBRIGATÓRIO aqui, e não é excesso de zelo.
    #
    # Republicar uma tag é permitido (é rotina durante uma release). Mas aí o NOME da imagem
    # não muda — e o `up -d` puro compara nomes: ele conclui que "nada mudou" e deixa o
    # container ANTIGO no ar. Você faria o deploy, veria "up-to-date", e continuaria rodando
    # o binário velho, sem um único erro em lugar nenhum.
    $composeUp = "cd '$remoteFolder' && " +
                 "docker compose -f docker-compose.app.yml pull && " +
                 "docker compose -f docker-compose.app.yml up -d --force-recreate"

    $up = Invoke-SSHCommand -SessionId $ssh.SessionId -Command $composeUp -TimeOut 600

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
