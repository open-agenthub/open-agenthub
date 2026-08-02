[CmdletBinding()]
param(
    [string]$ControlNamespace = 'agenthub-dev',
    [string]$SessionsNamespace = 'agenthub-dev-sessions',
    [int]$TimeoutSeconds = 240,
    [switch]$RequireCluster
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-LastExitCode([string]$Message) {
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$Timeout = $TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    do {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $params = @{
        Method = $Method
        Uri = "$script:ApiBase$Path"
        Headers = @{ 'X-AgentHub-Test-User' = 'files-smoke' }
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    Invoke-RestMethod @params
}

function New-Fixtures([string]$Root) {
    $png = [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==')
    [IO.File]::WriteAllBytes((Join-Path $Root 'BLUE-47.png'), $png)
    [IO.File]::WriteAllText((Join-Path $Root 'note.md'), "# BLUE-47`n`nDeterministic Markdown fixture.")
    [IO.File]::WriteAllText((Join-Path $Root 'plain.txt'), 'BLUE-47')
    [IO.File]::WriteAllText((Join-Path $Root 'unsafe.html'), '<strong>BLUE-47</strong>')
    [IO.File]::WriteAllText((Join-Path $Root 'unsafe.svg'), '<svg xmlns="http://www.w3.org/2000/svg"><text>BLUE-47</text></svg>')
    return Get-ChildItem -LiteralPath $Root -File
}

function Upload-File([string]$SessionId, [IO.FileInfo]$Fixture, [string]$MimeType) {
    $reserved = Invoke-Api POST "/api/sessions/$SessionId/files/reserve" @{
        name = $Fixture.Name
        mimeType = $MimeType
        size = $Fixture.Length
    }
    $uploadUri = [Uri]::new([Uri]$script:ApiBase, $reserved.upload.url)
    $headers = @{}
    foreach ($property in $reserved.upload.headers.PSObject.Properties) {
        $headers[$property.Name] = [string]$property.Value
    }
    Invoke-WebRequest -Method PUT -Uri $uploadUri -Headers $headers -ContentType $MimeType -InFile $Fixture.FullName | Out-Null
    return Invoke-Api POST "/api/sessions/$SessionId/files/$($reserved.file.id)/complete"
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "agenthub-files-smoke-$PID"
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$fixtures = New-Fixtures $fixtureRoot
if ($fixtures.Count -ne 5) { throw 'Deterministic fixture generation failed.' }

$context = (& kubectl config current-context 2>$null | Out-String).Trim()
$clusterOutput = & kubectl cluster-info --request-timeout=5s 2>&1
$clusterReady = $LASTEXITCODE -eq 0 -and $context -eq 'docker-desktop'
if (-not $clusterReady) {
    if ($RequireCluster) { throw "Docker Desktop Kubernetes is unavailable: $clusterOutput" }
    Write-Output 'Session file live smoke: skipped (Docker Desktop Kubernetes unavailable). Fixture generation passed.'
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    exit 0
}

$script:SessionId = $null
$forward = $null
$script:LocalPort = Get-FreeTcpPort
$script:ApiBase = "http://127.0.0.1:$script:LocalPort"
$forwardOut = Join-Path $fixtureRoot 'port-forward.log'
$forwardErr = Join-Path $fixtureRoot 'port-forward.err.log'

try {
    $forward = Start-Process kubectl -ArgumentList @(
        '-n', $ControlNamespace, 'port-forward', 'svc/agenthub-backend', "$script:LocalPort`:80"
    ) -RedirectStandardOutput $forwardOut -RedirectStandardError $forwardErr -PassThru -WindowStyle Hidden
    Wait-Until {
        try { Invoke-WebRequest "$script:ApiBase/healthz" -TimeoutSec 2 | Out-Null; $true }
        catch { $false }
    } 'backend port-forward' 30

    $session = Invoke-Api POST '/api/sessions' @{
        title = 'Session files smoke'
        mode = 'Interactive'
        agent = 'Claude'
        authMode = 'Subscription'
    }
    $script:SessionId = $session.id
    $pod = "session-$script:SessionId"
    & kubectl -n $SessionsNamespace wait --for=condition=Ready "pod/$pod" --timeout="$($TimeoutSeconds)s" | Out-Null
    Assert-LastExitCode 'Agent pod did not become Ready.'

    $capabilities = Invoke-Api GET "/api/sessions/$script:SessionId/files/capabilities"
    if ($capabilities.directPreviewMimeTypes -notcontains 'image/png') { throw 'PNG preview capability is missing.' }
    if ($capabilities.unavailableReasons.'text/html' -ne 'download_only') { throw 'HTML must be download-only.' }
    if ($capabilities.unavailableReasons.'image/svg+xml' -ne 'download_only') { throw 'SVG must be download-only.' }

    $image = Upload-File $script:SessionId ($fixtures | Where-Object Name -eq 'BLUE-47.png') 'image/png'
    $listed = Invoke-Api GET "/api/sessions/$script:SessionId/files"
    if ($listed.id -notcontains $image.id) { throw 'Uploaded image is missing from the file list.' }

    $token = (& kubectl -n $SessionsNamespace exec $pod -c agent -- printenv AGENTHUB_CALLBACK_TOKEN | Out-String).Trim()
    $callback = (& kubectl -n $SessionsNamespace exec $pod -c agent -- printenv AGENTHUB_CALLBACK_URL | Out-String).Trim()
    $presentBody = @{ fileId = $image.id } | ConvertTo-Json -Compress
    $presentBody | & kubectl -n $SessionsNamespace exec -i $pod -c agent -- curl -fsS -X PUT -H "X-Agent-Token: $token" -H 'Content-Type: application/json' --data-binary '@-' "$callback/files/presentation" | Out-Null
    Assert-LastExitCode 'Managed files callback could not present the image.'

    $presentation = Invoke-Api GET "/api/sessions/$script:SessionId/files/presentation"
    if ($presentation.fileId -ne $image.id -or $presentation.revision -lt 1) {
        throw 'Presented file did not reach shared UI state.'
    }
    $download = Invoke-WebRequest -Uri "$script:ApiBase/api/sessions/$script:SessionId/files/$($image.id)/content" -Headers @{ 'X-AgentHub-Test-User' = 'files-smoke' }
    if ($download.RawContentLength -ne $image.size) { throw 'Authorized content bytes do not match metadata.' }

    Write-Output "Session file live smoke passed in '$($capabilities.storageMode)' storage mode."
} finally {
    if ($script:SessionId) {
        try { Invoke-Api DELETE "/api/sessions/$script:SessionId" | Out-Null } catch { Write-Warning 'Smoke session cleanup failed.' }
    }
    if ($forward -and -not $forward.HasExited) { Stop-Process -Id $forward.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
