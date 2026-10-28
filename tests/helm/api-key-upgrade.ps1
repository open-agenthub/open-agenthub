param(
    # Release whose chart stands for "an install from before the API key".
    [string]$BaselineRef = 'v1.4.0'
)

# Real `helm install` / `helm upgrade` against a Kubernetes API server (#324). Needs a cluster in
# the current kubeconfig — a rootless kube-apiserver is enough, nothing has to run: only the
# objects helm writes and reads back are checked. Not part of CI, which has no cluster in the
# helm job; tests/helm/api-key-values.ps1 is the template-only half that is.
#
# What only a real API server shows: `lookup` reading the generated key and owner back, so an
# upgrade keeps them instead of rolling a new key or moving the owner.

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'
foreach ($tool in 'helm', 'kubectl', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is required." }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "agenthub-api-key-upgrade-$PID"
New-Item -ItemType Directory -Path $work | Out-Null
& git -C $repoRoot archive $BaselineRef helm/open-agenthub | tar -x -C $work
if ($LASTEXITCODE -ne 0) { throw "Could not extract the $BaselineRef chart." }
$baselineChart = Join-Path $work 'helm/open-agenthub'

function Invoke-Helm([string[]]$Arguments) {
    $output = & helm @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "helm $($Arguments -join ' ') failed:`n$output" }
    return $output -join "`n"
}
function Read-Secret([string]$Namespace, [string]$Key) {
    $enc = & kubectl -n $Namespace get secret agenthub-api-key -o "jsonpath={.data.$Key}"
    if ($LASTEXITCODE -ne 0 -or -not $enc) { throw "Secret agenthub-api-key/$Key missing in $Namespace" }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($enc))
}
function Read-Config([string]$Namespace) {
    return (& kubectl -n $Namespace get configmap agenthub-config -o 'jsonpath={.data.Auth__ApiKeyOwner}')
}
function Assert-Equal($Expected, $Actual, [string]$What, [switch]$Secret) {
    if ($Expected -ne $Actual) {
        if ($Secret) { throw "${What}: the value changed" }
        throw "${What}: expected '$Expected', got '$Actual'"
    }
    Write-Host "  ok  ${What}: $(if ($Secret) { 'unchanged' } else { $Actual })"
}
# The sessions namespace is cluster-scoped and deleted asynchronously, so each scenario gets its own.
function Remove-Release([string]$Namespace) {
    & helm uninstall agenthub -n $Namespace 2>$null | Out-Null
    & kubectl delete namespace $Namespace "$Namespace-sessions" --wait=false 2>$null | Out-Null
}
function Values([string]$Namespace) {
    return @('--set-string', 'postgres.password=test-only', '--set-string', "sessionsNamespace=$Namespace-sessions")
}

try {
    # --- 1. Fresh install: a key is generated and acts as "admin"; upgrades keep both. ---
    $ns = 'apikey-fresh'
    $notes = Invoke-Helm (@('install', 'agenthub', $chartPath, '-n', $ns, '--create-namespace') + (Values $ns))
    $key = Read-Secret $ns 'api-key'
    if ($key -notmatch '^[A-Za-z0-9]{48}$') { throw 'Fresh install: unexpected key shape.' }
    Assert-Equal 'admin' (Read-Secret $ns 'owner') 'fresh install owner'
    if (-not $notes.Contains("get secret agenthub-api-key")) { throw 'NOTES.txt does not say how to read the key.' }
    Invoke-Helm (@('upgrade', 'agenthub', $chartPath, '-n', $ns) + (Values $ns)) | Out-Null
    Assert-Equal $key (Read-Secret $ns 'api-key') 'key after upgrade' -Secret
    Assert-Equal 'admin' (Read-Secret $ns 'owner') 'owner after upgrade'

    # Rotation as NOTES.txt describes it: the patched value is what the next upgrade keeps.
    & kubectl -n $ns patch secret agenthub-api-key -p '{"stringData":{"api-key":"rotated-0123456789abcdef0123456789abcdef"}}' | Out-Null
    Invoke-Helm (@('upgrade', 'agenthub', $chartPath, '-n', $ns) + (Values $ns)) | Out-Null
    Assert-Equal 'rotated-0123456789abcdef0123456789abcdef' (Read-Secret $ns 'api-key') 'rotated key after upgrade' -Secret

    # A deleted Secret gets a new key, but the owner still comes back from the ConfigMap —
    # moving to "dev" here would hide every session the install has.
    & kubectl -n $ns delete secret agenthub-api-key | Out-Null
    Invoke-Helm (@('upgrade', 'agenthub', $chartPath, '-n', $ns) + (Values $ns)) | Out-Null
    if ((Read-Secret $ns 'api-key') -eq $key) { throw 'A deleted Secret should get a new key.' }
    Assert-Equal 'admin' (Read-Secret $ns 'owner') 'owner after the Secret was deleted'
    Remove-Release $ns

    # --- 2. Upgrade of a pre-key install without OIDC: its sessions belong to "dev". ---
    $ns = 'apikey-legacy'
    Invoke-Helm (@('install', 'agenthub', $baselineChart, '-n', $ns, '--create-namespace') + (Values $ns)) | Out-Null
    & kubectl -n $ns get secret agenthub-api-key 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "$BaselineRef unexpectedly has an API key Secret." }
    $notes = Invoke-Helm (@('upgrade', 'agenthub', $chartPath, '-n', $ns) + (Values $ns))
    $legacyKey = Read-Secret $ns 'api-key'
    Assert-Equal 'dev' (Read-Secret $ns 'owner') 'owner after upgrading a pre-key install'
    Assert-Equal 'dev' (Read-Config $ns) 'ConfigMap owner after upgrading a pre-key install'
    if (-not $notes.Contains('acts as "dev"')) { throw 'NOTES.txt does not explain the "dev" owner.' }
    Invoke-Helm (@('upgrade', 'agenthub', $chartPath, '-n', $ns) + (Values $ns)) | Out-Null
    Assert-Equal $legacyKey (Read-Secret $ns 'api-key') 'legacy key after a second upgrade' -Secret
    Assert-Equal 'dev' (Read-Secret $ns 'owner') 'legacy owner after a second upgrade'
    Remove-Release $ns

    # --- 3. Upgrade with --reuse-values from a pre-key install (what install.sh users run). ---
    $ns = 'apikey-reuse'
    Invoke-Helm (@('install', 'agenthub', $baselineChart, '-n', $ns, '--create-namespace') + (Values $ns)) | Out-Null
    Invoke-Helm @('upgrade', 'agenthub', $chartPath, '-n', $ns, '--reuse-values') | Out-Null
    Assert-Equal 'dev' (Read-Secret $ns 'owner') 'owner after --reuse-values upgrade'

    Write-Host 'API key upgrade assertions passed.'
}
finally {
    foreach ($ns in 'apikey-fresh', 'apikey-legacy', 'apikey-reuse') { Remove-Release $ns }
    Remove-Item -Recurse -Force $work
}
