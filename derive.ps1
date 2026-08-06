<#
.SYNOPSIS
    Deriva uma nova aplicação a partir deste template, num comando.

.DESCRIPTION
    Substitui a sequência manual (reinstalar o template, rodar o dotnet new, gerar as
    chaves à mão, conferir que a suíte nasce verde) por uma linha:

        ./derive.ps1 Tcm.SfcHortolandia.Api

    O que ele faz, em ordem — e para no primeiro erro:

      1. valida o nome (o dotnet new e o Postgres têm regras que, se violadas, só
         quebram lá na frente);
      2. reinstala o template a partir DESTA pasta (garante que você deriva da versão
         atual, não de uma instalada há três meses);
      3. dotnet new garius-api -n <Nome> -o ../<Nome>;
      4. gera as chaves de segurança com CSPRNG e as IMPRIME (nunca grava em disco);
      5. dotnet clean && build && test na app nova — confirma que ela nasce verde;
      6. imprime os próximos passos, com o nome do secret já derivado.

    O que ele NÃO faz, de propósito:

      - NÃO cria o secret no Google Secret Manager. A service account é read-only no
        Secret Manager; só você cria secrets. Automatizar isso daria uma falsa sensação
        de "pronto" — e a app não sobe sem o secret, então o erro apareceria no boot.
      - NÃO roda o bootstrap do banco (MIGRATE_ONLY). Ele depende do secret já criado.

    As duas coisas ficam nos "próximos passos" impressos no fim, com os valores prontos.

.PARAMETER Name
    O nome da aplicação — e o NAMESPACE RAIZ. Ex.: Tcm.SfcHortolandia.Api.

    É o mais caro de mudar depois (renomeia namespace, ApplicationName do banco, nome do
    secret no GCP, PROJECT_NAME dos containers e o UserSecretsId). Escolha com cuidado.

.PARAMETER SkipTests
    Pula o dotnet test na app nova (leva ~1min30, sobe Postgres e Redis via
    Testcontainers). Só para conferir a estrutura rápido — não pule na dúvida.

.PARAMETER Force
    Sobrescreve a pasta de destino se ela já existir. Sem isto, o script recusa —
    derivar por cima de um projeto que já tem trabalho é o tipo de perda que não se
    desfaz.

.EXAMPLE
    .\derive.ps1 Tcm.SfcHortolandia.Api
    .\derive.ps1 Amorena.Portal.Api -SkipTests

.NOTES
    ⚠️ ESTE ARQUIVO PRECISA SER SALVO EM UTF-8 **COM BOM** — mesma razão do deploy.ps1
    (o PowerShell 5.1 assume ANSI sem BOM e os acentos quebram o parser). Há um teste
    que trava isso: DeployScriptTests cobre o deploy.ps1; este segue a mesma regra.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string] $Name,

    [switch] $SkipTests,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Die($msg)        { Write-Host "`nFALHOU: $msg" -ForegroundColor Red; exit 1 }

# --- 1. Validar o nome -------------------------------------------------------
#
# O dotnet new aceita quase tudo, mas nem tudo GERA um projeto que compila e sobe. As
# regras abaixo são as que, se violadas, só quebrariam lá na frente — no build, ou pior,
# no Postgres (que rejeita nomes de role/database com certos caracteres).

Write-Step "Validando o nome '$Name'"

# Só letras, dígitos e pontos; cada segmento começa com letra. É o que produz um
# namespace C# válido E um slug/ApplicationName válidos no Postgres depois de
# normalizados. Um nome como "3M.Api" ou "Minha-App" passaria no dotnet new e explodiria
# adiante.
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$') {
    Die @"
Nome inválido: '$Name'.
       Use PascalCase com pontos, cada segmento começando por letra:
         Tcm.SfcHortolandia.Api   ✅
         Amorena.Portal.Api       ✅
         3M.Api                   ❌ (segmento começa com dígito)
         Minha-App.Api            ❌ (hífen)
"@
}

# O template ainda se chama Garius.* — derivar com o mesmo nome recriaria a colisão que o
# template inteiro existe para evitar.
if ($Name -match '^Garius(\.|$)') {
    Die "O nome não pode começar com 'Garius' — é o do próprio template, e colidiria no banco e no secret."
}

Write-Ok "Nome válido."

# --- 2. Resolver o destino ---------------------------------------------------
#
# Pasta irmã do template (../<Nome>), que é a convenção do README e a que o deploy.ps1
# assume.

