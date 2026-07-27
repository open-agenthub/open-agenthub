param()

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

$enabled = Render
foreach ($expected in @(
    'AgentHub__ControlNamespace: "agenthub-test"',
    'Browser__Enabled: "true"',
    'Browser__Image: "ghcr.io/open-agenthub/open-agenthub/browser:0.4.0"',
    'Browser__PullPolicy: "IfNotPresent"',
    'Browser__CpuRequest: "250m"',
    'Browser__MemoryRequest: "512Mi"',
    'Browser__CpuLimit: "1"',
    'Browser__MemoryLimit: "2Gi"',
    'Browser__ScreenWidth: "2560"',
    'Browser__ScreenHeight: "1600"',
    'Browser__StartupTimeoutSeconds: "90"',
    'Browser__CookieCheckpointSeconds: "60"',
    'Browser__CookieStateMaxBytes: "1048576"',
    'resources: ["networkpolicies"]',
    'verbs: ["get", "list", "watch", "create", "delete"]'
)) { Assert-Contains $enabled $expected }

$custom = Render @(
    '--set-string', 'browser.image.repository=registry.example.com/browser',
    '--set-string', 'browser.image.tag=pinned',
    '--set', 'browser.extraEgressPorts[0]=8443'
)
Assert-Contains $custom 'Browser__Image: "registry.example.com/browser:pinned"'
Assert-Contains $custom 'Browser__ExtraEgressPorts__0: "8443"'

$disabled = Render @('--set', 'browser.enabled=false')
Assert-Contains $disabled 'Browser__Enabled: "false"'
Assert-NotContains $disabled 'resources: ["networkpolicies"]'
Assert-Contains $disabled 'name: allow-agent-egress'
Assert-Contains $disabled 'name: allow-agent-to-backend'
Assert-NotContains $disabled 'port: 9222'

Write-Output 'Browser Helm values and least-privilege RBAC assertions passed.'
