param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'
$devValuesPath = Join-Path $chartPath 'values-dev.yaml'

if (-not (Get-Command helm -ErrorAction SilentlyContinue)) {
    throw 'helm is required to run the rendered-chart assertions.'
}

function Render-Chart {
    param([string[]]$Arguments)

    $output = & helm template agenthub-test $chartPath `
        --namespace agenthub-test `
        --set-string postgres.password=test-only `
        @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "helm template failed with exit code $LASTEXITCODE"
    }
    return $output -join "`n"
}

function Get-ConfigValue {
    param(
        [string]$Rendered,
        [string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Rendered,
        "(?m)^\s{2}${escapedName}:\s+`"([^`"]+)`"\s*$"
    )
    if (-not $match.Success) {
        throw "Rendered ConfigMap does not contain a non-empty $Name value."
    }
    return $match.Groups[1].Value
}

function Assert-ImageSet {
    param(
        [string]$Rendered,
        [string]$ExpectedClaude,
        [string]$ExpectedCodex,
        [string]$CaseName
    )

    $claude = Get-ConfigValue $Rendered 'AgentHub__ClaudeAgentImage'
    $codex = Get-ConfigValue $Rendered 'AgentHub__CodexAgentImage'

    if ($claude -ne $ExpectedClaude) {
        throw "$CaseName Claude image mismatch: expected '$ExpectedClaude', got '$claude'."
    }
    if ($codex -ne $ExpectedCodex) {
        throw "$CaseName Codex image mismatch: expected '$ExpectedCodex', got '$codex'."
    }
    if ($claude -eq $codex) {
        throw "$CaseName rendered identical Claude and Codex images: '$claude'."
    }
    foreach ($image in @($claude, $codex)) {
        if ([string]::IsNullOrWhiteSpace($image) -or $image -match '//|^/|:$') {
            throw "$CaseName rendered malformed image reference '$image'."
        }
    }

    $renderedImages = [regex]::Matches($Rendered, '(?m)^\s+image:\s+"?([^"\s]+)"?\s*$')
    foreach ($match in $renderedImages) {
        $image = $match.Groups[1].Value
        if ([string]::IsNullOrWhiteSpace($image) -or $image -match '//|^/|:$') {
            throw "$CaseName rendered another malformed image reference '$image'."
        }
    }
}

$default = Render-Chart
Assert-ImageSet $default `
    'ghcr.io/open-agenthub/open-agenthub/agent-runtime-claude:latest' `
    'ghcr.io/open-agenthub/open-agenthub/agent-runtime-codex:latest' `
    'default values'

$dev = Render-Chart @('--values', $devValuesPath)
Assert-ImageSet $dev `
    'open-agenthub-dev/agent-runtime-claude:local' `
    'open-agenthub-dev/agent-runtime-codex:local' `
    'development values'

$fallback = Render-Chart @(
    '--set-string', 'image.registry=registry.example.com/team/runtime',
    '--set-string', 'image.tag=2026.07'
)
Assert-ImageSet $fallback `
    'registry.example.com/team/runtime/agent-runtime-claude:2026.07' `
    'registry.example.com/team/runtime/agent-runtime-codex:2026.07' `
    'registry/tag fallback'

$override = Render-Chart @(
    '--set-string', 'image.registry=unused.example.com/fallback',
    '--set-string', 'image.tag=unused',
    '--set-string', 'agent.images.claude=images.example.com/custom/claude-runtime:pinned',
    '--set-string', 'agent.images.codex=images.example.com/custom/codex-runtime:pinned'
)
Assert-ImageSet $override `
    'images.example.com/custom/claude-runtime:pinned' `
    'images.example.com/custom/codex-runtime:pinned' `
    'explicit overrides'

Write-Output 'Helm runtime image rendering assertions passed (default, dev, fallback, override).'
