<#
.SYNOPSIS
    Valida o chart: lint, render e conferência contra o schema REAL do Kubernetes.

.DESCRIPTION
    Um chart não tem suíte de testes como o C#. O que dá para provar sem cluster é:

      1. `helm lint`        — a sintaxe do chart e do template;
      2. `helm template`    — que ele renderiza (pega erro de `required`, de indentação
                              e de função inexistente);
      3. `kubeconform`      — que o YAML gerado é ACEITO pela API do Kubernetes.

    O passo 3 é o que vale. Sem ele, um manifesto com `maxUnavailble` (typo) ou um campo
    de uma versão que o cluster não tem passa liso pelos dois primeiros e só falha no
    `kubectl apply` — no meio do deploy, com a migração já rodada.

    ⚠️ Isto NÃO substitui um teste em cluster. Ele prova que os manifestos são válidos,
    não que a aplicação sobe. O que ele pega é a classe de erro que mais aparece: campo
    errado, versão de API errada, template que não renderiza.

.PARAMETER KubernetesVersion
    Contra qual versão validar. O default é 1.29 porque o `preStop.sleep` (ver
    deployment.yaml) só existe a partir dela — validar contra 1.28 REPROVA, de propósito.

.EXAMPLE
    ./validate.ps1
    ./validate.ps1 -KubernetesVersion 1.31.0
#>
[CmdletBinding()]
param(
    [string]$KubernetesVersion = "1.29.0"
)

$ErrorActionPreference = "Stop"

$chart = $PSScriptRoot
$rendered = Join-Path ([System.IO.Path]::GetTempPath()) "garius-chart-$(Get-Random).yaml"

function Assert-Tool([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name não encontrado. $hint"
    }
}

Assert-Tool "helm" "Instale: https://helm.sh/docs/intro/install/"
Assert-Tool "kubeconform" "Instale: https://github.com/yannh/kubeconform/releases"

try {
    Write-Host "1/4  helm lint" -ForegroundColor Cyan
    helm lint $chart --set image.tag=v0.0.0-validate
    if ($LASTEXITCODE -ne 0) { throw "helm lint falhou." }

    Write-Host "2/4  helm template (com tudo LIGADO)" -ForegroundColor Cyan
    # networkPolicy ligada de propósito: ela é opt-in no values, e um template que só
    # é exercitado quando alguém o liga em produção é um template não testado.
    helm template validate $chart `
        --set image.tag=v0.0.0-validate `
        --set networkPolicy.enabled=true `
    | Out-File -FilePath $rendered -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "helm template falhou." }

    Write-Host "3/4  a tag da imagem é OBRIGATÓRIA" -ForegroundColor Cyan
    # Sem tag, o Helm montaria "repositorio:" e o Kubernetes leria isso como :latest —
    # subindo uma versão que ninguém escolheu. O chart tem de RECUSAR.
    helm template validate $chart 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        throw "O chart renderizou SEM image.tag. O guarda de versão sumiu — ver _helpers.tpl."
    }
    Write-Host "     ok: recusou renderizar sem image.tag" -ForegroundColor DarkGray

    Write-Host "4/4  kubeconform (schema real do Kubernetes $KubernetesVersion)" -ForegroundColor Cyan
    kubeconform -strict -summary -kubernetes-version $KubernetesVersion $rendered
    if ($LASTEXITCODE -ne 0) { throw "kubeconform reprovou os manifestos." }

    Write-Host ""
    Write-Host "Chart válido." -ForegroundColor Green
}
finally {
    if (Test-Path $rendered) { Remove-Item $rendered -Force }
}
