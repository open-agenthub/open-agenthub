{{- define "agenthub.labels" -}}
app.kubernetes.io/name: agenthub
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}

{{- define "agenthub.backendImage" -}}
{{ .Values.image.registry }}/backend:{{ .Values.image.tag }}
{{- end }}

{{- define "agenthub.frontendImage" -}}
{{ .Values.image.registry }}/frontend:{{ .Values.image.tag }}
{{- end }}

{{- define "agenthub.agentImage" -}}
{{ include "agenthub.claudeAgentImage" . }}
{{- end }}

{{- define "agenthub.claudeAgentImage" -}}
{{- $override := .Values.agent.images.claude | default "" | trim -}}
{{- if $override -}}
{{- $override -}}
{{- else -}}
{{- $registry := required "image.registry is required when agent.images.claude is empty" .Values.image.registry | trimSuffix "/" -}}
{{- $tag := required "image.tag is required when agent.images.claude is empty" .Values.image.tag -}}
{{- printf "%s/agent-runtime-claude:%s" $registry $tag -}}
{{- end -}}
{{- end }}

{{- define "agenthub.codexAgentImage" -}}
{{- $override := .Values.agent.images.codex | default "" | trim -}}
{{- if $override -}}
{{- $override -}}
{{- else -}}
{{- $registry := required "image.registry is required when agent.images.codex is empty" .Values.image.registry | trimSuffix "/" -}}
{{- $tag := required "image.tag is required when agent.images.codex is empty" .Values.image.tag -}}
{{- printf "%s/agent-runtime-codex:%s" $registry $tag -}}
{{- end -}}
{{- end }}

{{- define "agenthub.postgresConnectionString" -}}
{{- if .Values.postgres.enabled -}}
Host=postgres.{{ .Release.Namespace }}.svc.cluster.local;Database=agenthub;Username=agenthub;Password={{ required "postgres.password is required when postgres.enabled=true" .Values.postgres.password }}
{{- else -}}
{{ required "externalDatabase.connectionString is required when postgres.enabled=false" .Values.externalDatabase.connectionString }}
{{- end -}}
{{- end }}
