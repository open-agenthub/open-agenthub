param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Render([string[]]$Arguments) {
    $rendered = & helm template test (Join-Path $repoRoot 'helm/open-agenthub') --set postgres.password=test @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    return $rendered -join "`n"
}

$default = Render @('--set', 'ingress.enabled=true', '--set', 'ingress.host=example.test')
if ($default -notmatch 'nginx.ingress.kubernetes.io/proxy-body-size: "55m"') {
    throw 'missing 55m ingress limit'
}
if ($default -match 'name: agenthub-artifact-renderer') {
    throw 'renderer must be disabled by default'
}
if ($default -notmatch 'Files__MaxDocumentBytes: "52428800"') {
    throw 'file limits are missing from the backend config'
}
if ($default -notmatch 'Files__MaxVideoBytes: "104857600"') {
    throw 'the video limit is missing from the backend config'
}
if ($default -notmatch 'Files__StreamTicketMinutes: "10"') {
    throw 'the video stream ticket lifetime is missing from the backend config'
}
$video = Render @('--set', 'files.maxVideoBytes=314572800', '--set', 'ingress.host=example.test')
if ($video -notmatch 'Files__MaxVideoBytes: "314572800"') {
    throw 'files.maxVideoBytes was not honored'
}
if ($video -notmatch 'Files__MaxImageBytes: "20971520"') {
    throw 'the video limit must not move the image limit'
}

$enabled = Render @('--set', 'files.officePreview.enabled=true', '--set', 'ingress.host=example.test')
if ($enabled -notmatch 'name: agenthub-artifact-renderer') { throw 'enabled renderer missing' }
if ($enabled -notmatch 'runAsNonRoot: true') { throw 'renderer must run non-root' }
if ($enabled -notmatch 'readOnlyRootFilesystem: true') { throw 'renderer root filesystem must be read-only' }
if ($enabled -notmatch 'Files__OfficePreview__Enabled: "true"') { throw 'renderer config missing' }
if ($enabled -notmatch 'policyTypes: \[Ingress, Egress\][\s\S]*egress: \[\]') {
    throw 'renderer must have no egress'
}

$override = Render @(
    '--set', 'ingress.host=example.test',
    '--set-string', 'ingress.annotations.nginx\.ingress\.kubernetes\.io/proxy-body-size=60m'
)
if ($override -notmatch 'nginx.ingress.kubernetes.io/proxy-body-size: "60m"') {
    throw 'custom ingress body size annotation was not honored'
}

Write-Output 'Session file Helm values and renderer isolation assertions passed.'
