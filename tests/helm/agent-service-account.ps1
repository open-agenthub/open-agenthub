param()

# The agent pods' service account must stay without permissions and without a mounted token.
# Hub-side decisions such as who pays for a session's tokens (docs/usage-tracking.md) rely on the
# pod being unable to change its own Kubernetes objects; a binding to this account would undo that.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Render([string[]]$Arguments) {
    $rendered = & helm template test (Join-Path $repoRoot 'helm/open-agenthub') --set postgres.password=test @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    return $rendered -join "`n"
}

# Every optional feature that adds RBAC on, so a binding added behind any flag is caught.
$variants = @(
    @(),
    @('--set', 'browser.enabled=true', '--set', 'agent.portRequests.enabled=true', '--set', 'sessionContainers.enabled=true')
)
foreach ($arguments in $variants) {
    $documents = (Render $arguments) -split '(?m)^---\s*$'

    $account = $documents | Where-Object { $_ -match '(?m)^kind: ServiceAccount\s*$' -and $_ -match '(?m)^  name: agenthub-agent\s*$' }
    if (@($account).Count -ne 1) { throw 'expected exactly one agenthub-agent ServiceAccount' }
    if ($account -notmatch '(?m)^automountServiceAccountToken: false\s*$') {
        throw 'the agenthub-agent ServiceAccount must not mount its token'
    }

    foreach ($document in $documents) {
        if ($document -notmatch '(?m)^kind: (Cluster)?RoleBinding\s*$') { continue }
        if ($document -match '(?m)^\s*-?\s*name: agenthub-agent\s*$' -and $document -match '(?m)^subjects:') {
            $name = [regex]::Match($document, '(?m)^  name: (\S+)').Groups[1].Value
            throw "binding $name grants permissions to the agenthub-agent service account"
        }
    }
}
Write-Host 'agent service account: no token, no bindings'
