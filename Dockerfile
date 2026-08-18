# ─────────────────────────────────────────────────────────────────────────────
# UMA imagem, DOIS modos.
#
# O mesmo binário roda a API e roda as migrations — quem decide é a env var
# MIGRATE_ONLY, lida no Program.cs. Por isso NÃO existem duas tags (-api e
# -migration): seriam bit a bit idênticas, e manter duas dobra o build, o push e
# a chance de subir a API numa versão e a migration em outra.
#
#   MIGRATE_ONLY=true  -> cria banco/roles/grants, aplica migrations, sai com 0
#   (sem a flag)       -> a API sobe
# ─────────────────────────────────────────────────────────────────────────────

# --- build ------------------------------------------------------------------
# ⚠️ AS IMAGENS SÃO PINADAS POR DIGEST, não só por tag.
#
# `sdk:10.0` é uma tag MUTÁVEL: ela aponta para uma imagem diferente a cada patch
# do .NET. Dois builds do MESMO commit podem gerar binários diferentes — e quando
# um deles quebra, não há como saber o que mudou. Com o digest, o build é
# reproduzível: o mesmo commit dá a mesma imagem, sempre.
#
# O CUSTO é real e precisa ser aceito conscientemente: um digest fixo NÃO recebe
# patch de segurança sozinho. Sem alguém atualizando, a imagem envelhece e a CVE
# fica. Por isso o Renovate (ver renovate.json) abre PR quando sai versão nova —
# pinar SEM automação de atualização é trocar um problema por outro.
#
# Para atualizar à mão:
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

# Os arquivos de projeto vêm ANTES do código-fonte, sozinhos, porque o restore só
# depende deles. Assim a camada de restore (a lenta) só é refeita quando uma
# dependência muda de verdade — e não a cada linha de C# que você edita.
#
# ⚠️ O .editorconfig NÃO é opcional aqui. É ele que calibra os analisadores, e o
# build deste template trata warning como ERRO. Sem ele, o build QUEBRA dentro do
# container (CA1000, CA1711, CA1716...) enquanto passa liso na sua máquina — que
# é o pior tipo de erro: o que só aparece no deploy.
COPY .editorconfig ./
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Garius.Api/Garius.Api.csproj                   src/Garius.Api/
COPY src/Garius.Core/Garius.Core.csproj                 src/Garius.Core/
COPY src/Garius.Infrastructure/Garius.Infrastructure.csproj src/Garius.Infrastructure/
RUN dotnet restore src/Garius.Api/Garius.Api.csproj

COPY src/ src/

# --no-restore: o restore já aconteceu acima. Sem isso ele roda de novo e joga a
# camada de cache fora.
RUN dotnet publish src/Garius.Api/Garius.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# --- runtime ----------------------------------------------------------------
#
# CHISELED: sem shell, sem gerenciador de pacotes, sem coreutils. Só o runtime do
# .NET e as bibliotecas de que ele depende.
#
# Isso corta a superfície de ataque no ponto que mais importa: quem conseguir
# execução de comando dentro do container não encontra `sh`, `curl`, `apt` nem
# `cat` para escalar. A imagem também fica bem menor, o que acelera o pull em todo
# nó novo do cluster.
#
# ⚠️ DUAS CONSEQUÊNCIAS QUE MORDEM SE FOREM IGNORADAS:
#
#   1. Não há `/bin/sleep`. Um `preStop` com `exec: [/bin/sleep, "5"]` falha em
#      SILÊNCIO (o Kubernetes só registra um evento) e a corrida do encerramento
#      volta — com o manifesto parecendo correto. Por isso o chart usa a action
#      `sleep` NATIVA do Kubernetes (1.29+). Ver deploy/helm/templates/deployment.yaml.
#
#   2. `docker exec <container> sh` não funciona. Para depurar, use
#      `kubectl debug` com um container efêmero, ou troque temporariamente para a
#      variante não-chiseled.
#
# ⚠️ FALTA O KERBEROS, e isto aparece no log: ao subir, o Npgsql tenta carregar
# `libgssapi_krb5.so.2` e registra "Cannot load library". É INOFENSIVO aqui —
# a autenticação do Postgres neste template é por senha, e o Npgsql segue sem GSSAPI
# —, mas o erro assusta quem lê o log pela primeira vez e some se você usar a
# variante `-extra`.
#
# Só troque para `-extra` se realmente precisar: de Kerberos/GSSAPI, de ICU
# (formatação por cultura) ou de tzdata. Ela é maior e traz de volta parte da
# superfície que a chiseled corta.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:0839314d08bb65da369135389a5d8291f75ace587fbb0488f469eb92c62eef68 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Não-root. A imagem chiseled já roda como `app` (uid 1654) por padrão; explicitar
# aqui é uma trava — se alguém trocar a imagem base por uma que roda como root, a
# linha continua valendo.
USER app

# 8080 é o default do usuário `app` (não-root não abre porta < 1024). O compose
# não precisa mexer em ASPNETCORE_URLS.
EXPOSE 8080

# Sem HEALTHCHECK aqui, de propósito — e agora é mais que uma preferência: a imagem
# chiseled NÃO TEM shell nem curl, então não há com o que fazer a requisição. Quem
# checa a saúde é o orquestrador, pelo /health/live: o compose com o /dev/tcp do
# bash (que só funciona na variante não-chiseled) ou, em Kubernetes, a httpGet
# probe — que faz a requisição de FORA do container e não depende de nada dentro
# dele. Ver HealthSetup.cs e deploy/helm.

ENTRYPOINT ["dotnet", "Garius.Api.dll"]
