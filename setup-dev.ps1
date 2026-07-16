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

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
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
docker build --tag 'open-agenthub-dev/backend:local' (Join-Path $repoRoot 'backend')
docker build --tag 'open-agenthub-dev/frontend:local' (Join-Path $repoRoot 'frontend')
docker build --tag 'open-agenthub-dev/agent-runtime:local' (Join-Path $repoRoot 'agent-runtime')

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
    helm upgrade --install $releaseName $chartPath `
        --namespace $controlNamespace `
        --create-namespace `
        --values $valuesPath `
        --set "sessionsNamespace=$sessionsNamespace" `
        --set-string "postgres.password=$postgresPassword"

    kubectl -n $controlNamespace rollout status statefulset/postgres --timeout=180s
    kubectl -n $controlNamespace rollout restart deployment/agenthub-backend deployment/agenthub-frontend
    kubectl -n $controlNamespace rollout status deployment/agenthub-backend --timeout=180s
    kubectl -n $controlNamespace rollout status deployment/agenthub-frontend --timeout=180s

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
