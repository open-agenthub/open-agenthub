[CmdletBinding()]
param(
    [switch]$NoPortForward,
    # Unset asks; the switches answer without a prompt so the script stays usable from CI.
    [switch]$WithObjectStorage,
    [switch]$WithoutObjectStorage
)

$ErrorActionPreference = 'Stop'

$releaseName = 'agenthub-dev'
$controlNamespace = 'agenthub-dev'
$sessionsNamespace = 'agenthub-dev-sessions'
$requiredContext = 'docker-desktop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$chartPath = Join-Path $repoRoot 'helm/open-agenthub'
$valuesPath = Join-Path $chartPath 'values-dev.yaml'
# Optional, gitignored personal overrides (git OAuth apps, Slack tokens, …).
$localValuesPath = Join-Path $chartPath 'values-dev.local.yaml'

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

function Assert-NativeSuccess([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

foreach ($command in @('docker', 'kubectl', 'helm')) {
    Require-Command $command
}

$currentContext = (kubectl config current-context 2>$null).Trim()
if ($currentContext -ne $requiredContext) {
    throw "Refusing to deploy: kubectl context '$currentContext' is not '$requiredContext'."
}

if (-not (Test-Path -LiteralPath $valuesPath)) {
    throw "Development values file not found: $valuesPath"
}

Write-Host 'Building local images...'
docker build --file (Join-Path $repoRoot 'backend/Dockerfile') --tag 'open-agenthub-dev/backend:local' $repoRoot
Assert-NativeSuccess 'Backend image build'
docker build --tag 'open-agenthub-dev/frontend:local' (Join-Path $repoRoot 'frontend')
Assert-NativeSuccess 'Frontend image build'
docker build --file (Join-Path $repoRoot 'agent-runtime/claude/Dockerfile') --tag 'open-agenthub-dev/agent-runtime-claude:local' (Join-Path $repoRoot 'agent-runtime')
Assert-NativeSuccess 'Claude image build'
docker build --file (Join-Path $repoRoot 'agent-runtime/codex/Dockerfile') --tag 'open-agenthub-dev/agent-runtime-codex:local' (Join-Path $repoRoot 'agent-runtime')
Assert-NativeSuccess 'Codex image build'
docker build --file (Join-Path $repoRoot 'agent-runtime/cursor/Dockerfile') --tag 'open-agenthub-dev/agent-runtime-cursor:local' (Join-Path $repoRoot 'agent-runtime')
Assert-NativeSuccess 'Cursor image build'
docker build --file (Join-Path $repoRoot 'agent-runtime/openclaw/Dockerfile') --tag 'open-agenthub-dev/agent-runtime-openclaw:local' (Join-Path $repoRoot 'agent-runtime')
Assert-NativeSuccess 'OpenClaw image build'
docker build --tag 'open-agenthub-dev/browser:local' (Join-Path $repoRoot 'browser-runtime')
Assert-NativeSuccess 'Browser image build'

$passwordBytes = $null
$encodedPassword = kubectl -n $controlNamespace get secret postgres-secret -o "jsonpath={.data.password}" 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($encodedPassword)) {
    try {
        $postgresPassword = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encodedPassword))
    } catch {
        throw 'The existing postgres-secret contains an invalid password value.'
    }
} else {
    helm status $releaseName --namespace $controlNamespace *> $null
    if ($LASTEXITCODE -eq 0) {
        throw 'The existing Helm release is missing postgres-secret; refusing to rotate the database password.'
    }

    $passwordBytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
    $postgresPassword = [Convert]::ToHexString($passwordBytes).ToLowerInvariant()
}

function New-HexSecret([int]$Bytes) {
    $buffer = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return [Convert]::ToHexString($buffer).ToLowerInvariant()
}

# Reads a value out of an existing secret; empty when the secret, the key, or the value
# itself is absent. Credentials are never regenerated on a redeploy: a new access key would
# leave every object already in the bucket unreachable, and the hub would report that as
# missing session state rather than as a credential it no longer has.
function Get-ExistingSecretValue([string]$Secret, [string]$Key) {
    $encoded = kubectl -n $controlNamespace get secret $Secret -o "jsonpath={.data.$Key}" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($encoded)) { return '' }
    try { return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) } catch { return '' }
}