$output = Join-Path (Split-Path $root -Parent) $Name

if (Test-Path $output) {
    if (-not $Force) {
        Die @"
A pasta de destino já existe:
         $output
       Use -Force para sobrescrever — mas confira antes que não há trabalho lá dentro.
"@
    }
    Write-Host "  -Force: a pasta $output será sobrescrita." -ForegroundColor Yellow
}

# --- 3. Reinstalar o template a partir DESTA pasta ---------------------------
#
# Sem isto, o dotnet new usaria a versão instalada — que pode ser de semanas atrás, sem
# as correções que você acabou de fazer. Derivar da versão ERRADA do template é um bug
# silencioso: a app nasce, funciona, e só difere do template no que foi corrigido.

Write-Step "Reinstalando o template (garante a versão atual)"

dotnet new install $root --force | Out-Null
if ($LASTEXITCODE -ne 0) { Die "Falha ao instalar o template a partir de $root." }

Write-Ok "Template instalado."

# --- 4. Derivar --------------------------------------------------------------

Write-Step "Gerando $Name em $output"

$forceArg = if ($Force) { '--force' } else { $null }

dotnet new garius-api -n $Name -o $output $forceArg
if ($LASTEXITCODE -ne 0) { Die "O dotnet new falhou." }

Write-Ok "Projeto gerado."

# --- 5. Gerar as chaves (e NUNCA gravá-las) ----------------------------------
#
# CSPRNG do .NET (RandomNumberGenerator) — não depende do openssl estar instalado no
# Windows. 32 bytes = 256 bits, em base64, que é o formato que o template espera.
#
# Impressas na tela, jamais em arquivo: uma chave que toca o disco pode vazar num commit,
# num backup, num histórico de shell. Você copia daqui direto para o Secret Manager.

function New-Key {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes)
}

$jwtKey        = New-Key
$encKey        = New-Key
$blindIndexKey = New-Key

# O nome do secret que o dotnet new derivou (mesma regra do template.json):
# nameLower, não-alfanumérico -> '-', + '-secrets'.
$secretName = ($Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-') + '-secrets'

# --- 6. Confirmar que a app nasce verde --------------------------------------

if (-not $SkipTests) {
    Write-Step "Conferindo que a app nasce verde (clean + build + test)"
    Write-Host "  (sobe Postgres e Redis via Testcontainers — ~1min30)" -ForegroundColor Gray

    Push-Location $output
    try {
        dotnet clean | Out-Null
        dotnet build
        if ($LASTEXITCODE -ne 0) { Die "A app derivada NÃO COMPILA. Isto é um bug do template — não continue." }

        dotnet test
        if ($LASTEXITCODE -ne 0) { Die "A suíte da app derivada FALHOU. Investigue antes de usar." }
    }
    finally {
        Pop-Location
    }

    Write-Ok "Build limpo, suíte verde."
}
else {
    Write-Host "`n  AVISO: testes PULADOS (-SkipTests). A app pode não estar verde." -ForegroundColor Yellow
}

# --- 7. Próximos passos ------------------------------------------------------

$projectPath = "src/$Name"

Write-Host @"

============================================================
  $Name derivada em:
  $output
============================================================

PRÓXIMOS PASSOS (os dois que este script NÃO faz por você):

1. CRIE o secret no Google Secret Manager com o nome:

     $secretName

   A service account é read-only — só você cria secrets. Conteúdo:

     Jwt:SigningKey                $jwtKey
     Encryption:Keys:1             $encKey
     Encryption:ActiveKeyVersion   1
     Encryption:BlindIndexKey      $blindIndexKey
     Redis:Password                <a senha do seu Redis>
     Database:RootPassword         <senha do superusuário Postgres>
     Database:AppPassword          <senha de runtime da app>
     Bootstrap:AdminEmail          <seu e-mail de superadmin>
     Bootstrap:AdminPassword       <senha forte>

   ⚠️ As chaves acima foram geradas AGORA, com CSPRNG, e NÃO estão salvas em
   lugar nenhum. Copie-as para o Secret Manager antes de fechar este terminal.

2. BOOTSTRAP do banco (cria banco, roles, grants, migrations e o admin):

     `$env:MIGRATE_ONLY='true'; dotnet run --project $projectPath

   E depois, para subir de verdade:

     dotnet run --project $projectPath

3. LEIA o README.md e o CLAUDE.md da app nova. As regras invioláveis e o
   porquê de cada armadilha vieram junto.

"@ -ForegroundColor White
