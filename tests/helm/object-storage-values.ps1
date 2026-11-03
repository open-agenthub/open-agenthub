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

# Off by default, and nothing of it leaks into a deployment that points s3.* elsewhere.
$default = Render
Assert-NotContains $default 'name: garage'
Assert-NotContains $default 'dxflrs/garage'
Assert-Contains $default 'S3__ServiceUrl: ""'

$credentials = @(
    '--set', 'objectStorage.enabled=true',
    '--set-string', 'objectStorage.accessKey=GK0123456789abcdef01234567',
    '--set-string', 'objectStorage.secretKey=secret-only',
    '--set-string', 'objectStorage.rpcSecret=rpc-only',
    '--set-string', 'objectStorage.adminToken=admin-only'
)

$enabled = Render $credentials
foreach ($expected in @(
    'image: dxflrs/garage:v1.0.1',
    'serviceName: garage',
    # The hub is wired to the in-cluster store without repeating the values by hand.
    'S3__ServiceUrl: "http://garage.agenthub-test.svc.cluster.local:3900"',
    'S3__AccessKey: "GK0123456789abcdef01234567"',
    'S3__SecretKey: "secret-only"',
    'S3__Bucket: "agenthub"',
    'rpc_secret: "rpc-only"',
    'admin_token: "admin-only"',
    # The region Garage signs with has to be the region the hub signs with, or every
    # request fails with what looks like a credentials error.
    's3_region = "us-east-1"',
    'replication_factor = 1',
    # A session restores its own state from the presigned url, so the sessions namespace
    # needs egress to the store's port.
    '- { protocol: TCP, port: 3900 }'
)) { Assert-Contains $enabled $expected }

# Pointing s3.* somewhere explicitly wins over the in-cluster default: that is how an
# existing deployment is moved to another provider without tearing the store down first.
$overridden = Render ($credentials + @(
    '--set-string', 's3.serviceUrl=https://s3.example.com',
    '--set-string', 's3.accessKey=external-key',
    '--set-string', 's3.secretKey=external-secret'
))
Assert-Contains $overridden 'S3__ServiceUrl: "https://s3.example.com"'
Assert-Contains $overridden 'S3__AccessKey: "external-key"'
Assert-Contains $overridden 'S3__SecretKey: "external-secret"'
Assert-Contains $overridden 'serviceName: garage'

# The region follows s3.region into the server's own config.
$region = Render ($credentials + @('--set-string', 's3.region=eu-central-1'))
Assert-Contains $region 's3_region = "eu-central-1"'

# Persistence claims a volume for metadata and one for data; without it both are emptyDir,
# which is what a throwaway dev cluster wants.
Assert-Contains $enabled 'metadata: { name: meta }'
Assert-Contains $enabled 'storage: 20Gi'
$ephemeral = Render ($credentials + @('--set', 'objectStorage.persistence=false'))
Assert-NotContains $ephemeral 'metadata: { name: meta }'
Assert-Contains $ephemeral 'mountPath: /var/lib/garage/meta'
Assert-Contains $ephemeral "        - name: meta`n          emptyDir: {}"

