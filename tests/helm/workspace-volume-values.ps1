param()
$ErrorActionPreference = 'Stop'
$chart = Join-Path $PSScriptRoot '../../helm/open-agenthub'
function Render([string[]]$Arguments = @()) {
    $output = & helm template workspace-proof $chart --namespace agenthub --set-string postgres.password=proof-only @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    return $output -join "`n"
}
function Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Missing chart setting: $Expected" }
}
$default = Render
Contains $default 'AgentHub__WorkspaceVolumePersistent: "false"'
Contains $default 'AgentHub__WorkspaceVolumeSize: "10Gi"'
Contains $default 'AgentHub__WorkspaceVolumeStorageClass: ""'
$enabled = Render @('--set', 'agent.workspaceVolume.persistent=true', '--set-string', 'agent.workspaceVolume.size=20Gi', '--set-string', 'agent.workspaceVolume.storageClass=proof-storage')
Contains $enabled 'AgentHub__WorkspaceVolumePersistent: "true"'
Contains $enabled 'AgentHub__WorkspaceVolumeSize: "20Gi"'
Contains $enabled 'AgentHub__WorkspaceVolumeStorageClass: "proof-storage"'
# Disabling new admission must not remove permission to resume or delete recorded claims.
foreach ($render in @($default, $enabled)) {
    $role = ($render -split '(?m)^---\s*$') | Where-Object { $_ -match '(?m)^kind: Role\s*$' -and $_ -match 'resources: \["persistentvolumeclaims"\]' }
    if (@($role).Count -ne 1) { throw 'Expected exactly one session namespace claim role' }
    Contains $role 'verbs: ["get", "list", "create", "delete"]'
    if ($role -match 'resources: \["persistentvolumes"\]') { throw 'Session claim management must not grant cluster volume authority' }
}
Write-Host 'PASS: workspace admission defaults, explicit storage settings and retained claim cleanup RBAC'
