param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Render([string[]]$Arguments) {
    $rendered = & helm template test (Join-Path $repoRoot 'helm/open-agenthub') --set postgres.password=test @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    return $rendered -join "`n"
}

# The backend's own default is also 20; the chart states it so an operator can see and size it.
$default = Render @()
if ($default -notmatch 'Postgres__MaxPoolSize: "20"') {
    throw 'the Postgres pool size is missing from the backend config'
}
$sized = Render @('--set', 'backend.database.maxPoolSize=8')
if ($sized -notmatch 'Postgres__MaxPoolSize: "8"') {
    throw 'backend.database.maxPoolSize was not honored'
}
# Values files from before the key existed must still render, with the default.
$older = Render @('--set', 'backend.database=null')
if ($older -notmatch 'Postgres__MaxPoolSize: "20"') {
    throw 'a values file without backend.database must fall back to 20'
}

Write-Output 'Postgres pool size Helm value assertions passed.'
