param()

# Chart assertions for the login of installs without OIDC (#324, docs/no-oidc-api-key.md).
# `helm template` cannot look anything up in a cluster, so this covers the decisions made from
# values and from --is-upgrade; tests/helm/api-key-upgrade.ps1 covers reading the generated key
# and owner back from a real API server.

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
function Get-Document([string]$Text, [string]$Kind, [string]$Name) {
    foreach ($doc in ($Text -split "(?m)^---\s*$")) {
        if ($doc -match "(?m)^kind: $Kind\s*$" -and $doc -match "(?m)^  name: $Name\s*$") { return $doc }
    }
    return $null
}
function Get-GeneratedKey([string]$Text) {
    $secret = Get-Document $Text 'Secret' 'agenthub-api-key'
    if (-not $secret) { throw 'The chart did not generate the agenthub-api-key Secret.' }
    if ($secret -notmatch '(?m)^  api-key: "([^"]*)"') { throw 'The generated Secret has no api-key.' }
    return $Matches[1]
}

# --- Chart default: no OIDC, so the chart generates a key and the backend reads it. ---
$default = Render
$key = Get-GeneratedKey $default
# The backend refuses anything under 32 characters; 48 alphanumerics leave no doubt.
if ($key -notmatch '^[A-Za-z0-9]{48}$') { throw "Generated key has an unexpected shape (length $($key.Length))." }
$secret = Get-Document $default 'Secret' 'agenthub-api-key'
Assert-Contains $secret 'owner: "admin"'
Assert-Contains $default 'Auth__ApiKeyOwner: "admin"'
Assert-NotContains $default 'Auth__InsecureDevMode'
$backend = Get-Document $default 'Deployment' 'agenthub-backend'
Assert-Contains $backend '- name: Auth__ApiKey'
Assert-Contains $backend 'name: agenthub-api-key'
Assert-Contains $backend 'key: api-key'
Assert-Contains $backend 'checksum/api-key:'
# The key belongs in a secretKeyRef only: in the ConfigMap it would be readable by anyone who
# can read config, and in agenthub-secrets it would not follow an existing Secret's rotation.
Assert-NotContains (Get-Document $default 'ConfigMap' 'agenthub-config') $key
Assert-NotContains (Get-Document $default 'Secret' 'agenthub-secrets') 'Auth__ApiKey'

# Two renders without a cluster to read back from generate two different keys: the key is random,
# not derived from anything an attacker could know (release name, namespace).
if ((Get-GeneratedKey (Render)) -eq $key) { throw 'Two renders produced the same key.' }

# --- Upgrade of an install from before the key: its sessions belong to "dev". ---
$upgrade = Render @('--is-upgrade')
Assert-Contains (Get-Document $upgrade 'Secret' 'agenthub-api-key') 'owner: "dev"'
Assert-Contains $upgrade 'Auth__ApiKeyOwner: "dev"'

# An explicit owner wins on install and on upgrade alike.
$owned = Render @('--is-upgrade', '--set-string', 'auth.apiKey.owner=ops')
Assert-Contains (Get-Document $owned 'Secret' 'agenthub-api-key') 'owner: "ops"'
Assert-Contains $owned 'Auth__ApiKeyOwner: "ops"'

# `helm upgrade --reuse-values` from a release older than these values has no auth block at all;
# the chart has to fall back to the generated key instead of failing on a nil pointer.
$reused = Render @('--is-upgrade', '--set', 'auth=null')
Assert-Contains (Get-Document $reused 'Secret' 'agenthub-api-key') 'owner: "dev"'

# --- An existing Secret: referenced, never generated or copied. ---
$existing = Render @('--set-string', 'auth.apiKey.existingSecret=my-key', '--set-string', 'auth.apiKey.existingSecretKey=token')
if (Get-Document $existing 'Secret' 'agenthub-api-key') { throw 'An existing Secret must not be shadowed by a generated one.' }
$backend = Get-Document $existing 'Deployment' 'agenthub-backend'
Assert-Contains $backend 'name: my-key'
Assert-Contains $backend 'key: token'
Assert-Contains $existing 'Auth__ApiKeyOwner: "admin"'
Assert-Contains (Render @('--is-upgrade', '--set-string', 'auth.apiKey.existingSecret=my-key')) 'Auth__ApiKeyOwner: "dev"'

# --- OIDC: no key, no API-key settings. ---
$oidc = Render @('--set-string', 'oidc.authority=https://sso.example.com/realms/agenthub')
if (Get-Document $oidc 'Secret' 'agenthub-api-key') { throw 'OIDC installs must not get an API key.' }
Assert-NotContains $oidc 'Auth__ApiKey'
Assert-NotContains $oidc 'Auth__InsecureDevMode'
# insecureDevMode is ignored under OIDC, as the backend ignores it.
Assert-NotContains (Render @('--set-string', 'oidc.authority=https://sso.example.com/realms/agenthub', '--set', 'auth.insecureDevMode=true')) 'Auth__InsecureDevMode'

# --- Explicit insecure development mode: no key, the flag reaches the backend. ---
$insecure = Render @('--set', 'auth.insecureDevMode=true')
if (Get-Document $insecure 'Secret' 'agenthub-api-key') { throw 'Insecure mode must not generate a key.' }
Assert-Contains $insecure 'Auth__InsecureDevMode: "true"'
Assert-NotContains $insecure 'Auth__ApiKey'

# The Docker Desktop profile has no OIDC provider; without the opt-in its backend would not start.
$dev = Render @('--values', (Join-Path $chartPath 'values-dev.yaml'))
Assert-Contains $dev 'Auth__InsecureDevMode: "true"'

Write-Host 'API key chart assertions passed.'

# Empty strings must survive rendering: the backend uses them to trust nobody.
Assert-Contains $default 'ForwardedHeaders__KnownNetworks: "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"'
Assert-Contains $default 'ForwardedHeaders__KnownProxies: "127.0.0.1,::1"'
$proxies = Render @('--set-string', 'backend.forwardedHeaders.knownProxies=192.0.2.4', '--set-string', 'backend.forwardedHeaders.knownNetworks=198.51.100.0/24')
Assert-Contains $proxies 'ForwardedHeaders__KnownProxies: "192.0.2.4"'
Assert-Contains $proxies 'ForwardedHeaders__KnownNetworks: "198.51.100.0/24"'
$direct = Render @('--set-string', 'backend.forwardedHeaders.knownProxies=', '--set-string', 'backend.forwardedHeaders.knownNetworks=')
Assert-Contains $direct 'ForwardedHeaders__KnownProxies: ""'
Assert-Contains $direct 'ForwardedHeaders__KnownNetworks: ""'
$old = Render @('--is-upgrade', '--set', 'backend.forwardedHeaders=null')
Assert-Contains $old 'ForwardedHeaders__KnownNetworks: "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"'
