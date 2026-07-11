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
{{ .Values.image.registry }}/agent-runtime:{{ .Values.image.tag }}
{{- end }}

{{- define "agenthub.postgresConnectionString" -}}
{{- if .Values.postgres.enabled -}}
Host=postgres.{{ .Release.Namespace }}.svc.cluster.local;Database=agenthub;Username=agenthub;Password={{ required "postgres.password is required when postgres.enabled=true" .Values.postgres.password }}
{{- else -}}
{{ required "externalDatabase.connectionString is required when postgres.enabled=false" .Values.externalDatabase.connectionString }}
{{- end -}}
{{- end }}
