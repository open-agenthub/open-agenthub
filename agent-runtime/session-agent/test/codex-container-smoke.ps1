param(
    [string]$Image = "open-agenthub-dev/agent-runtime-codex:test"
)

$ErrorActionPreference = "Stop"
$fixturePath = Join-Path $PSScriptRoot "fixtures/codex-container"

function Invoke-Docker {
    param([string[]]$Arguments)

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker exited with code $LASTEXITCODE"
    }
}

$version = & docker run --rm --entrypoint codex $Image --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'codex-cli 0\.144\.5') {
    throw "unexpected Codex CLI version: $version"
}

Invoke-Docker @(
    "run", "--rm", "--entrypoint", "sh", $Image, "-c",
    "test -x /usr/local/bin/node && test -x /usr/local/bin/codex"
)
Invoke-Docker @(
    "run", "--rm", "--entrypoint", "codex", $Image,
    "exec", "--sandbox", "workspace-write", "--json",
    "--dangerously-bypass-hook-trust", "resume", "--help"
)

$mount = "type=bind,source=$fixturePath,target=/fixtures,readonly"
$authFixture = Join-Path $fixturePath "subscription-auth.json"
$mcpFixture = Join-Path $fixturePath "mcp.json"
$authMount = "type=bind,source=$authFixture,target=/secrets/codex/auth.json,readonly"
$mcpMount = "type=bind,source=$mcpFixture,target=/secrets/mcp/mcp.json,readonly"
Invoke-Docker @(
    "run", "--rm", "--mount", $mount, "--mount", $authMount,
    "--mount", $mcpMount,
    "--entrypoint", "/fixtures/run-subscription.sh", $Image
)
Invoke-Docker @(
    "run", "--rm", "--mount", $mount,
    "-e", "CODEX_API_KEY=synthetic-codex-key",
    "--entrypoint", "/fixtures/run-api-key.sh", $Image
)

$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$missingKeyOutput = & docker @(
    "run", "--rm", "-e", "AGENTHUB_AUTH_MODE=apikey",
    "-e", "AGENTHUB_MODE=autonomous",
    "--entrypoint", "/usr/local/bin/entrypoint.sh", $Image
) 2>&1
$missingKeyExit = $LASTEXITCODE
$ErrorActionPreference = $previousErrorAction
if ($missingKeyExit -eq 0) {
    throw "API-key mode unexpectedly accepted a missing key"
}
$missingKeyText = $missingKeyOutput -join "`n"
if ($missingKeyText -notmatch 'CODEX_API_KEY is required' -or
    $missingKeyText -match 'device-auth') {
    throw "API-key mode did not fail closed: $missingKeyText"
}

Write-Output "Codex container smoke fixtures passed for $Image"
