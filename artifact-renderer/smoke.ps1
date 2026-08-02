param(
    [string]$Image = 'agenthub-artifact-renderer:test'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

docker build --file (Join-Path $repoRoot 'artifact-renderer/Dockerfile') --tag $Image $repoRoot
if ($LASTEXITCODE -ne 0) { throw 'artifact renderer image build failed' }

$version = docker run --rm --entrypoint soffice $Image --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'LibreOffice') {
    throw 'LibreOffice is not runnable in the artifact renderer image'
}

Write-Output "Artifact renderer container smoke test passed: $version"
