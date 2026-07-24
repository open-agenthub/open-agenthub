[CmdletBinding()]
param(
    [switch]$NoPortForward
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
    $helmArgs += @(
        '--set', "sessionsNamespace=$sessionsNamespace",
        '--set-string', "postgres.password=$postgresPassword"
    )
    helm @helmArgs
    Assert-NativeSuccess 'Helm deployment'

    kubectl -n $controlNamespace rollout status statefulset/postgres --timeout=180s
    Assert-NativeSuccess 'Postgres rollout'
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
