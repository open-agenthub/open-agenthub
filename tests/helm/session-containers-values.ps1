param()

# Chart assertions for session containers (docs/session-containers.md): the switch, the
# cluster-wide RBAC it adds, and the admission policies that confine that RBAC. The policies'
# behaviour against a real API server is recorded in the PR that added them; these assertions
# keep the rendered values from drifting.

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'

if (-not (Get-Command helm -ErrorAction SilentlyContinue)) { throw 'helm is required.' }

function Render([string[]]$Arguments = @()) {
    $output = & helm template agenthub-test $chartPath --namespace agenthub-test --set-string postgres.password=test-only @Arguments
    if ($LASTEXITCODE -ne 0) { throw "helm template failed with exit code $LASTEXITCODE" }
    return $output -join "`n"
}
function Assert-Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Rendered chart is missing: $Expected" }
}
function Assert-NotContains([string]$Text, [string]$Expected) {
    if ($Text.Contains($Expected)) { throw "Rendered chart unexpectedly contains: $Expected" }
}
function Document([string]$Text, [string]$Kind, [string]$Name) {
    $doc = ($Text -split "(?m)^---\s*$") | Where-Object { $_ -match "(?m)^kind: $Kind\s*$" -and $_ -match "(?m)^  name: $([regex]::Escape($Name))\s*$" }
    if (-not $doc) { throw "Rendered chart has no $Kind $Name" }
    return ($doc | Select-Object -First 1)
}

# On by default (owner decision on #107); operators switch it off.
$default = Render
foreach ($expected in @(
    'SessionContainers__Enabled: "true"',
    'SessionContainers__HubId: "agenthub-test"',
    'SessionContainers__NamespacePrefix: "agenthub-sessions-"',
    'SessionContainers__ClusterRoleName: "agenthub-test-session-containers"',
    'SessionContainers__AllowedRegistries: ""',
    'SessionContainers__RequireDigest: "false"',
    'SessionContainers__MaxPods: "5"',
    'SessionContainers__QuotaCpu: "2"',
    'SessionContainers__QuotaMemory: "4Gi"',
    'SessionContainers__QuotaEphemeralStorage: "10Gi"',
    'SessionContainers__MaxCpu: "1"',
    'SessionContainers__MaxMemory: "2Gi"',
    'SessionContainers__MaxVolumeSize: "2Gi"',
    'SessionContainers__EgressPorts: "443,22,80"'
)) { Assert-Contains $default $expected }

# The cluster-wide part: namespaces, RoleBindings, and bind on exactly one ClusterRole.
$clusterRole = Document $default 'ClusterRole' 'agenthub-test-session-containers-namespaces'
Assert-Contains $clusterRole 'resources: ["namespaces"]'
Assert-Contains $clusterRole 'verbs: ["get", "list", "watch", "create", "delete"]'
Assert-NotContains $clusterRole '"update"'
Assert-NotContains $clusterRole '"patch"'
Assert-Contains $clusterRole 'resources: ["rolebindings"]'
Assert-Contains $clusterRole 'verbs: ["bind"]'
Assert-Contains $clusterRole 'resourceNames: ["agenthub-test-session-containers"]'
Assert-NotContains $clusterRole 'resources: ["pods'
Assert-NotContains $clusterRole '"secrets"'
$clusterBinding = Document $default 'ClusterRoleBinding' 'agenthub-test-session-containers-namespaces'
Assert-Contains $clusterBinding 'name: agenthub-backend'
Assert-Contains $clusterBinding 'namespace: agenthub-test'

# The per-namespace role: what a helper needs, and none of secrets, exec or RBAC.
$nsRole = Document $default 'ClusterRole' 'agenthub-test-session-containers'
foreach ($expected in @('resources: ["pods"]', 'resources: ["pods/log"]', 'resources: ["services", "resourcequotas", "limitranges"]', 'resources: ["networkpolicies"]')) {
    Assert-Contains $nsRole $expected
}
foreach ($absent in @('"secrets"', '"configmaps"', '"pods/exec"', '"serviceaccounts"', '"roles"', '"persistentvolumeclaims"')) {
    Assert-NotContains $nsRole $absent
}
# It must never be bound cluster-wide: only the backend's own RoleBinding in a session namespace.
if ($default -match '(?ms)kind: ClusterRoleBinding.*?roleRef:\s*\n\s*apiGroup: [^\n]*\n\s*kind: ClusterRole\s*\n\s*name: agenthub-test-session-containers\s*\n') {
    throw 'The per-namespace ClusterRole is bound cluster-wide.'
}

