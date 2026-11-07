param()

# #491: the chart's default image tag is its appVersion. An install of chart X has to run the
# images of release X, and an upgrade to chart Y has to move every component to Y. The old
# default "latest" under IfNotPresent did neither: a fresh install ran whatever main had last
# published, and upgrades left every node on the image it had pulled first.

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'

if (-not (Get-Command helm -ErrorAction SilentlyContinue)) { throw 'helm is required.' }

$appVersion = (Select-String -Path (Join-Path $chartPath 'Chart.yaml') -Pattern '^appVersion:\s*"?([^"\s]+)"?').Matches[0].Groups[1].Value
if (-not $appVersion) { throw 'could not read appVersion from Chart.yaml' }

function Render([string]$Chart, [string[]]$Arguments = @()) {
    $output = & helm template agenthub-test $Chart --namespace agenthub-test `
        --set-string postgres.password=test-only --set files.officePreview.enabled=true @Arguments
    if ($LASTEXITCODE -ne 0) { throw "helm template failed with exit code $LASTEXITCODE" }
    return $output -join "`n"
}

# Every project image the chart can select: pod specs and the runtime images the backend starts
# session and browser pods from. Third-party images (postgres, garage, git) are pinned elsewhere.
function Project-Images([string]$Rendered) {
    $pattern = '(?m)(?:^\s+image:\s*|^\s+\w+(?:AgentImage|__Image):\s*)"?(ghcr\.io/open-agenthub/open-agenthub/[^"\s]+)"?\s*$'
    return @([regex]::Matches($Rendered, $pattern) | ForEach-Object { $_.Groups[1].Value })
}

function Assert-AllTagged([string]$Rendered, [string]$Tag, [string]$CaseName) {
    $images = Project-Images $Rendered
    $expected = @('backend', 'frontend', 'artifact-renderer', 'browser',
        'agent-runtime-claude', 'agent-runtime-codex', 'agent-runtime-cursor',
        'agent-runtime-openclaw', 'agent-runtime-opencode')
    foreach ($component in $expected) {
        $image = "ghcr.io/open-agenthub/open-agenthub/${component}:$Tag"
        if ($images -notcontains $image) {
            throw "$CaseName did not select $image. Rendered: $($images -join ', ')"
        }
    }
    $other = @($images | Where-Object { -not $_.EndsWith(":$Tag") })
    if ($other.Count -gt 0) { throw "$CaseName rendered images off the tag ${Tag}: $($other -join ', ')" }
}

Assert-AllTagged (Render $chartPath) $appVersion 'default values'
Assert-AllTagged (Render $chartPath @('--set-string', 'image.tag=')) $appVersion 'explicitly empty image.tag'
# A moving tag is still possible, but only as a deliberate choice.
Assert-AllTagged (Render $chartPath @('--set-string', 'image.tag=latest')) 'latest' 'explicit latest'

# An upgrade is a newer chart with a newer appVersion: the same values must move every image.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "agenthub-chart-$([guid]::NewGuid())"
try {
    Copy-Item -Recurse $chartPath $scratch
    $chartFile = Join-Path $scratch 'Chart.yaml'
    (Get-Content $chartFile -Raw) -replace '(?m)^appVersion:.*$', 'appVersion: "99.0.1"' |
        Set-Content -NoNewline $chartFile
    Assert-AllTagged (Render $scratch) '99.0.1' 'upgraded chart'
} finally {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

Write-Output 'Image tag defaults follow the chart appVersion.'