# Object storage: offered rather than assumed. Without it the hub runs, but a session
# cannot be resumed in a fresh pod — its state archive has nowhere to live.
if ($WithObjectStorage -and $WithoutObjectStorage) {
    throw 'Pass either -WithObjectStorage or -WithoutObjectStorage, not both.'
}
$objectStorage = $null
if ($WithObjectStorage) { $objectStorage = $true }
if ($WithoutObjectStorage) { $objectStorage = $false }
if ($null -eq $objectStorage) {
    kubectl -n $controlNamespace get statefulset garage *> $null
    if ($LASTEXITCODE -eq 0) {
        $objectStorage = $true
    } else {
        Write-Host 'Deploy object storage (Garage) into the cluster as well?'
        Write-Host 'Without it, session state, uploads and artifacts have nowhere to be stored.'
        $answer = Read-Host 'Deploy it? [Y/n]'
        $objectStorage = $answer -notmatch '^[nN]'
    }
}

$objectStorageArgs = @()
if ($objectStorage) {
    $garageAccessKey = Get-ExistingSecretValue 'agenthub-secrets' 'S3__AccessKey'
    $garageSecretKey = Get-ExistingSecretValue 'agenthub-secrets' 'S3__SecretKey'
    $garageRpcSecret = Get-ExistingSecretValue 'garage-secrets' 'rpc_secret'
    $garageAdminToken = Get-ExistingSecretValue 'garage-secrets' 'admin_token'
    # Garage only accepts an access key id shaped like its own: GK plus 24 hex characters.
    if (-not $garageAccessKey) { $garageAccessKey = "GK$(New-HexSecret 12)" }
    if (-not $garageSecretKey) { $garageSecretKey = New-HexSecret 32 }
    if (-not $garageRpcSecret) { $garageRpcSecret = New-HexSecret 32 }
    if (-not $garageAdminToken) { $garageAdminToken = New-HexSecret 16 }
    $objectStorageArgs = @(
        '--set', 'objectStorage.enabled=true',
        '--set-string', "objectStorage.accessKey=$garageAccessKey",
        '--set-string', "objectStorage.secretKey=$garageSecretKey",
        '--set-string', "objectStorage.rpcSecret=$garageRpcSecret",
        '--set-string', "objectStorage.adminToken=$garageAdminToken"
    )
}

