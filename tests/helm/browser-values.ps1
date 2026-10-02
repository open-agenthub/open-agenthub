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
    'Browser__Image: "ghcr.io/open-agenthub/open-agenthub/browser:latest"',
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
    'Browser__PreviewPorts__0: "3000"',
    'Browser__PreviewPorts__2: "5173"',
    'resources: ["networkpolicies"]',
    # deletecollection included deliberately: session teardown removes a session's policies by
    # label selector in one call, and without the verb every delete fails with a 403.
    'verbs: ["get", "list", "watch", "create", "delete", "deletecollection"]'
)) { Assert-Contains $enabled $expected }

$custom = Render @(
    '--set-string', 'browser.image.repository=registry.example.com/browser',
    '--set-string', 'browser.image.tag=pinned',
    '--set', 'browser.extraEgressPorts[0]=8443'
)
Assert-Contains $custom 'Browser__Image: "registry.example.com/browser:pinned"'

# The browser follows the shared image.tag like every other component, and only falls
# back to the chart version when no tag is configured at all.
$sharedTag = Render @('--set-string', 'image.tag=v9.9.9')
Assert-Contains $sharedTag 'Browser__Image: "ghcr.io/open-agenthub/open-agenthub/browser:v9.9.9"'

# Clearing image.tag means every agent image has to be given explicitly, so pin them all.
$noTag = Render @(
    '--set-string', 'image.tag=',
    '--set-string', 'agent.images.claude=example/claude:test',
    '--set-string', 'agent.images.codex=example/codex:test',
    '--set-string', 'agent.images.cursor=example/cursor:test',
    '--set-string', 'agent.images.openclaw=example/openclaw:test'
)
# Read the expectation from Chart.yaml rather than repeating the number here: this is the
# appVersion the fallback resolves to, and a literal version in this script turns every
# release bump into a red build for no reason (the Codex smoke script does the same with
# the pinned CLI version).
$appVersion = (Select-String -Path (Join-Path $chartPath 'Chart.yaml') -Pattern '^appVersion:\s*"?([^"\s]+)"?').Matches[0].Groups[1].Value
if (-not $appVersion) { throw 'could not read appVersion from Chart.yaml' }
Assert-Contains $noTag "Browser__Image: `"ghcr.io/open-agenthub/open-agenthub/browser:$appVersion`""
Assert-Contains $custom 'Browser__ExtraEgressPorts__0: "8443"'

$disabled = Render @('--set', 'browser.enabled=false')
Assert-Contains $disabled 'Browser__Enabled: "false"'
Assert-Contains $disabled 'name: allow-agent-egress'
Assert-Contains $disabled 'name: allow-agent-to-backend'
Assert-NotContains $disabled 'port: 9222'
# Per-session network policies come from the browser AND from approved runtime port requests,
# so turning the browser off alone must not drop the permission — an instance running port
# requests would then be unable to manage the policies it creates.
Assert-Contains $disabled 'resources: ["networkpolicies"]'

# Least privilege still holds: with neither feature enabled there is nothing to manage.
$noPolicies = Render @('--set', 'browser.enabled=false', '--set', 'agent.portRequests.enabled=false')
Assert-NotContains $noPolicies 'resources: ["networkpolicies"]'

Write-Output 'Browser Helm values and least-privilege RBAC assertions passed.'