# A missing credential has to fail the render, not deploy a store nothing can sign against.
foreach ($missing in @('objectStorage.accessKey', 'objectStorage.rpcSecret', 'objectStorage.adminToken')) {
    $filtered = @('--set', 'objectStorage.enabled=true')
    foreach ($pair in @(
        @('objectStorage.accessKey', 'GK0123456789abcdef01234567'),
        @('objectStorage.secretKey', 'secret-only'),
        @('objectStorage.rpcSecret', 'rpc-only'),
        @('objectStorage.adminToken', 'admin-only'))) {
        if ($pair[0] -ne $missing) { $filtered += @('--set-string', "$($pair[0])=$($pair[1])") }
    }
    $failed = $false
    try { Render $filtered | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Chart rendered without $missing" }
}

# Inspect the storage policy separately so another resource cannot satisfy an assertion.
function Storage-Policy([string]$Text) {
    return (($Text -split '(?m)^---$' | Where-Object { $_.Contains('name: allow-agent-to-object-storage') }) -join "`n")
}
Assert-NotContains $default 'name: allow-agent-to-object-storage'
$bundledPolicy = Storage-Policy $enabled
Assert-Contains $bundledPolicy 'kubernetes.io/metadata.name: "agenthub-test"'
Assert-Contains $bundledPolicy 'matchLabels: { app: garage }'
Assert-Contains $bundledPolicy 'port: 3900'
Assert-NotContains $bundledPolicy 'to: []'
$generalPolicy = ($enabled -split '(?m)^---$' | Where-Object { $_.Contains('name: allow-agent-egress') }) -join "`n"
Assert-NotContains $generalPolicy 'port: 3900'

foreach ($endpoint in @(
    @('http://192.0.2.10:9000', '9000', 'cidr: "192.0.2.10/32"'),
    @('http://[2001:db8::1]:9000', '9000', 'cidr: "2001:db8::1/128"'),
    @('http://[2001:db8::1]:9100', '9100', 'cidr: "2001:db8::1/128"')
)) {
    $policy = Storage-Policy (Render @('--set-string', "s3.serviceUrl=$($endpoint[0])"))
    Assert-Contains $policy "port: $($endpoint[1])"
    Assert-Contains $policy $endpoint[2]
    Assert-NotContains $policy 'app: garage'
}
# An explicit endpoint wins over the bundled default in configuration and egress alike.
Assert-NotContains $overridden 'name: allow-agent-to-object-storage'
Assert-NotContains (Storage-Policy $overridden) 'app: garage'
$extra = Render @('--set', 'agent.extraEgressPorts[0]=9001')
Assert-Contains $extra 'port: 9001'
Assert-NotContains $extra 'name: allow-agent-to-object-storage'

foreach ($endpoint in @('http://storage.example.com:9000', 'https://storage.example.com', 'http://storage.example.com', 'https://[2001:db8::1]')) {
    Assert-NotContains (Render @('--set-string', "s3.serviceUrl=$endpoint")) 'name: allow-agent-to-object-storage'
}
$scoped = Storage-Policy (Render @('--set-string', 's3.serviceUrl=http://storage.example.com:9000', '--set-string', 'agent.storageEgress.cidrs[0]=192.0.2.0/24'))
Assert-Contains $scoped 'port: 9000'
Assert-Contains $scoped 'cidr: "192.0.2.0/24"'
Assert-NotContains $scoped 'to: []'
# NOTES is hidden by helm template; wrap the real template in a ConfigMap in a
# temporary chart so warning behavior is verified without needing a cluster.
$temporaryChart = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
try {
    Copy-Item $chartPath $temporaryChart -Recurse
    $notes = Get-Content (Join-Path $temporaryChart 'templates/NOTES.txt') -Raw
    $wrapper = '{{ define "storage-proof-notes" }}' + $notes + '{{ end }}' + "`n" + @'
apiVersion: v1
kind: ConfigMap
metadata:
  name: storage-proof-notes
data:
  notes: {{ include "storage-proof-notes" . | quote }}
'@
    Set-Content (Join-Path $temporaryChart 'templates/storage-proof-notes.yaml') $wrapper
    function Render-Notes([string[]]$Arguments = @()) {
        $output = & helm template agenthub-test $temporaryChart --set-string postgres.password=test-only --show-only templates/storage-proof-notes.yaml @Arguments
        if ($LASTEXITCODE -ne 0) { throw 'Notes rendering failed' }
        return $output -join "`n"
    }
    $endpointArgs = @('--set-string', 's3.serviceUrl=http://storage.example.com:9000')
    Assert-Contains (Render-Notes $endpointArgs) 'WARNING: Storage endpoint storage.example.com uses TCP 9000'
    Assert-NotContains (Render-Notes ($endpointArgs + @('--set-string', 'agent.storageEgress.cidrs[0]=192.0.2.10/32'))) 'WARNING: Storage endpoint'
    Assert-NotContains (Render-Notes ($endpointArgs + @('--set', 'agent.extraEgressPorts[0]=9000'))) 'WARNING: Storage endpoint'
    Assert-NotContains (Render-Notes @('--set-string', 's3.serviceUrl=https://storage.example.com')) 'WARNING: Storage endpoint'
    Assert-NotContains (Render-Notes @('--set-string', 's3.serviceUrl=http://192.0.2.10:9000')) 'WARNING: Storage endpoint'
    Assert-NotContains (Render-Notes) 'WARNING: Storage endpoint'
} finally {
    Remove-Item $temporaryChart -Recurse -Force
}
Write-Host 'object storage chart assertions passed'
