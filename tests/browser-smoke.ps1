[CmdletBinding()]
param(
    [string]$ControlNamespace = 'agenthub-dev',
    [string]$SessionsNamespace = 'agenthub-dev-sessions',
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-LastExitCode([string]$Message) {
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Timeout = $TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    do {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $headers = @{ 'X-AgentHub-Test-User' = 'browser-smoke' }
    $params = @{ Method = $Method; Uri = "$script:ApiBase$Path"; Headers = $headers }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    Invoke-RestMethod @params
}

function Get-BrowserPhase([string]$Id) {
    try { return (Invoke-Api GET "/api/sessions/$Id").browser.phase }
    catch { return $null }
}

function Wait-AgentReady([string]$PodName) {
    & kubectl -n $SessionsNamespace wait --for=condition=Ready "pod/$PodName" --timeout="$($TimeoutSeconds)s" | Out-Null
    Assert-LastExitCode "Agent pod $PodName did not become Ready."
}

function Get-AgentValue([string]$PodName, [string]$Variable) {
    $value = & kubectl -n $SessionsNamespace exec $PodName -c agent -- printenv $Variable
    Assert-LastExitCode "Could not read $Variable through the in-cluster test fixture."
    return ($value | Out-String).Trim()
}

function Start-BrowserFromAgent([string]$PodName) {
    $status = & kubectl -n $SessionsNamespace exec $PodName -c agent -- sh -c 'curl -sS -o /tmp/agenthub-browser-smoke.json -w "%{http_code}" -X POST -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" "$AGENTHUB_CALLBACK_URL/browser"'
    Assert-LastExitCode 'The matching agent pod could not call browser lifecycle.'
    if (($status | Out-String).Trim() -ne '200') { throw "Matching agent lifecycle call returned $status instead of 200." }
    Wait-Until { (Get-BrowserPhase $script:SessionId) -eq 'Running' } 'browser phase Running'
}

function Invoke-ForeignLifecycle([string]$PodName, [string]$Url, [string]$Token = '') {
    if ([string]::IsNullOrEmpty($Token)) {
        $status = & kubectl -n $SessionsNamespace exec $PodName -- curl -sS -o /dev/null -w '%{http_code}' -X POST $Url
    } else {
        $curlConfig = "header = `"X-Agent-Token: $Token`"`n"
        $status = $curlConfig | & kubectl -n $SessionsNamespace exec -i $PodName -- curl -sS -o /dev/null -w '%{http_code}' -X POST --config - $Url
    }
    Assert-LastExitCode 'Foreign lifecycle probe could not be executed.'
    return ($status | Out-String).Trim()
}

function Assert-BrowserWebSocket([string]$Id) {
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.SetRequestHeader('X-AgentHub-Test-User', 'browser-smoke')
    $socket.Options.AddSubProtocol('binary')
    $uri = [Uri]("ws://127.0.0.1:$script:LocalPort/ws/sessions/$Id/browser")
    $cts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        $socket.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
        if ($socket.State -ne [Net.WebSockets.WebSocketState]::Open) { throw 'Browser WebSocket did not open.' }
        $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'smoke complete', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    } finally {
        $cts.Dispose()
        $socket.Dispose()
    }
}

function Assert-CookieRoundTrip([string]$AgentPod, [string]$BrowserIp) {
    $cookieScript = @"
import { chromium } from '/opt/session-agent/node_modules/playwright-core/index.mjs';
const browser = await chromium.connectOverCDP('http://${BrowserIp}:9222');
const context = browser.contexts()[0];
await context.addCookies([{name:'agenthub_browser_smoke',value:'restored',domain:'example.test',path:'/',expires:Math.floor(Date.now()/1000)+600,httpOnly:true,secure:false,sameSite:'Lax'}]);
process.exit(0);
"@
    & kubectl -n $SessionsNamespace exec $AgentPod -c agent -- node --input-type=module -e $cookieScript
    Assert-LastExitCode 'Could not seed the browser cookie through CDP.'
}

function Assert-RestoredCookie([string]$AgentPod, [string]$BrowserIp) {
    $cookieScript = @"
import { chromium } from '/opt/session-agent/node_modules/playwright-core/index.mjs';
const browser = await chromium.connectOverCDP('http://${BrowserIp}:9222');
const cookies = await browser.contexts()[0].cookies();
process.exit(cookies.some(c => c.name === 'agenthub_browser_smoke' && c.value === 'restored') ? 0 : 7);
"@
    & kubectl -n $SessionsNamespace exec $AgentPod -c agent -- node --input-type=module -e $cookieScript
    Assert-LastExitCode 'The test cookie was not restored after resume.'
}

$context = (& kubectl config current-context | Out-String).Trim()
Assert-LastExitCode 'Could not read the Kubernetes context.'
if ($context -ne 'docker-desktop') { throw "Refusing Kubernetes context '$context'; expected docker-desktop." }

& kubectl cluster-info --request-timeout=10s | Out-Null
Assert-LastExitCode 'Docker Desktop Kubernetes is unavailable.'

$script:SessionId = $null
$foreignPod = $null
$forward = $null
$callbackToken = $null
$script:LocalPort = Get-FreeTcpPort
$script:ApiBase = "http://127.0.0.1:$script:LocalPort"
$forwardOut = Join-Path ([IO.Path]::GetTempPath()) "agenthub-browser-forward-$PID.log"
$forwardErr = Join-Path ([IO.Path]::GetTempPath()) "agenthub-browser-forward-$PID.err.log"

try {
    $forward = Start-Process kubectl -ArgumentList @('-n', $ControlNamespace, 'port-forward', 'svc/agenthub-backend', "$script:LocalPort`:80") -RedirectStandardOutput $forwardOut -RedirectStandardError $forwardErr -PassThru -WindowStyle Hidden
    Wait-Until {
        try { Invoke-WebRequest -Uri "$script:ApiBase/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null; $true }
        catch { $false }
    } 'backend port-forward' 30

    $session = Invoke-Api POST '/api/sessions' @{
        title = 'Integrated browser smoke'
        mode = 'Interactive'
        agent = 'Claude'
        authMode = 'Subscription'
    }
    $script:SessionId = $session.id
    if ([string]::IsNullOrWhiteSpace($script:SessionId)) { throw 'Session API returned no id.' }
    $agentPod = "session-$script:SessionId"
    Wait-AgentReady $agentPod

    # The companion workspace must be available alongside the browser for every session.
    $fileCapabilities = Invoke-Api GET "/api/sessions/$script:SessionId/files/capabilities"
    if ($fileCapabilities.directPreviewMimeTypes -notcontains 'image/png') {
        throw 'The Files workspace does not advertise direct image previews.'
    }
    if ($fileCapabilities.typeSupport.'text/html' -ne 'download_only') {
        throw 'The Files workspace must keep HTML download-only.'
    }

    $callbackToken = Get-AgentValue $agentPod 'AGENTHUB_CALLBACK_TOKEN'
    $callbackUrl = Get-AgentValue $agentPod 'AGENTHUB_CALLBACK_URL'
    if ([string]::IsNullOrWhiteSpace($callbackToken)) { throw 'The in-cluster callback token fixture returned an empty value.' }

    Start-BrowserFromAgent $agentPod
    $browserPod = "browser-$script:SessionId"
    $browserIp = (& kubectl -n $SessionsNamespace get pod $browserPod -o 'jsonpath={.status.podIP}' | Out-String).Trim()
    Assert-LastExitCode 'Could not resolve browser pod IP.'

    $foreignPod = "browser-smoke-foreign-$($script:SessionId.Substring(0, [Math]::Min(8, $script:SessionId.Length)))"
    $fixture = @"
apiVersion: v1
kind: Pod
metadata:
  name: $foreignPod
  namespace: $SessionsNamespace
  labels:
    agenthub.dev/session: $script:SessionId
    agenthub.dev/component: agent
spec:
  automountServiceAccountToken: false
  restartPolicy: Never
  securityContext:
    runAsNonRoot: true
    runAsUser: 101
    runAsGroup: 102
    seccompProfile: { type: RuntimeDefault }
  containers:
    - name: probe
      image: curlimages/curl:8.12.1
      command: ["sh", "-c", "sleep 600"]
      securityContext:
        allowPrivilegeEscalation: false
        readOnlyRootFilesystem: true
        capabilities: { drop: ["ALL"] }
"@
    $fixture | & kubectl apply -f - | Out-Null
    Assert-LastExitCode 'Could not create the foreign security fixture.'
    & kubectl -n $SessionsNamespace wait --for=condition=Ready "pod/$foreignPod" --timeout='120s' | Out-Null
    Assert-LastExitCode 'Foreign security fixture did not become Ready.'

    $lifecycleUrl = "$callbackUrl/browser"
    if ((Invoke-ForeignLifecycle $foreignPod $lifecycleUrl) -ne '401') { throw 'Foreign pod without token was not rejected with 401.' }
    if ((Invoke-ForeignLifecycle $foreignPod $lifecycleUrl $callbackToken) -ne '401') { throw 'Foreign pod with the callback token was not rejected with 401.' }

    & kubectl -n $SessionsNamespace exec $agentPod -c agent -- curl -fsS --max-time 5 "http://$browserIp`:9222/json/version" | Out-Null
    Assert-LastExitCode 'Matching agent pod could not reach browser CDP.'
    & kubectl -n $SessionsNamespace exec $foreignPod -- curl -fsS --max-time 3 "http://$browserIp`:9222/json/version" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Foreign pod unexpectedly reached browser CDP.' }

    Assert-BrowserWebSocket $script:SessionId

    $s3Key = (& kubectl -n $ControlNamespace get secret agenthub-secrets -o 'jsonpath={.data.S3__AccessKey}' | Out-String).Trim()
    $verifyCookies = -not [string]::IsNullOrWhiteSpace($s3Key)
    $s3Key = $null
    if ($verifyCookies) { Assert-CookieRoundTrip $agentPod $browserIp }

    Invoke-Api POST "/api/sessions/$script:SessionId/pause" | Out-Null
    Wait-Until {
        $podCount = (& kubectl -n $SessionsNamespace get pod -l "agenthub.dev/session=$script:SessionId,agenthub.dev/component=browser" --no-headers 2>$null | Measure-Object -Line).Lines
        $policyCount = (& kubectl -n $SessionsNamespace get networkpolicy -l "agenthub.dev/session=$script:SessionId,agenthub.dev/browser-resource=true" --no-headers 2>$null | Measure-Object -Line).Lines
        $podCount -eq 0 -and $policyCount -eq 0 -and (Get-BrowserPhase $script:SessionId) -eq 'Stopped'
    } 'browser pod, policy, and lease cleanup'

    if ($verifyCookies) {
        Invoke-Api POST "/api/sessions/$script:SessionId/resume" | Out-Null
        Wait-AgentReady $agentPod
        $callbackToken = Get-AgentValue $agentPod 'AGENTHUB_CALLBACK_TOKEN'
        Start-BrowserFromAgent $agentPod
        $browserIp = (& kubectl -n $SessionsNamespace get pod $browserPod -o 'jsonpath={.status.podIP}' | Out-String).Trim()
        Assert-RestoredCookie $agentPod $browserIp
        Write-Host 'S3 cookie restore: passed'
    } else {
        Write-Host 'S3 cookie restore: skipped (S3 credentials are not configured)'
    }

    Write-Host 'Integrated browser Docker Desktop smoke: passed'
} finally {
    $callbackToken = $null
    if ($foreignPod) { & kubectl -n $SessionsNamespace delete pod $foreignPod --ignore-not-found --wait=false 2>$null | Out-Null }
    if ($script:SessionId) {
        try { Invoke-Api DELETE "/api/sessions/$script:SessionId" | Out-Null } catch { Write-Warning 'Smoke session cleanup through the API failed.' }
    }
    if ($forward -and -not $forward.HasExited) { Stop-Process -Id $forward.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $forwardOut, $forwardErr -Force -ErrorAction SilentlyContinue
}
