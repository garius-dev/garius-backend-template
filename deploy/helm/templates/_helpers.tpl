{{/*
Nome base do release. Truncado em 63 caracteres porque é o limite de um label do
Kubernetes — passar disso faz o apply falhar com uma mensagem que não menciona
truncamento.
*/}}
{{- define "garius.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "garius.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "garius.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "garius.selectorLabels" . }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "garius.selectorLabels" -}}
app.kubernetes.io/name: {{ include "garius.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "garius.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "garius.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
A imagem, com a tag OBRIGATÓRIA.

`required` de propósito: sem tag, o Helm montaria `repositorio:` e o Kubernetes
resolveria isso como `:latest` — subindo uma versão que ninguém escolheu, sem
erro nenhum. Falhar aqui, no `helm template`, é infinitamente mais barato do que
descobrir em produção que o cluster está rodando outra coisa.
*/}}
{{- define "garius.image" -}}
{{- $tag := required "image.tag é obrigatório — passe --set image.tag=vX.Y.Z (a versão tem UMA fonte, o argumento do deploy)" .Values.image.tag }}
{{- printf "%s:%s" .Values.image.repository $tag }}
{{- end }}

{{/*
As variáveis de ambiente comuns aos dois papéis (API e migração).

Elas ficam num helper só porque DIVERGIR é o modo de falha real aqui: a migração
que conecta num banco e a API que conecta em outro é o tipo de bug que passa em
todo teste e só aparece em produção.
*/}}
{{- define "garius.commonEnv" -}}
{{- range $key, $value := .Values.env }}
- name: {{ $key }}
  value: {{ $value | quote }}
{{- end }}
- name: GOOGLE_APPLICATION_CREDENTIALS
  value: /var/secrets/google/{{ .Values.gcpServiceAccount.key }}
{{- end }}

{{/*
Contexto de segurança do container. Idêntico nos dois papéis, de propósito: um
Job de migração com privilégio a mais é a porta que ninguém audita.
*/}}
{{- define "garius.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
runAsNonRoot: true
# 1654 é o uid do usuário `app` nas imagens .NET (o Dockerfile já faz USER app).
runAsUser: 1654
capabilities:
  drop:
    - ALL
seccompProfile:
  type: RuntimeDefault
{{- end }}

{{/*
Volumes que o readOnlyRootFilesystem TORNA OBRIGATÓRIOS.

⚠️ Sem eles o pod não sobe. O .NET escreve em /tmp (arquivos temporários, o socket
de diagnóstico) e o ASP.NET Core em ~/.aspnet (chaves do DataProtection, quando
não há keyring externo). Com a raiz somente-leitura, essas escritas falham no
boot — e é a causa nº 1 de "liguei o readOnlyRootFilesystem e a aplicação parou
de subir".
*/}}
{{- define "garius.volumes" -}}
- name: tmp
  emptyDir: {}
- name: aspnet-data
  emptyDir: {}
- name: gcp-sa
  secret:
    secretName: {{ .Values.gcpServiceAccount.secretName }}
{{- end }}

{{- define "garius.volumeMounts" -}}
- name: tmp
  mountPath: /tmp
- name: aspnet-data
  mountPath: /home/app/.aspnet
- name: gcp-sa
  mountPath: /var/secrets/google
  readOnly: true
{{- end }}
