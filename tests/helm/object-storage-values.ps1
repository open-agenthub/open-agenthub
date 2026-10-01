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

Write-Host 'object storage chart assertions passed'
