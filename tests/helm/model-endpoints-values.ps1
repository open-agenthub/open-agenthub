param()

# agent.modelEndpoints (docs/custom-model-endpoints.md): the operator's list of in-cluster model
# servers becomes one egress policy for agent pods, scoped to those pods and ports; nothing is
# rendered without it.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'

if (-not (Get-Command helm -ErrorAction SilentlyContinue)) { throw 'helm is required.' }

function Render([string[]]$Arguments = @()) {
    $output = & helm template agenthub-test $chartPath --namespace agenthub-test --set-string postgres.password=test-only @Arguments 2>&1
    return @{ Text = ($output -join "`n"); Exit = $LASTEXITCODE }
}
function Policy([string]$Text) {
    $doc = ($Text -split "(?m)^---\s*$") | Where-Object { $_ -match 'name: allow-agent-model-endpoints' }
    return ($doc -join "`n")
}
function Assert-Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Rendered policy is missing: $Expected`n$Text" }
}

$default = Render
if ($default.Exit -ne 0) { throw "helm template failed: $($default.Text)" }
if (Policy $default.Text) { throw 'allow-agent-model-endpoints must not render without agent.modelEndpoints.' }

$values = Join-Path ([System.IO.Path]::GetTempPath()) "model-endpoints-$([guid]::NewGuid()).yaml"
@'
agent:
  modelEndpoints:
    - namespace: llm
      podLabels: { app: llama-server }
      ports: [8080]
    - namespace: gateway
      ports: [9000, 9001]
'@ | Set-Content -Path $values
try {
    $rendered = Render @('-f', $values)
    if ($rendered.Exit -ne 0) { throw "helm template failed: $($rendered.Text)" }
    $policy = Policy $rendered.Text
    foreach ($expected in @(
        'namespace: agenthub-sessions',
        'matchLabels: { agenthub.dev/component: agent }',
        'policyTypes: [Egress]',
        'matchLabels: { kubernetes.io/metadata.name: "llm" }',
        'matchLabels: {"app":"llama-server"}',
        '- { protocol: TCP, port: 8080 }',
        'matchLabels: { kubernetes.io/metadata.name: "gateway" }',
        '- { protocol: TCP, port: 9000 }',
        '- { protocol: TCP, port: 9001 }'
    )) { Assert-Contains $policy $expected }
    # The namespace and pod selectors are one peer (AND), not two (OR): two peers would open the
    # port on every pod of the namespace, and on every namespace's llama-server pods.
    if ($policy -notmatch '(?m)- namespaceSelector:\s*\n\s*matchLabels: \{ kubernetes\.io/metadata\.name: "llm" \}\s*\n\s{10}podSelector:') {
        throw "podSelector must belong to the same peer as the namespaceSelector:`n$policy"
    }
    # A server without podLabels: every pod of that namespace, on the listed ports only.
    $gateway = ($policy -split '- to:') | Where-Object { $_ -match '"gateway"' }
    if ($gateway -match 'podSelector') { throw "an entry without podLabels must not get a podSelector:`n$gateway" }

    @'
agent:
  modelEndpoints:
    - podLabels: { app: llama-server }
      ports: [8080]
'@ | Set-Content -Path $values
    $missing = Render @('-f', $values)
    if ($missing.Exit -eq 0 -or -not $missing.Text.Contains('agent.modelEndpoints[].namespace is required')) {
        throw "an entry without a namespace must fail the render: $($missing.Text)"
    }
} finally {
    Remove-Item -Force $values -ErrorAction SilentlyContinue
}

Write-Host 'model endpoint values: ok'
