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
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Não-root. A imagem aspnet já traz o usuário `app`; usá-lo custa uma linha e
# tira a escalada trivial de quem conseguir execução dentro do container.
USER app

# 8080 é o default do usuário `app` (não-root não abre porta < 1024). O compose
# não precisa mexer em ASPNETCORE_URLS.
EXPOSE 8080

# Sem HEALTHCHECK aqui, de propósito: a imagem `aspnet` não tem curl nem wget, e
# instalá-los só para isso engorda o runtime e amplia a superfície. O healthcheck
# fica no compose, que usa o /health/live (o endpoint que existe de verdade —
# ver HealthSetup.cs).

ENTRYPOINT ["dotnet", "Garius.Api.dll"]
