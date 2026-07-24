param(
    [string]$Image = "open-agenthub-browser:test",
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$container = "agenthub-browser-smoke"

if ($Build) {
    docker build -t $Image $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw "Browser image build failed" }
}

try {
    docker run -d --rm --name $container --read-only `
        --tmpfs /data:rw,uid=1000,gid=1000 `
        --tmpfs /tmp:rw,uid=1000,gid=1000 `
        --tmpfs /dev/shm:rw,uid=1000,gid=1000 `
        -p 19222:9222 -p 16080:6080 -p 16081:6081 $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Browser container failed to start" }

    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        try { $version = Invoke-RestMethod http://127.0.0.1:19222/json/version -TimeoutSec 2 } catch { $version = $null }
    } until ($version.webSocketDebuggerUrl -or (Get-Date) -gt $deadline)
    if (-not $version.webSocketDebuggerUrl) { throw "CDP did not become ready" }

    $health = Invoke-WebRequest http://127.0.0.1:16081/healthz -UseBasicParsing
    if ($health.StatusCode -ne 200) { throw "Health endpoint failed" }

    $client = [Net.Sockets.TcpClient]::new("127.0.0.1", 16080)
    try {
        $stream = $client.GetStream()
        $request = "GET / HTTP/1.1`r`nHost: localhost`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==`r`nSec-WebSocket-Version: 13`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($request)
        $stream.Write($bytes, 0, $bytes.Length)
        $buffer = New-Object byte[] 1024
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $response = [Text.Encoding]::ASCII.GetString($buffer, 0, $count)
        if ($response -notmatch "101 Switching Protocols") { throw "RFB WebSocket upgrade failed" }
    } finally { $client.Dispose() }

    $uid = docker exec $container id -u
    if ($uid.Trim() -ne "1000") { throw "Container is not running as UID 1000" }
    docker exec $container sh -c "touch /root-write-test" 2>$null
    if ($LASTEXITCODE -eq 0) { throw "Read-only root filesystem check failed" }
    docker exec $container sh -c "touch /data/write-test && rm /data/write-test"
    if ($LASTEXITCODE -ne 0) { throw "Writable runtime mount check failed" }

    Write-Host "Browser runtime smoke test passed."
} finally {
    docker stop $container 2>$null | Out-Null
}
