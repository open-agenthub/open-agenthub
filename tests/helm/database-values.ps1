param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Render([string[]]$Arguments) {
    $rendered = & helm template test (Join-Path $repoRoot 'helm/open-agenthub') --set postgres.password=test @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    return $rendered -join "`n"
}

# The backend's own defaults are also 20 and 10; the chart states them so an operator can see and size them.
$default = Render @()
if ($default -notmatch 'Postgres__MaxPoolSize: "20"') {
    throw 'the Postgres pool size is missing from the backend config'
}
if ($default -notmatch 'Postgres__LockPoolSize: "10"') {
    throw 'the Postgres lock pool size is missing from the backend config'
}
$sized = Render @('--set', 'backend.database.maxPoolSize=8', '--set', 'backend.database.lockPoolSize=3')
if ($sized -notmatch 'Postgres__MaxPoolSize: "8"') {
    throw 'backend.database.maxPoolSize was not honored'
}
if ($sized -notmatch 'Postgres__LockPoolSize: "3"') {
    throw 'backend.database.lockPoolSize was not honored'
}
# Values files from before the key existed must still render, with the default.
$older = Render @('--set', 'backend.database=null')
if ($older -notmatch 'Postgres__MaxPoolSize: "20"' -or $older -notmatch 'Postgres__LockPoolSize: "10"') {
    throw 'a values file without backend.database must fall back to 20 and 10'
}

# Initialization must not start or signal the image's temporary multi-process server.
if ($default -notmatch 'name: initialize-database' -or $default -notmatch 'postgres --single') {
    throw 'bundled database must initialize in single-user mode'
}
if ($default -match 'docker_temp_server_start|docker_temp_server_stop') {
    throw 'bundled initialization must not depend on temporary-server signals'
}
if ($default -match 'type: Unconfined') { throw 'database confinement must remain enabled by default' }
$unconfined = Render @('--set', 'postgres.appArmorUnconfined=true')
if (([regex]::Matches($unconfined, 'appArmorProfile: \{ type: Unconfined \}')).Count -ne 2) {
    throw 'explicit confinement workaround must apply to init and server containers only'
}
$ephemeral = Render @('--set', 'postgres.persistence=false')
if ($ephemeral -notmatch 'name: init' -or $ephemeral -notmatch 'emptyDir: \{\}') {
    throw 'ephemeral database must retain its initialization script and writable data volume'
}
Write-Output 'Postgres startup and pool size Helm value assertions passed.'