try {
    Write-Host 'Deploying the development release...'
    $helmArgs = @(
        'upgrade', '--install', $releaseName, $chartPath,
        '--namespace', $controlNamespace,
        '--create-namespace',
        '--values', $valuesPath
    )
    if (Test-Path -LiteralPath $localValuesPath) {
        Write-Host "Applying local overrides from $localValuesPath"
        $helmArgs += @('--values', $localValuesPath)
    }
    $helmArgs += $objectStorageArgs
    $helmArgs += @(
        '--set', "sessionsNamespace=$sessionsNamespace",
        '--set-string', "postgres.password=$postgresPassword"
    )
    helm @helmArgs
    Assert-NativeSuccess 'Helm deployment'

    kubectl -n $controlNamespace rollout status statefulset/postgres --timeout=180s
    Assert-NativeSuccess 'Postgres rollout'

    # Garage creates nothing by itself: a fresh node has no layout, no bucket and no key,
    # and its image has no shell for a bootstrap job to use. Each step is skipped when it
    # is already done, so a redeploy costs nothing.
    if ($objectStorage) {
        kubectl -n $controlNamespace rollout status statefulset/garage --timeout=180s
        Assert-NativeSuccess 'Garage rollout'
        function Invoke-Garage { kubectl -n $controlNamespace exec garage-0 -- /garage @args }

        $buckets = (Invoke-Garage bucket list 2>$null) -join "`n"
        if ($buckets -match '\sagenthub\s') {
            Write-Host 'Object storage already initialised.'
        } else {
            Write-Host 'Initialising object storage...'
            $layout = ((Invoke-Garage layout show 2>$null) -join "`n")
            $currentVersion = 0
            if ($layout -match 'Current cluster layout version: (\d+)') {
                $currentVersion = [int]$Matches[1]
            }
            if ($currentVersion -lt 1) {
                $nodeId = ((Invoke-Garage node id -q 2>$null) -join '').Trim().Split('@')[0]
                if (-not $nodeId) { throw 'Could not read the Garage node id; object storage is not initialised.' }
                Invoke-Garage layout assign -z dc1 -c 18GB $nodeId
                Assert-NativeSuccess 'Garage layout assign'
                # The version to apply is always one past the current one; parsing it out of
                # the hint Garage prints would tie this to that sentence's wording.
                Invoke-Garage layout apply --version ($currentVersion + 1)
                Assert-NativeSuccess 'Garage layout apply'
            }
            Invoke-Garage bucket create agenthub
            Assert-NativeSuccess 'Garage bucket create'
            Invoke-Garage key import --yes $garageAccessKey $garageSecretKey -n agenthub-key
            Assert-NativeSuccess 'Garage key import'
            Invoke-Garage bucket allow --read --write --owner agenthub --key agenthub-key
            Assert-NativeSuccess 'Garage bucket allow'
            Write-Host "Object storage ready: bucket agenthub on garage.$controlNamespace.svc.cluster.local:3900"
        }
    }
    kubectl -n $controlNamespace rollout restart deployment/agenthub-backend deployment/agenthub-frontend
    Assert-NativeSuccess 'Backend rollout restart'
    kubectl -n $controlNamespace rollout status deployment/agenthub-backend --timeout=180s
    Assert-NativeSuccess 'Backend rollout'
    kubectl -n $controlNamespace rollout status deployment/agenthub-frontend --timeout=180s
    Assert-NativeSuccess 'Frontend rollout'

    $backendForward = $null
    $frontendForward = $null
    try {
        $backendForward = Start-Process kubectl -ArgumentList @('-n', $controlNamespace, 'port-forward', 'svc/agenthub-backend', '18080:80') -PassThru -WindowStyle Hidden
        $backendHealthy = $false
        1..30 | ForEach-Object {
            if (-not $backendHealthy) {
                try {
                    $response = Invoke-WebRequest -Uri 'http://127.0.0.1:18080/healthz' -UseBasicParsing -TimeoutSec 2
                    $backendHealthy = $response.StatusCode -eq 200
                } catch {
                    Start-Sleep -Seconds 1
                }
            }
        }
        if (-not $backendHealthy) {
            throw 'Backend health check failed at http://127.0.0.1:18080/healthz.'
        }

        Stop-Process -Id $backendForward.Id -Force -ErrorAction SilentlyContinue
        $backendForward = $null

        $frontendForward = Start-Process kubectl -ArgumentList @('-n', $controlNamespace, 'port-forward', 'svc/agenthub-frontend', '18081:80') -PassThru -WindowStyle Hidden
        $frontendHealthy = $false
        1..30 | ForEach-Object {
            if (-not $frontendHealthy) {
                try {
                    $response = Invoke-WebRequest -Uri 'http://127.0.0.1:18081/' -UseBasicParsing -TimeoutSec 2
                    $frontendHealthy = $response.StatusCode -eq 200
                } catch {
                    Start-Sleep -Seconds 1
                }
            }
        }
        if (-not $frontendHealthy) {
            throw 'Frontend service check failed at http://127.0.0.1:18081/.'
        }
    } finally {
        foreach ($forward in @($backendForward, $frontendForward)) {
            if ($null -ne $forward -and -not $forward.HasExited) {
                Stop-Process -Id $forward.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host 'Development release is ready.'
    Write-Host 'Control namespace: agenthub-dev'
    Write-Host 'Sessions namespace: agenthub-dev-sessions'
    Write-Host '  Logs: kubectl -n agenthub-dev logs deployment/agenthub-backend --follow'
    Write-Host '  Redeploy: .\setup-dev.ps1 -NoPortForward'
    Write-Host '  Uninstall: helm uninstall agenthub-dev -n agenthub-dev'
    Write-Host '  Remove sessions: kubectl delete namespace agenthub-dev-sessions'
    if (-not $NoPortForward) {
        Write-Host 'Serving the frontend at http://localhost:8080. Press Ctrl+C to stop.'
        kubectl -n $controlNamespace port-forward svc/agenthub-frontend 8080:80
    } else {
        Write-Host 'Port-forward skipped (-NoPortForward).'
        Write-Host 'Run: kubectl -n agenthub-dev port-forward svc/agenthub-frontend 8080:80'
    }
} finally {
    $postgresPassword = $null
    if ($null -ne $passwordBytes) {
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    }
}