# The admission policies that confine the RBAC above.
$nsPolicy = Document $default 'ValidatingAdmissionPolicy' 'agenthub-test-session-containers-namespaces'
foreach ($expected in @(
    'failurePolicy: Fail',
    'request.userInfo.username == "system:serviceaccount:agenthub-test:agenthub-backend"',
    'variables.ns.metadata.name.startsWith("agenthub-sessions-")',
    'variables.labels["agenthub.dev/session-containers"] == "agenthub-test"',
    'variables.labels["pod-security.kubernetes.io/enforce"] in ["restricted","baseline"]'
)) { Assert-Contains $nsPolicy $expected }
$scopePolicy = Document $default 'ValidatingAdmissionPolicy' 'agenthub-test-session-containers-scope'
foreach ($expected in @(
    'failurePolicy: Fail',
    'values: ["agenthub-test", "agenthub-sessions"]',
    'resources: ["*/*"]',
    'namespaceObject.metadata.labels["agenthub.dev/session-containers"] == "agenthub-test"',
    'object.roleRef.name == "agenthub-test-session-containers"',
    's.namespace == "agenthub-test"'
)) { Assert-Contains $scopePolicy $expected }
foreach ($name in @('agenthub-test-session-containers-namespaces', 'agenthub-test-session-containers-scope')) {
    $binding = Document $default 'ValidatingAdmissionPolicyBinding' $name
    Assert-Contains $binding "policyName: $name"
    Assert-Contains $binding 'validationActions: [Deny]'
}

# With root sessions disabled a helper namespace can only be restricted.
$noRoot = Render @('--set', 'agent.allowRootSessions=false')
Assert-Contains (Document $noRoot 'ValidatingAdmissionPolicy' 'agenthub-test-session-containers-namespaces') 'in ["restricted"]'

# Helpers get the session pod's egress list, including the ports allow-agent-egress adds.
$egress = Render @('--set', 'agent.extraEgressPorts[0]=8443', '--set', 'objectStorage.enabled=true',
    '--set-string', 'objectStorage.accessKey=GK0123456789abcdef01234567', '--set-string', 'objectStorage.secretKey=secret-only',
    '--set-string', 'objectStorage.rpcSecret=rpc-only', '--set-string', 'objectStorage.adminToken=admin-only')
Assert-Contains $egress 'SessionContainers__EgressPorts: "443,22,80,8443,3900"'
$agentEgress = Document $egress 'NetworkPolicy' 'allow-agent-egress'
foreach ($port in @(443, 22, 80, 8443, 3900)) { Assert-Contains $agentEgress "port: $port }" }

$custom = Render @(
    '--set', 'sessionContainers.allowedRegistries[0]=docker.io',
    '--set', 'sessionContainers.allowedRegistries[1]=registry.example.com',
    '--set', 'sessionContainers.requireDigest=true',
    '--set-string', 'sessionContainers.namespacePrefix=helpers-',
    '--set', 'sessionContainers.quota.pods=2'
)
Assert-Contains $custom 'SessionContainers__AllowedRegistries: "docker.io,registry.example.com"'
Assert-Contains $custom 'SessionContainers__RequireDigest: "true"'
Assert-Contains $custom 'SessionContainers__NamespacePrefix: "helpers-"'
Assert-Contains $custom 'variables.ns.metadata.name.startsWith("helpers-")'
Assert-Contains $custom 'SessionContainers__MaxPods: "2"'

# Off: no cluster-wide RBAC and no admission policy at all.
$disabled = Render @('--set', 'sessionContainers.enabled=false')
Assert-Contains $disabled 'SessionContainers__Enabled: "false"'
foreach ($absent in @('kind: ClusterRole', 'kind: ClusterRoleBinding', 'kind: ValidatingAdmissionPolicy', 'agenthub.dev/session-containers')) {
    Assert-NotContains $disabled $absent
}

Write-Output 'Session container Helm values, RBAC and admission policy assertions passed.'
